using Orvian.Application.Settings;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class ApplicationSettingsTests
{
    [Fact]
    public void DefaultsAreConservativeAndValid()
    {
        ApplicationSettings.Validate(ApplicationSettings.Default);

        Assert.True(ApplicationSettings.Default.ClearSessionCredentialsOnDisconnect);
        Assert.True(
            ApplicationSettings.Default.OutputRetentionDays <
            ApplicationSettings.Default.AuditRetentionDays);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(91, 90)]
    [InlineData(30, 0)]
    public void InvalidRetentionPolicyIsRejected(int outputDays, int auditDays)
    {
        var settings = ApplicationSettings.Default with
        {
            OutputRetentionDays = outputDays,
            AuditRetentionDays = auditDays
        };

        Assert.Throws<ArgumentException>(() => ApplicationSettings.Validate(settings));
    }

    [Fact]
    public void InvalidCultureIsRejected()
    {
        var settings = ApplicationSettings.Default with
        {
            CultureName = "not-a-real-culture!"
        };

        Assert.Throws<ArgumentException>(() => ApplicationSettings.Validate(settings));
    }
}
