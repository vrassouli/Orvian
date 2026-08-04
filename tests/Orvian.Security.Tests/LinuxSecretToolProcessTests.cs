using System.Text;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class LinuxSecretToolProcessTests
{
    [Fact]
    public async Task StructuredProcessProtocolPreservesExactStdinAndStdout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"orvian-secret-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "secret-tool");
        var state = Path.Combine(directory, "stored-secret");
        try
        {
            await File.WriteAllTextAsync(
                executable,
                $"""
                #!/bin/sh
                case "$1" in
                  store)
                    /bin/cat > "{state}"
                    ;;
                  lookup)
                    if [ -f "{state}" ]; then
                      /bin/cat "{state}"
                    else
                      exit 1
                    fi
                    ;;
                  clear)
                    /bin/rm -f "{state}"
                    ;;
                  *)
                    exit 2
                    ;;
                esac
                """);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            var api = new NativeLinuxSecretServiceApi(executable);
            var expected = Encoding.UTF8.GetBytes("first line\npässword\n");

            await api.StoreAsync(
                "secret-process-test",
                "Orvian credential",
                expected,
                CancellationToken.None);
            var retrieved = await api.LookupAsync(
                "secret-process-test",
                CancellationToken.None);

            Assert.Equal(expected, retrieved);
            await api.ClearAsync(
                "secret-process-test",
                CancellationToken.None);
            Assert.Null(await api.LookupAsync(
                "secret-process-test",
                CancellationToken.None));
            Array.Clear(expected);
            Array.Clear(retrieved!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
