using System.Collections.Immutable;
using System.Globalization;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.DateTime;

public sealed class DateTimePlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.datetime",
        "Date & Time",
        new Version(0, 1, 0),
        "Remotune",
        "Inspect remote date, time, timezone, and synchronization state.",
        new Version(0, 1, 0),
        [
            PluginPermissions.UiNavigationContribute,
            PluginPermissions.CommandReadExecute,
            PluginPermissions.CommandMutateExecute,
            PluginPermissions.PrivilegeElevatedRequest
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/date-time",
            "Date & Time",
            "clock",
            typeof(DateTimePageViewModel),
            "date-time");
        builder.Features.Add(new SystemdDateTimeProvider());
        builder.Features.Add(new PosixDateTimeProvider());
        builder.Features.Add(new SystemdTimezoneMutationProvider());
        builder.Features.Add(new SystemdDateTimeMutationProvider());
    }
}

public sealed class SystemdDateTimeMutationProvider : IMutationFeatureProvider
{
    private const string InputFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "time.datetime.write");

    public string FeatureId => "date-time";

    public string MutationId => "datetime.change";

    public string ProviderId => "date-time.systemd.datetime";

    public int Priority => 100;

    public string Title => "Set date & time";

    public string Purpose => "Set the remote system date and local time.";

    public string ParameterName => "dateTime";

    public string ParameterLabel => "YYYY-MM-DD HH:mm:ss (remote local time)";

    public PluginMutationRisk Risk => PluginMutationRisk.Elevated;

    public bool RequiresElevation => true;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginMutationRequest CreateRequest(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(ParameterName, out var value) ||
            !System.DateTime.TryParseExact(
                value,
                InputFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) ||
            !string.Equals(
                parsed.ToString(InputFormat, CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Date and time must use YYYY-MM-DD HH:mm:ss.",
                nameof(parameters));
        }

        return new(
            Title,
            $"Set the remote date and time to {value}.",
            Risk,
            "timedatectl",
            [new("set-time"), new(value)],
            TimeSpan.FromSeconds(15));
    }
}

public sealed class DateTimePageViewModel;

public sealed class SystemdTimezoneMutationProvider : IMutationFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "time.timezone.write");

    public string FeatureId => "date-time";

    public string MutationId => "timezone.change";

    public string ProviderId => "date-time.systemd.timezone";

    public int Priority => 100;

    public string Title => "Change timezone";

    public string Purpose => "Set the remote system timezone.";

    public string ParameterName => "timezone";

    public string ParameterLabel => "IANA timezone (for example, Europe/Berlin)";

    public PluginMutationRisk Risk => PluginMutationRisk.Elevated;

    public bool RequiresElevation => true;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginMutationRequest CreateRequest(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(ParameterName, out var timezone) ||
            !IsValidTimezone(timezone))
        {
            throw new ArgumentException(
                "Timezone must be a valid IANA-style identifier.",
                nameof(parameters));
        }

        return new(
            Title,
            $"Change the remote timezone to {timezone}.",
            Risk,
            "timedatectl",
            [new("set-timezone"), new(timezone)],
            TimeSpan.FromSeconds(15));
    }

    private static bool IsValidTimezone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value[0] == '/' ||
            value[^1] == '/')
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length >= 2 &&
            segments.All(segment =>
                segment.Length > 0 &&
                segment is not ("." or "..") &&
                segment.All(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '_' or '-' or '+'));
    }
}

public sealed class SystemdDateTimeProvider : IReadOnlyFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "time.systemd");

    public string FeatureId => "date-time";

    public string ProviderId => "date-time.systemd";

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginReadRequest CreateRequest() =>
        new(
            "timedatectl",
            [
                new("show"),
                new("--property=Timezone"),
                new("--property=NTP"),
                new("--property=NTPSynchronized"),
                new("--property=LocalRTC"),
                new("--property=TimeUSec")
            ],
            TimeSpan.FromSeconds(10));

    public PluginFeatureParseResult Parse(PluginCommandOutput output)
    {
        if (output.IsTruncated)
        {
            return PluginFeatureParseResult.Failure(
                "Date and time output was truncated.");
        }

        if (output.ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "timedatectl returned a nonzero exit code.");
        }

        if (output.StandardOutput.Length > 32 * 1024 ||
            output.StandardOutput.Contains('\0'))
        {
            return PluginFeatureParseResult.Failure(
                "timedatectl returned malformed output.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                return PluginFeatureParseResult.Failure(
                    "timedatectl returned an unexpected field.");
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!KnownKeys.Contains(key) || !values.TryAdd(key, value))
            {
                return PluginFeatureParseResult.Failure(
                    "timedatectl returned duplicate or unknown fields.");
            }
        }

        if (!values.ContainsKey("Timezone") || !values.ContainsKey("TimeUSec"))
        {
            return PluginFeatureParseResult.Failure(
                "timedatectl omitted required date and timezone fields.");
        }

        return new(new PluginFeatureReadModel(
            values.ToImmutableDictionary(StringComparer.Ordinal)));
    }

    private static IReadOnlySet<string> KnownKeys { get; } =
        new HashSet<string>(
            ["Timezone", "NTP", "NTPSynchronized", "LocalRTC", "TimeUSec"],
            StringComparer.Ordinal);
}

public sealed class PosixDateTimeProvider : IReadOnlyFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "time.read");

    public string FeatureId => "date-time";

    public string ProviderId => "date-time.posix";

    public int Priority => 10;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginReadRequest CreateRequest() =>
        new(
            "date",
            [new("+%Y-%m-%dT%H:%M:%S%z")],
            TimeSpan.FromSeconds(5));

    public PluginFeatureParseResult Parse(PluginCommandOutput output)
    {
        if (output.IsTruncated || output.ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "The POSIX date command did not return complete output.");
        }

        var value = output.StandardOutput.Trim();
        if (value.Length != 24 || value.Contains('\0') ||
            value[^5] is not ('+' or '-') ||
            !value[^4..].All(char.IsAsciiDigit))
        {
            return PluginFeatureParseResult.Failure(
                "The POSIX date command returned malformed output.");
        }

        var normalized = value.Insert(value.Length - 2, ":");
        if (!DateTimeOffset.TryParseExact(
                normalized,
                "yyyy-MM-dd'T'HH:mm:sszzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return PluginFeatureParseResult.Failure(
                "The POSIX date command returned an invalid timestamp.");
        }

        return new(new PluginFeatureReadModel(
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [
                    KeyValuePair.Create(
                        "TimeUSec",
                        parsed.ToString("O", CultureInfo.InvariantCulture)),
                    KeyValuePair.Create(
                        "Timezone",
                        $"UTC{parsed:zzz}"),
                    KeyValuePair.Create(
                        "Synchronization",
                        "Unavailable with the POSIX provider")
                ])));
    }
}
