using System.Runtime.InteropServices;
using System.Text;

namespace Orvian.Security;

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string operation, int status)
        : base($"The secure store could not {operation} the credential (status {status}).")
    {
        Status = status;
    }

    public int Status { get; }
}

public sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string ServiceName = "io.orvian.credentials";
    private readonly IMacKeychainApi _keychain;

    public MacOsKeychainSecretStore()
        : this(CreateNativeApi())
    {
    }

    internal MacOsKeychainSecretStore(IMacKeychainApi keychain)
    {
        _keychain = keychain ?? throw new ArgumentNullException(nameof(keychain));
    }

    public async Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SecretStoreValidation.Validate(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var secretBytes = SecretEncoding.EncodeUtf8(secret.Span);
        try
        {
            await Task.Run(
                    () => _keychain.Upsert(ServiceName, descriptor.Id.Value, secretBytes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(secretBytes);
        }
    }

    public async Task<SecretValue?> RetrieveAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();

        var secretBytes = await Task.Run(
                () => _keychain.Find(ServiceName, id.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return SecretEncoding.DecodeAndClear(secretBytes);
    }

    public async Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
                () => _keychain.Delete(ServiceName, id.Value),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IMacKeychainApi CreateNativeApi()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The macOS Keychain secret store is available only on macOS.");
        }

        return new NativeMacKeychainApi();
    }
}

internal interface IMacKeychainApi
{
    void Upsert(string service, string account, byte[] secret);

    byte[]? Find(string service, string account);

    void Delete(string service, string account);
}

internal sealed partial class NativeMacKeychainApi : IMacKeychainApi
{
    private const int Success = 0;
    private const int ItemNotFound = -25300;

    public void Upsert(string service, string account, byte[] secret)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        try
        {
            var findStatus = FindItem(
                serviceBytes,
                accountBytes,
                out var passwordLength,
                out var passwordData,
                out var item);
            try
            {
                if (findStatus == Success)
                {
                    var modifyStatus = NativeMethods.SecKeychainItemModifyAttributesAndData(
                        item,
                        IntPtr.Zero,
                        checked((uint)secret.Length),
                        secret);
                    ThrowIfFailure(modifyStatus, "update");
                    return;
                }

                if (findStatus != ItemNotFound)
                {
                    ThrowIfFailure(findStatus, "locate");
                }

                var addStatus = NativeMethods.SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    checked((uint)serviceBytes.Length),
                    serviceBytes,
                    checked((uint)accountBytes.Length),
                    accountBytes,
                    checked((uint)secret.Length),
                    secret,
                    out var addedItem);
                try
                {
                    ThrowIfFailure(addStatus, "store");
                }
                finally
                {
                    Release(addedItem);
                }
            }
            finally
            {
                FreePassword(passwordLength, passwordData);
                Release(item);
            }
        }
        finally
        {
            Array.Clear(serviceBytes);
            Array.Clear(accountBytes);
        }
    }

    public byte[]? Find(string service, string account)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        try
        {
            var status = FindItem(
                serviceBytes,
                accountBytes,
                out var passwordLength,
                out var passwordData,
                out var item);
            try
            {
                if (status == ItemNotFound)
                {
                    return null;
                }

                ThrowIfFailure(status, "retrieve");
                var value = new byte[checked((int)passwordLength)];
                if (value.Length > 0)
                {
                    Marshal.Copy(passwordData, value, 0, value.Length);
                }

                return value;
            }
            finally
            {
                FreePassword(passwordLength, passwordData);
                Release(item);
            }
        }
        finally
        {
            Array.Clear(serviceBytes);
            Array.Clear(accountBytes);
        }
    }

    public void Delete(string service, string account)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        try
        {
            var status = FindItem(
                serviceBytes,
                accountBytes,
                out var passwordLength,
                out var passwordData,
                out var item);
            try
            {
                if (status == ItemNotFound)
                {
                    return;
                }

                ThrowIfFailure(status, "locate");
                ThrowIfFailure(NativeMethods.SecKeychainItemDelete(item), "delete");
            }
            finally
            {
                FreePassword(passwordLength, passwordData);
                Release(item);
            }
        }
        finally
        {
            Array.Clear(serviceBytes);
            Array.Clear(accountBytes);
        }
    }

    private static int FindItem(
        byte[] service,
        byte[] account,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr item) =>
        NativeMethods.SecKeychainFindGenericPassword(
            IntPtr.Zero,
            checked((uint)service.Length),
            service,
            checked((uint)account.Length),
            account,
            out passwordLength,
            out passwordData,
            out item);

    private static void FreePassword(uint length, IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        if (length > 0)
        {
            var zeros = new byte[checked((int)length)];
            Marshal.Copy(zeros, 0, data, zeros.Length);
        }

        _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            NativeMethods.CFRelease(item);
        }
    }

    private static void ThrowIfFailure(int status, string operation)
    {
        if (status != Success)
        {
            throw new SecretStoreException(operation, status);
        }
    }

    private static partial class NativeMethods
    {
        private const string SecurityFramework =
            "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationFramework =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainFindGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            byte[] data);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport(SecurityFramework)]
        internal static partial int SecKeychainItemFreeContent(
            IntPtr attrList,
            IntPtr data);

        [LibraryImport(CoreFoundationFramework)]
        internal static partial void CFRelease(IntPtr value);
    }
}

internal static class SecretStoreValidation
{
    public static void Validate(SecretDescriptor descriptor)
    {
        Validate(descriptor.Id);
        if (descriptor.HostProfileId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(descriptor.Label))
        {
            throw new ArgumentException("Secret descriptor is invalid.", nameof(descriptor));
        }
    }

    public static void Validate(SecretReferenceId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Secret reference ID is required.", nameof(id));
        }
    }
}

internal static class SecretEncoding
{
    public static byte[] EncodeUtf8(
        ReadOnlySpan<char> value,
        int maximumBytes = int.MaxValue)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        if (length > maximumBytes)
        {
            throw new ArgumentException(
                "The credential exceeds the secure backend size limit.",
                nameof(value));
        }

        var bytes = new byte[length];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    public static SecretValue? DecodeAndClear(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var characters = new char[Encoding.UTF8.GetCharCount(bytes)];
            try
            {
                Encoding.UTF8.GetChars(bytes, characters);
                return new SecretValue(characters);
            }
            finally
            {
                Array.Clear(characters);
            }
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}
