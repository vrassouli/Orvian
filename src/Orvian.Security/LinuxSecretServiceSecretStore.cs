using System.Buffers;
using System.Diagnostics;

namespace Orvian.Security;

public sealed class LinuxSecretServiceSecretStore : ISecretStore
{
    private const int MaximumSecretBytes = 8191;
    private readonly ILinuxSecretServiceApi _secretService;

    public LinuxSecretServiceSecretStore()
        : this(CreateNativeApi())
    {
    }

    internal LinuxSecretServiceSecretStore(ILinuxSecretServiceApi secretService)
    {
        _secretService = secretService ??
            throw new ArgumentNullException(nameof(secretService));
    }

    public async Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SecretStoreValidation.Validate(descriptor);
        var bytes = SecretEncoding.EncodeUtf8(secret.Span, MaximumSecretBytes);
        try
        {
            await _secretService.StoreAsync(
                    descriptor.Id.Value,
                    "Orvian credential",
                    bytes,
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
        var bytes = await _secretService
            .LookupAsync(id.Value, cancellationToken)
            .ConfigureAwait(false);
        return SecretEncoding.DecodeAndClear(bytes);
    }

    public Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        return _secretService.ClearAsync(id.Value, cancellationToken);
    }

    internal static bool TryCreate(out ISecretStore store)
    {
        if (OperatingSystem.IsLinux() &&
            NativeLinuxSecretServiceApi.TryFindExecutable(out var executable))
        {
            store = new LinuxSecretServiceSecretStore(
                new NativeLinuxSecretServiceApi(executable));
            return true;
        }

        store = new UnavailableSecretStore();
        return false;
    }

    private static ILinuxSecretServiceApi CreateNativeApi()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Secret Service credential storage is available only on Linux.");
        }

        if (!NativeLinuxSecretServiceApi.TryFindExecutable(out var executable))
        {
            throw new PlatformNotSupportedException(
                "Secret Service requires the trusted secret-tool executable.");
        }

        return new NativeLinuxSecretServiceApi(executable);
    }
}

internal interface ILinuxSecretServiceApi
{
    Task StoreAsync(
        string reference,
        string label,
        byte[] secret,
        CancellationToken cancellationToken);

    Task<byte[]?> LookupAsync(
        string reference,
        CancellationToken cancellationToken);

    Task ClearAsync(
        string reference,
        CancellationToken cancellationToken);
}

internal sealed class NativeLinuxSecretServiceApi(string executable)
    : ILinuxSecretServiceApi
{
    private const int MaximumOutputBytes = 8191;
    private const int MaximumDiagnosticBytes = 4096;
    private static readonly string[] TrustedExecutablePaths =
    [
        "/usr/bin/secret-tool",
        "/bin/secret-tool",
        "/usr/local/bin/secret-tool"
    ];

    public async Task StoreAsync(
        string reference,
        string label,
        byte[] secret,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
                ["store", $"--label={label}", "--",
                    "application", "io.orvian.credentials",
                    "reference", reference],
                secret,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false);
        Clear(result.Output);
    }

    public async Task<byte[]?> LookupAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
                ["lookup", "--", "application", "io.orvian.credentials",
                    "reference", reference],
                standardInput: null,
                allowNotFound: true,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Output;
    }

    public async Task ClearAsync(
        string reference,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
                ["clear", "--", "application", "io.orvian.credentials",
                    "reference", reference],
                standardInput: null,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false);
        Clear(result.Output);
    }

    internal static bool TryFindExecutable(out string executable)
    {
        executable = TrustedExecutablePaths.FirstOrDefault(File.Exists) ??
            string.Empty;
        return executable.Length > 0;
    }

    private async Task<ProcessResult> ExecuteAsync(
        IEnumerable<string> arguments,
        byte[]? standardInput,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new SecretStoreException("start Secret Service", -1);
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                InvalidOperationException)
        {
            throw new SecretStoreException("start Secret Service", -1);
        }

        try
        {
            var outputTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                MaximumOutputBytes,
                cancellationToken);
            var diagnosticTask = ReadBoundedAsync(
                process.StandardError.BaseStream,
                MaximumDiagnosticBytes,
                cancellationToken);
            if (standardInput is not null)
            {
                await process.StandardInput.BaseStream
                    .WriteAsync(standardInput, cancellationToken)
                    .ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var diagnostics = await diagnosticTask.ConfigureAwait(false);
            try
            {
                if (process.ExitCode == 0)
                {
                    return new(output);
                }

                if (allowNotFound &&
                    process.ExitCode == 1 &&
                    diagnostics.Length == 0)
                {
                    Array.Clear(output);
                    return new(null);
                }

                Array.Clear(output);
                throw new SecretStoreException(
                    "access Secret Service",
                    process.ExitCode);
            }
            finally
            {
                Array.Clear(diagnostics);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var count = 0;
            while (count <= limit)
            {
                var read = await stream
                    .ReadAsync(rented.AsMemory(count, limit + 1 - count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return rented.AsSpan(0, count).ToArray();
                }

                count += read;
            }

            throw new SecretStoreException("read Secret Service", -1);
        }
        finally
        {
            Array.Clear(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            Array.Clear(value);
        }
    }

    private sealed record ProcessResult(byte[]? Output = null);
}
