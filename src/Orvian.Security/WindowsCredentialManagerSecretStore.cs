using System.Runtime.InteropServices;
using System.Text;

namespace Orvian.Security;

public sealed class WindowsCredentialManagerSecretStore : ISecretStore
{
    private const string TargetPrefix = "Orvian/";
    private const int MaximumCredentialBytes = 2560;
    private readonly IWindowsCredentialApi _credentials;

    public WindowsCredentialManagerSecretStore()
        : this(CreateNativeApi())
    {
    }

    internal WindowsCredentialManagerSecretStore(IWindowsCredentialApi credentials)
    {
        _credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
    }

    public async Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SecretStoreValidation.Validate(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = SecretEncoding.EncodeUtf8(secret.Span, MaximumCredentialBytes);
        try
        {
            await Task.Run(
                    () => _credentials.Upsert(Target(descriptor.Id), bytes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public async Task<SecretValue?> RetrieveAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await Task.Run(
                () => _credentials.Find(Target(id)),
                cancellationToken)
            .ConfigureAwait(false);
        return SecretEncoding.DecodeAndClear(bytes);
    }

    public async Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
                () => _credentials.Delete(Target(id)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Target(SecretReferenceId id) => TargetPrefix + id.Value;

    private static IWindowsCredentialApi CreateNativeApi()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager is available only on Windows.");
        }

        return new NativeWindowsCredentialApi();
    }
}

internal interface IWindowsCredentialApi
{
    void Upsert(string target, byte[] secret);

    byte[]? Find(string target);

    void Delete(string target);
}

internal sealed partial class NativeWindowsCredentialApi : IWindowsCredentialApi
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Upsert(string target, byte[] secret)
    {
        var secretPointer = secret.Length == 0
            ? IntPtr.Zero
            : Marshal.AllocHGlobal(secret.Length);
        var targetPointer = Marshal.StringToHGlobalUni(target);
        var userPointer = Marshal.StringToHGlobalUni("Orvian");
        var credentialPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<NativeCredential>());
        try
        {
            if (secret.Length > 0)
            {
                Marshal.Copy(secret, 0, secretPointer, secret.Length);
            }

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userPointer
            };
            Marshal.StructureToPtr(credential, credentialPointer, fDeleteOld: false);
            if (!NativeMethods.CredWrite(credentialPointer, 0))
            {
                ThrowLastError("store");
            }
        }
        finally
        {
            ZeroAndFree(secretPointer, secret.Length);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userPointer);
            Marshal.FreeHGlobal(credentialPointer);
        }
    }

    public byte[]? Find(string target)
    {
        if (!NativeMethods.CredRead(
                target,
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var status = Marshal.GetLastPInvokeError();
            if (status == ErrorNotFound)
            {
                return null;
            }

            throw new SecretStoreException("retrieve", status);
        }

        NativeCredential? credential = null;
        try
        {
            credential = Marshal.PtrToStructure<NativeCredential>(
                credentialPointer);
            var value = new byte[checked((int)credential.Value.CredentialBlobSize)];
            if (value.Length > 0)
            {
                Marshal.Copy(
                    credential.Value.CredentialBlob,
                    value,
                    0,
                    value.Length);
            }

            return value;
        }
        finally
        {
            if (credential is { CredentialBlob: not 0, CredentialBlobSize: > 0 })
            {
                Marshal.Copy(
                    new byte[checked((int)credential.Value.CredentialBlobSize)],
                    0,
                    credential.Value.CredentialBlob,
                    checked((int)credential.Value.CredentialBlobSize));
            }

            NativeMethods.CredFree(credentialPointer);
        }
    }

    public void Delete(string target)
    {
        if (NativeMethods.CredDelete(target, CredentialTypeGeneric, 0))
        {
            return;
        }

        var status = Marshal.GetLastPInvokeError();
        if (status != ErrorNotFound)
        {
            throw new SecretStoreException("delete", status);
        }
    }

    private static void ThrowLastError(string operation) =>
        throw new SecretStoreException(operation, Marshal.GetLastPInvokeError());

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        if (length > 0)
        {
            Marshal.Copy(new byte[length], 0, pointer, length);
        }

        Marshal.FreeHGlobal(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            "advapi32.dll",
            EntryPoint = "CredWriteW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWrite(
            IntPtr credential,
            uint flags);

        [LibraryImport(
            "advapi32.dll",
            EntryPoint = "CredReadW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(
            string target,
            uint type,
            uint reservedFlag,
            out IntPtr credential);

        [LibraryImport(
            "advapi32.dll",
            EntryPoint = "CredDeleteW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(
            string target,
            uint type,
            uint flags);

        [LibraryImport("advapi32.dll")]
        internal static partial void CredFree(IntPtr buffer);
    }
}
