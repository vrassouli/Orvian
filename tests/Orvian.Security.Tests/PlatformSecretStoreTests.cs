using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class PlatformSecretStoreTests
{
    [Fact]
    public void FactorySelectsOnlyNativeSecureOrExplicitUnavailableStore()
    {
        var store = PlatformSecretStore.Create();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialManagerSecretStore>(store);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacOsKeychainSecretStore>(store);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.True(
                store is LinuxSecretServiceSecretStore or UnavailableSecretStore);
        }
        else
        {
            Assert.IsType<UnavailableSecretStore>(store);
        }
    }

    [Fact]
    public void PlatformSpecificConstructorsFailClosedOnWrongPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new WindowsCredentialManagerSecretStore());
        }

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new MacOsKeychainSecretStore());
        }

        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => new LinuxSecretServiceSecretStore());
        }
    }
}
