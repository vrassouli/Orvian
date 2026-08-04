using Orvian.Core.Hosts;

namespace Orvian.Security;

public readonly record struct SecretReferenceId(string Value)
{
    public static SecretReferenceId New() => new($"secret-{Guid.NewGuid():N}");

    public override string ToString() => Value;
}

public enum SecretKind
{
    SshPassword,
    PrivateKeyPassphrase
}

public sealed record SecretDescriptor(
    SecretReferenceId Id,
    SecretKind Kind,
    HostProfileId HostProfileId,
    string Label);

public sealed class SecretValue : IDisposable
{
    private char[]? _value;

    public SecretValue(ReadOnlySpan<char> value)
    {
        _value = value.ToArray();
    }

    public ReadOnlyMemory<char> Memory =>
        _value ?? throw new ObjectDisposedException(nameof(SecretValue));

    public void Dispose()
    {
        if (_value is null)
        {
            return;
        }

        Array.Clear(_value);
        _value = null;
    }
}

public interface ISecretStore
{
    Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    Task<SecretValue?> RetrieveAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default);
}
