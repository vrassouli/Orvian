namespace Orvian.Security;

public static class PlatformSecretStore
{
    public static ISecretStore Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialManagerSecretStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainSecretStore();
        }

        if (OperatingSystem.IsLinux() &&
            LinuxSecretServiceSecretStore.TryCreate(out var linuxStore))
        {
            return linuxStore;
        }

        return new UnavailableSecretStore();
    }
}
