using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Ssh.Tests;

public sealed class PosixArgumentEncoderTests
{
    [Theory]
    [InlineData("simple", "'simple'")]
    [InlineData("", "''")]
    [InlineData("two words", "'two words'")]
    [InlineData("$(touch /tmp/pwned)", "'$(touch /tmp/pwned)'")]
    [InlineData("a; rm -rf /", "'a; rm -rf /'")]
    [InlineData("single'quote", "'single'\"'\"'quote'")]
    [InlineData("line\nbreak", "'line\nbreak'")]
    public void Argument_is_encoded_as_one_posix_word(string argument, string expected)
    {
        var encoded = PosixArgumentEncoder.Encode(
            Request("printf", new CommandArgument(argument)));

        Assert.Equal($"printf {expected}", encoded);
    }

    [Theory]
    [InlineData("echo value")]
    [InlineData("echo;id")]
    [InlineData("$(id)")]
    [InlineData("echo\nid")]
    public void Executable_shell_syntax_is_rejected(string executable)
    {
        Assert.Throws<ArgumentException>(
            () => PosixArgumentEncoder.Encode(Request(executable)));
    }

    [Fact]
    public void Arguments_remain_ordered_and_separate()
    {
        var encoded = PosixArgumentEncoder.Encode(
            Request("systemctl", new("restart"), new("nginx.service")));

        Assert.Equal("systemctl 'restart' 'nginx.service'", encoded);
    }

    private static CommandRequest Request(
        string executable,
        params CommandArgument[] arguments) =>
        new()
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.test",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = executable,
            Arguments = [.. arguments]
        };
}
