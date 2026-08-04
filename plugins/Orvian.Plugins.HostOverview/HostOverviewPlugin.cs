using System.Collections.Immutable;
using System.Globalization;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.HostOverview;

public sealed class HostOverviewPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.host-overview",
        "Host Overview",
        new Version(0, 1, 0),
        "Remotune",
        "Present trusted local host identity, connection, and discovery context.",
        new Version(0, 1, 0),
        [
            PluginPermissions.HostRead,
            PluginPermissions.UiNavigationContribute
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/host-overview",
            "Host Overview",
            "server",
            typeof(HostOverviewPageViewModel),
            "host-overview");
        builder.Features.Add(new HostOverviewProvider());
    }
}

public sealed class HostOverviewPageViewModel;

public sealed class HostOverviewProvider : ILocalHostFeatureProvider
{
    public string FeatureId => "host-overview";

    public string ProviderId => "host-overview.core-context";

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities =>
        ImmutableHashSet<string>.Empty;

    public PluginFeatureParseResult Build(LocalHostFeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.Ordinal);
        values["display_name"] = context.DisplayName;
        values["endpoint"] = context.Endpoint;
        values["connection_state"] = context.ConnectionState;
        values["trusted_identity"] = BuildTrustedIdentity(context);
        values["operating_system"] = GetFact(
            context,
            "os.pretty_name",
            context.OperatingSystem);
        values["remote_hostname"] = GetFact(context, "host.name");
        values["kernel"] = GetFact(context, "kernel.release");
        values["architecture"] = GetFact(context, "kernel.arch");
        values["uptime"] = BuildUptime(context);
        values["discovery"] = BuildDiscoveryState(context);
        values["privilege_provider"] = BuildPrivilegeProvider(context);
        values["active_providers"] = BuildActiveProviders(context);
        values["capabilities"] = context.Capabilities.Count == 0
            ? "None discovered"
            : string.Join(", ", context.Capabilities.Order(StringComparer.Ordinal));
        return new(new PluginFeatureReadModel(values.ToImmutable()));
    }

    private static string BuildTrustedIdentity(LocalHostFeatureContext context) =>
        context.TrustedHostKeyAlgorithm is null ||
        context.TrustedHostKeyFingerprint is null
            ? "Not trusted yet"
            : $"{context.TrustedHostKeyAlgorithm} · {context.TrustedHostKeyFingerprint}";

    private static string BuildDiscoveryState(LocalHostFeatureContext context)
    {
        if (context.DiscoveryCompletedAt is null)
        {
            return "Not available";
        }

        var freshness = IsConnected(context.ConnectionState) ? "Current" : "Cached · stale";
        var completeness = context.IsDiscoveryPartial ? "partial" : "complete";
        return $"{freshness} · {completeness} · " +
            context.DiscoveryCompletedAt.Value.ToUniversalTime()
                .ToString("u", CultureInfo.InvariantCulture);
    }

    private static string BuildUptime(LocalHostFeatureContext context)
    {
        var rawBootTime = GetOptionalFact(context, "system.boot_time");
        if (rawBootTime is null ||
            !DateTimeOffset.TryParse(
                rawBootTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var bootTime) ||
            bootTime > context.GeneratedAt)
        {
            return "Unavailable";
        }

        var uptime = context.GeneratedAt - bootTime;
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{Math.Max(0, uptime.Minutes)}m";
    }

    private static string BuildPrivilegeProvider(LocalHostFeatureContext context) =>
        context.Capabilities.Contains("privilege.root")
            ? "Direct root"
            : context.Capabilities.Contains("privilege.sudo")
                ? "sudo"
                : context.Capabilities.Contains("privilege.doas")
                    ? "doas"
                    : "None detected";

    private static string BuildActiveProviders(LocalHostFeatureContext context)
    {
        var providers = new List<string>();
        AddProvider(providers, "Init", GetOptionalFact(context, "init.system"));
        AddProvider(
            providers,
            "Packages",
            GetOptionalFact(context, "package.provider"));
        AddProvider(
            providers,
            "Timezone",
            context.Capabilities.Contains("time.systemd")
                ? "systemd/timedatectl"
                : context.Capabilities.Contains("time.read")
                    ? "POSIX"
                    : null);
        return providers.Count == 0
            ? "No provider selection available"
            : string.Join(" · ", providers);
    }

    private static void AddProvider(
        ICollection<string> providers,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            providers.Add($"{label}: {value}");
        }
    }

    private static string GetFact(
        LocalHostFeatureContext context,
        string key,
        string fallback = "Unavailable") =>
        context.Facts.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static string? GetOptionalFact(
        LocalHostFeatureContext context,
        string key) =>
        context.Facts.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsConnected(string state) =>
        state is "Connected" or "Discovering" or "Ready";
}
