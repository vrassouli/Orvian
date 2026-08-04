using Orvian.Plugin.Abstractions;
using Orvian.Plugins.DateTime;
using Xunit;

namespace Orvian.Plugins.DateTime.Tests;

public sealed class DateTimeProviderTests
{
    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/GMT+3")]
    public void TimezoneMutationUsesValidatedStructuredArgument(string timezone)
    {
        var request = new SystemdTimezoneMutationProvider().CreateRequest(
            new Dictionary<string, string> { ["timezone"] = timezone });

        Assert.Equal("timedatectl", request.Executable);
        Assert.Equal(
            ["set-timezone", timezone],
            request.Arguments.Select(argument => argument.Value));
        Assert.Equal(PluginMutationRisk.Elevated, request.Risk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UTC")]
    [InlineData("../etc/passwd")]
    [InlineData("/Europe/Berlin")]
    [InlineData("Europe//Berlin")]
    [InlineData("Europe/Berlin;reboot")]
    public void TimezoneMutationRejectsInvalidIdentifiers(string timezone)
    {
        Assert.Throws<ArgumentException>(
            () => new SystemdTimezoneMutationProvider().CreateRequest(
                new Dictionary<string, string> { ["timezone"] = timezone }));
    }

    [Theory]
    [InlineData("2026-08-02 17:30:00")]
    [InlineData("2028-02-29 00:00:01")]
    public void DateTimeMutationUsesValidatedStructuredArgument(string dateTime)
    {
        var request = new SystemdDateTimeMutationProvider().CreateRequest(
            new Dictionary<string, string> { ["dateTime"] = dateTime });

        Assert.Equal("timedatectl", request.Executable);
        Assert.Equal(
            ["set-time", dateTime],
            request.Arguments.Select(argument => argument.Value));
        Assert.Equal(PluginMutationRisk.Elevated, request.Risk);
        Assert.True(new SystemdDateTimeMutationProvider().RequiresElevation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-08-02")]
    [InlineData("2026-8-2 17:30:00")]
    [InlineData("2026-02-29 17:30:00")]
    [InlineData("2026-08-02T17:30:00")]
    [InlineData("2026-08-02 17:30:00; reboot")]
    public void DateTimeMutationRejectsInvalidOrNonCanonicalValues(string dateTime)
    {
        Assert.Throws<ArgumentException>(
            () => new SystemdDateTimeMutationProvider().CreateRequest(
                new Dictionary<string, string> { ["dateTime"] = dateTime }));
    }

    [Fact]
    public void SystemdProviderBuildsStructuredReadOnlyRequest()
    {
        var request = new SystemdDateTimeProvider().CreateRequest();

        Assert.Equal("timedatectl", request.Executable);
        Assert.Equal("show", request.Arguments[0].Value);
        Assert.All(request.Arguments, argument => Assert.False(argument.IsSensitive));
    }

    [Fact]
    public void SystemdProviderParsesMachineFields()
    {
        var result = new SystemdDateTimeProvider().Parse(new(
            0,
            """
            Timezone=Europe/Berlin
            NTP=yes
            NTPSynchronized=yes
            LocalRTC=no
            TimeUSec=Wed 2026-07-29 12:30:00 CEST

            """,
            string.Empty,
            false));

        Assert.True(result.IsSuccess);
        Assert.Equal("Europe/Berlin", result.Model!.Values["Timezone"]);
        Assert.Equal("yes", result.Model.Values["NTPSynchronized"]);
    }

    [Theory]
    [InlineData("Timezone=UTC\nTimezone=Europe/Berlin\nTimeUSec=value")]
    [InlineData("Timezone=UTC\nUnknown=value\nTimeUSec=value")]
    [InlineData("Timezone=UTC")]
    [InlineData("Timezone\0=UTC\nTimeUSec=value")]
    public void SystemdProviderRejectsMalformedOutput(string output)
    {
        var result = new SystemdDateTimeProvider().Parse(
            new(0, output, string.Empty, false));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
    }

    [Fact]
    public void PosixProviderParsesInvariantTimestamp()
    {
        var result = new PosixDateTimeProvider().Parse(
            new(0, "2026-07-29T12:30:00+0330\n", string.Empty, false));

        Assert.True(result.IsSuccess);
        Assert.Equal("UTC+03:30", result.Model!.Values["Timezone"]);
    }

    [Theory]
    [InlineData("Wed Jul 29 12:30:00")]
    [InlineData("2026-07-29T12:30:00+2500")]
    [InlineData("")]
    public void PosixProviderRejectsLocalizedOrInvalidOutput(string output)
    {
        var result = new PosixDateTimeProvider().Parse(
            new(0, output, string.Empty, false));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ProvidersRejectTruncatedOutput()
    {
        Assert.False(new SystemdDateTimeProvider().Parse(
            new(0, "Timezone=UTC\nTimeUSec=value", string.Empty, true)).IsSuccess);
        Assert.False(new PosixDateTimeProvider().Parse(
            new(0, "2026-07-29T12:30:00+0000", string.Empty, true)).IsSuccess);
    }
}
