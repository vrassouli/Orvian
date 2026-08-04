using System.Collections.Immutable;
using Orvian.Diagnostics;
using Xunit;

namespace Orvian.Diagnostics.Tests;

public sealed class RollingJsonDiagnosticLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"orvian-diagnostics-{Guid.NewGuid():N}");

    [Fact]
    public async Task WritesStructuredEventAndDefensivelyRedactsSecrets()
    {
        await using var log = new RollingJsonDiagnosticLog(_directory);
        var rawSecret = "highly-sensitive-value";
        var written = await log.TryWriteAsync(Event(
            "Startup failed password=highly-sensitive-value",
            ImmutableDictionary<string, string>.Empty
                .Add("password", rawSecret)
                .Add("endpoint", "https://user:highly-sensitive-value@example.test")));

        var item = Assert.Single(await log.QueryAsync());
        var raw = await File.ReadAllTextAsync(
            Path.Combine(_directory, "orvian-diagnostics.jsonl"));

        Assert.True(written);
        Assert.DoesNotContain(rawSecret, raw, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", item.SafeMessage);
        Assert.Equal("[REDACTED]", item.SafeProperties["password"]);
        Assert.DoesNotContain(rawSecret, item.SafeProperties["endpoint"]);
    }

    [Fact]
    public async Task RotationIsBoundedAndQueriesNewestFirst()
    {
        await using var log = new RollingJsonDiagnosticLog(
            _directory,
            maximumFileBytes: 1024,
            retainedFiles: 2);
        for (var index = 0; index < 30; index++)
        {
            await log.TryWriteAsync(Event($"Event {index} {new string('x', 120)}") with
            {
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(index)
            });
        }

        var files = Directory.GetFiles(_directory);
        var events = await log.QueryAsync(limit: 5);

        Assert.True(files.Length <= 3);
        Assert.Equal(5, events.Count);
        Assert.True(events.SequenceEqual(events.OrderByDescending(item => item.OccurredAt)));
    }

    [Fact]
    public async Task CorruptLinesAreSkippedWithoutLosingValidDiagnostics()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "orvian-diagnostics.jsonl"),
            "not-json" + Environment.NewLine);
        await using var log = new RollingJsonDiagnosticLog(_directory);
        await log.TryWriteAsync(Event("Valid event"));

        var item = Assert.Single(await log.QueryAsync());

        Assert.Equal("Valid event", item.SafeMessage);
    }

    [Fact]
    public async Task IoFailureDoesNotCrashCaller()
    {
        Directory.CreateDirectory(_directory);
        var blockingFile = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        await using var log = new RollingJsonDiagnosticLog(
            Path.Combine(blockingFile, "child"));

        Assert.False(await log.TryWriteAsync(Event("Cannot write")));
    }

    [Fact]
    public async Task QueryLimitIsBounded()
    {
        await using var log = new RollingJsonDiagnosticLog(_directory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.QueryAsync(limit: 501));
    }

    [Fact]
    public async Task SingleEventCannotExceedConfiguredFileBound()
    {
        await using var log = new RollingJsonDiagnosticLog(
            _directory,
            maximumFileBytes: 1024);

        Assert.False(await log.TryWriteAsync(Event(new string('x', 4096))));
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SafeDiagnosticEvent Event(
        string message,
        ImmutableDictionary<string, string>? properties = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DiagnosticSeverity.Error,
            "startup",
            "startup.failed",
            message,
            properties ?? ImmutableDictionary<string, string>.Empty);
}
