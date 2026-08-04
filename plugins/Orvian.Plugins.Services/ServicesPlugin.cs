using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.Services;

public sealed class ServicesPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.services",
        "Services",
        new Version(0, 1, 0),
        "Orvian",
        "Inspect and manage remote system services.",
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
            "/services",
            "Services",
            "settings",
            typeof(ServicesPageViewModel),
            "services");
        builder.Features.Add(new SystemdServicesReadProvider());
        builder.Features.Add(new SystemdServiceMutationProvider("start", PluginMutationRisk.Low));
        builder.Features.Add(new SystemdServiceMutationProvider("stop", PluginMutationRisk.Elevated));
        builder.Features.Add(
            new SystemdServiceMutationProvider("restart", PluginMutationRisk.Elevated));
        builder.Features.Add(new SystemdServiceMutationProvider("enable", PluginMutationRisk.Low));
        builder.Features.Add(new SystemdServiceMutationProvider("disable", PluginMutationRisk.Low));
    }
}

public sealed class ServicesPageViewModel;

public sealed class SystemdServicesReadProvider : IReadOnlyFeatureProvider
{
    private const int MaximumOutputLength = 1024 * 1024;
    private const int MaximumServices = 2000;
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "init.systemd", "service.read");
    private static readonly HashSet<string> KnownKeys = new(
        ["Id", "Description", "LoadState", "ActiveState", "SubState", "UnitFileState"],
        StringComparer.Ordinal);

    public string FeatureId => "services";

    public string ProviderId => "services.systemd.read";

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginReadRequest CreateRequest() =>
        new(
            "systemctl",
            [
                new("show"),
                new("--type=service"),
                new("--all"),
                new("--property=Id"),
                new("--property=Description"),
                new("--property=LoadState"),
                new("--property=ActiveState"),
                new("--property=SubState"),
                new("--property=UnitFileState"),
                new("--no-pager")
            ],
            TimeSpan.FromSeconds(20));

    public PluginFeatureParseResult Parse(PluginCommandOutput output)
    {
        if (output.IsTruncated || output.ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "The systemd service inventory was incomplete.");
        }

        if (output.StandardOutput.Length > MaximumOutputLength ||
            output.StandardOutput.Contains('\0'))
        {
            return PluginFeatureParseResult.Failure(
                "The systemd service inventory was malformed.");
        }

        var services = new List<ImmutableDictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (!TryFinishService(current, services))
                {
                    return PluginFeatureParseResult.Failure(
                        "The systemd service inventory contained an invalid record.");
                }

                current.Clear();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                return PluginFeatureParseResult.Failure(
                    "The systemd service inventory contained an invalid field.");
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!KnownKeys.Contains(key) ||
                !IsSafeValue(value) ||
                !current.TryAdd(key, value))
            {
                return PluginFeatureParseResult.Failure(
                    "The systemd service inventory contained duplicate or unsafe fields.");
            }
        }

        if (!TryFinishService(current, services) || services.Count == 0)
        {
            return PluginFeatureParseResult.Failure(
                "The systemd service inventory contained no valid services.");
        }

        var values = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.Ordinal);
        values.Add("Service count", services.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < services.Count; index++)
        {
            var service = services[index];
            var prefix = $"Service {index + 1:D4}";
            values.Add($"{prefix}.Id", service["Id"]);
            values.Add($"{prefix}.Description",
                service.GetValueOrDefault("Description", string.Empty));
            values.Add($"{prefix}.LoadState", service["LoadState"]);
            values.Add($"{prefix}.ActiveState", service["ActiveState"]);
            values.Add($"{prefix}.SubState", service["SubState"]);
            values.Add($"{prefix}.UnitFileState",
                service.GetValueOrDefault("UnitFileState", "unknown"));
        }

        return new(new PluginFeatureReadModel(values.ToImmutable()));
    }

    private static bool TryFinishService(
        IReadOnlyDictionary<string, string> current,
        ICollection<ImmutableDictionary<string, string>> services)
    {
        if (current.Count == 0)
        {
            return true;
        }

        if (services.Count >= MaximumServices ||
            !current.TryGetValue("Id", out var id) ||
            !id.EndsWith(".service", StringComparison.Ordinal) ||
            !current.ContainsKey("LoadState") ||
            !current.ContainsKey("ActiveState") ||
            !current.ContainsKey("SubState"))
        {
            return false;
        }

        services.Add(current.ToImmutableDictionary(StringComparer.Ordinal));
        return true;
    }

    private static bool IsSafeValue(string value) =>
        value.Length <= 4096 &&
        value.All(character =>
            character is '\t' || !char.IsControl(character));
}

public sealed class SystemdServiceMutationProvider(
    string action,
    PluginMutationRisk risk) : IMutationFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "init.systemd", "service.manage");

    public string FeatureId => "services";

    public string MutationId => $"service.{action}";

    public string ProviderId => $"services.systemd.{action}";

    public int Priority => 100;

    public string Title =>
        $"{char.ToUpperInvariant(action[0])}{action[1..]} service";

    public string Purpose => $"{Title} on the remote host.";

    public string ParameterName => "service";

    public string ParameterLabel => "Service unit (for example, nginx.service)";

    public PluginMutationRisk Risk { get; } = risk;

    public bool RequiresElevation => true;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginMutationRequest CreateRequest(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(ParameterName, out var service) ||
            !IsValidServiceName(service))
        {
            throw new ArgumentException(
                "Service must be a safe systemd service-unit identifier.",
                nameof(parameters));
        }

        return new(
            Title,
            $"{Title} “{service}”.",
            Risk,
            "systemctl",
            [new(action), new(service)],
            TimeSpan.FromSeconds(30));
    }

    private static bool IsValidServiceName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.EndsWith(".service", StringComparison.Ordinal) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is ':' or '_' or '.' or '@' or '-');
}
