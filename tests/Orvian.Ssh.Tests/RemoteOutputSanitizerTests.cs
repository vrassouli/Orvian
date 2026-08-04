using Xunit;

namespace Orvian.Ssh.Tests;

public sealed class RemoteOutputSanitizerTests
{
    [Theory]
    [InlineData("\u001b[31mfailed\u001b[0m", "failed")]
    [InlineData("\u001b]0;malicious title\aoutput", "output")]
    [InlineData("safe\0text", "safetext")]
    [InlineData("left\u202Etxt.exe", "lefttxt.exe")]
    [InlineData("one\ntwo\tvalue", "one\ntwo\tvalue")]
    public void Dangerous_presentation_controls_are_removed(
        string input,
        string expected)
    {
        Assert.Equal(expected, RemoteOutputSanitizer.Sanitize(input));
    }

    [Fact]
    public void Unterminated_escape_sequence_does_not_leak_payload()
    {
        Assert.Equal("prefix", RemoteOutputSanitizer.Sanitize("prefix\u001b[31"));
    }
}
