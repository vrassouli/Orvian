using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.NetworkSettings;

public sealed class NetworkSettingsPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.network-settings", "Network Settings", new Version(0, 1, 0),
        "Remotune", "Inspect and update IPv4 and IPv6 connection settings.",
        new Version(0, 1, 0),
        [PluginPermissions.UiNavigationContribute, PluginPermissions.CommandReadExecute,
            PluginPermissions.CommandMutateExecute,
            PluginPermissions.PrivilegeElevatedRequest]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/network-settings", "Network Settings", "network",
            typeof(NetworkSettingsPageViewModel), "network-settings");
        builder.Features.Add(new LinuxNetworkReadProvider());
        builder.Features.Add(new NetworkManagerMutationProvider(AddressFamily.InterNetwork));
        builder.Features.Add(new NetworkManagerMutationProvider(AddressFamily.InterNetworkV6));
        builder.Features.Add(new NetplanMutationProvider());
    }
}

public sealed class NetplanMutationProvider : IMultiCommandMutationFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "network.netplan.write");

    public string FeatureId => "network-settings";
    public string MutationId => "network.netplan.update";
    public string ProviderId => "network.netplan";
    public int Priority => 100;
    public string Title => "Apply Netplan settings";
    public string Purpose => "Persist and apply IPv4/IPv6 settings. The SSH session may disconnect; update the Remotune host endpoint before reconnecting if its address changes.";
    public string ParameterName => "interface";
    public string ParameterLabel => "Network interface";
    public IReadOnlyList<PluginMutationParameter> Parameters =>
    [
        new("interface", "Network interface (for example, enp0s5)"),
        new("ipv4Address", "IPv4 address/prefix (optional)", false),
        new("ipv4Gateway", "IPv4 gateway (optional)", false),
        new("ipv6Address", "IPv6 address/prefix (optional)", false),
        new("ipv6Gateway", "IPv6 gateway (optional)", false),
        new("dns", "IPv4/IPv6 DNS servers, comma-separated")
    ];
    public PluginMutationRisk Risk => PluginMutationRisk.Destructive;
    public bool RequiresElevation => true;
    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public ImmutableArray<PluginMutationRequest> CreateRequests(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var device = Required(parameters, "interface");
        if (!LinuxNetworkReadProvider.IsInterfaceName(device))
        {
            throw new ArgumentException("Invalid interface name.", nameof(parameters));
        }

        var ipv4Address = Optional(parameters, "ipv4Address");
        var ipv6Address = Optional(parameters, "ipv6Address");
        if (ipv4Address is null && ipv6Address is null)
        {
            throw new ArgumentException("At least one address is required.", nameof(parameters));
        }

        if (ipv4Address is not null)
        {
            ValidateCidr(ipv4Address, AddressFamily.InterNetwork);
        }

        if (ipv6Address is not null)
        {
            ValidateCidr(ipv6Address, AddressFamily.InterNetworkV6);
        }

        var ipv4Gateway = Optional(parameters, "ipv4Gateway");
        var ipv6Gateway = Optional(parameters, "ipv6Gateway");
        ValidateOptionalAddress(ipv4Gateway, AddressFamily.InterNetwork);
        ValidateOptionalAddress(ipv6Gateway, AddressFamily.InterNetworkV6);
        var dns = ParseDns(Required(parameters, "dns"));
        var addresses = new[] { ipv4Address, ipv6Address }
            .Where(value => value is not null).Cast<string>().ToArray();
        var routes = new List<Dictionary<string, string>>();
        if (ipv4Gateway is not null)
        {
            routes.Add(new() { ["to"] = "default", ["via"] = ipv4Gateway });
        }

        if (ipv6Gateway is not null)
        {
            routes.Add(new() { ["to"] = "default", ["via"] = ipv6Gateway });
        }

        var purpose = Purpose;
        return
        [
            Set($"ethernets.{device}.dhcp4=false", purpose),
            Set($"ethernets.{device}.dhcp6=false", purpose),
            Set($"ethernets.{device}.addresses={JsonSerializer.Serialize(addresses)}", purpose),
            Set($"ethernets.{device}.routes={JsonSerializer.Serialize(routes)}", purpose),
            Set($"ethernets.{device}.nameservers.addresses={JsonSerializer.Serialize(dns)}", purpose),
            new(Title, purpose, Risk, "netplan", [new("generate")], TimeSpan.FromSeconds(20)),
            new(Title, purpose, Risk, "netplan", [new("apply")], TimeSpan.FromSeconds(45))
        ];
    }

    private PluginMutationRequest Set(string assignment, string purpose) =>
        new(Title, purpose, Risk, "netplan", [new("set"), new(assignment)],
            TimeSpan.FromSeconds(20));

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        Optional(values, key) ??
        throw new ArgumentException($"{key} is required.", nameof(values));

    private static string? Optional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static void ValidateCidr(string value, AddressFamily family)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != family || !int.TryParse(parts[1], out var prefix) ||
            prefix < 0 || prefix > (family == AddressFamily.InterNetwork ? 32 : 128))
        {
            throw new ArgumentException("Invalid address/prefix.", nameof(value));
        }
    }

    private static void ValidateOptionalAddress(string? value, AddressFamily family)
    {
        if (value is not null &&
            (!IPAddress.TryParse(value, out var address) || address.AddressFamily != family))
        {
            throw new ArgumentException("Invalid gateway address.", nameof(value));
        }
    }

    private static string[] ParseDns(string value)
    {
        var servers = value.Split(',', StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (servers.Length is 0 or > 8 ||
            servers.Any(server => !IPAddress.TryParse(server, out _)))
        {
            throw new ArgumentException("Invalid DNS address list.", nameof(value));
        }

        return servers.Select(server => IPAddress.Parse(server).ToString()).ToArray();
    }
}

public sealed class NetworkSettingsPageViewModel;

public sealed class LinuxNetworkReadProvider : IMultiCommandReadFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "network.ip.read");

    public string FeatureId => "network-settings";
    public string ProviderId => "network.linux.iproute2";
    public int Priority => 100;
    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public ImmutableArray<PluginReadRequest> CreateRequests() =>
    [
        new("ip", [new("-j"), new("address"), new("show")], TimeSpan.FromSeconds(10)),
        new("ip", [new("-j"), new("route"), new("show"), new("table"), new("all")],
            TimeSpan.FromSeconds(10)),
        new("cat", [new("/etc/resolv.conf")], TimeSpan.FromSeconds(10))
    ];

    public PluginFeatureParseResult Parse(ImmutableArray<PluginCommandOutput> outputs)
    {
        if (outputs.Length != 3 || outputs.Any(output => output.IsTruncated) ||
            outputs[0].ExitCode != 0 || outputs[1].ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "Network information was incomplete or unavailable.");
        }

        if (outputs.Sum(output => output.StandardOutput.Length) > 512 * 1024 ||
            outputs.Any(output => output.StandardOutput.Contains('\0')))
        {
            return PluginFeatureParseResult.Failure("Network information was malformed.");
        }

        try
        {
            using var addresses = JsonDocument.Parse(outputs[0].StandardOutput);
            using var routes = JsonDocument.Parse(outputs[1].StandardOutput);
            var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var link in addresses.RootElement.EnumerateArray())
            {
                var name = link.GetProperty("ifname").GetString();
                if (!IsInterfaceName(name))
                {
                    continue;
                }

                var ipv4 = new List<string>();
                var ipv6 = new List<string>();
                if (link.TryGetProperty("addr_info", out var addressInfo))
                {
                    foreach (var address in addressInfo.EnumerateArray())
                    {
                        var local = address.GetProperty("local").GetString();
                        var prefix = address.GetProperty("prefixlen").GetInt32();
                        if (IPAddress.TryParse(local, out var parsed))
                        {
                            (parsed.AddressFamily == AddressFamily.InterNetwork ? ipv4 : ipv6)
                                .Add($"{parsed}/{prefix}");
                        }
                    }
                }

                values[$"{name}.ipv4_addresses"] = ipv4.Count == 0 ? "None" : string.Join(", ", ipv4);
                values[$"{name}.ipv6_addresses"] = ipv6.Count == 0 ? "None" : string.Join(", ", ipv6);
            }

            foreach (var route in routes.RootElement.EnumerateArray())
            {
                if (!route.TryGetProperty("dst", out var destination) ||
                    destination.GetString() != "default" ||
                    !route.TryGetProperty("gateway", out var gateway) ||
                    !route.TryGetProperty("dev", out var device))
                {
                    continue;
                }

                var gatewayValue = gateway.GetString();
                var deviceValue = device.GetString();
                if (IsInterfaceName(deviceValue) && IPAddress.TryParse(gatewayValue, out var parsed))
                {
                    var family = parsed.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6";
                    values[$"{deviceValue}.{family}_gateway"] = parsed.ToString();
                }
            }

            values["dns_servers"] = NormalizeDns(outputs[2].StandardOutput);
            return new(new PluginFeatureReadModel(values.ToImmutable()));
        }
        catch (JsonException)
        {
            return PluginFeatureParseResult.Failure("Network information was malformed.");
        }
        catch (InvalidOperationException)
        {
            return PluginFeatureParseResult.Failure("Network information was malformed.");
        }
    }

    private static string NormalizeDns(string output)
    {
        var servers = output.Split('\n', StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[0] == "nameserver" &&
                IPAddress.TryParse(parts[1], out _))
            .Select(parts => IPAddress.Parse(parts[1]).ToString())
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        return servers.Length == 0 ? "None" : string.Join(", ", servers);
    }

    internal static bool IsInterfaceName(string? value) =>
        value is { Length: > 0 and <= 15 } &&
        Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
}

public sealed class NetworkManagerMutationProvider(AddressFamily family) : IMutationFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "network.networkmanager.write");
    private readonly bool _ipv6 = family == AddressFamily.InterNetworkV6;

    public string FeatureId => "network-settings";
    public string MutationId => _ipv6 ? "network.ipv6.update" : "network.ipv4.update";
    public string ProviderId => _ipv6 ? "network.networkmanager.ipv6" : "network.networkmanager.ipv4";
    public int Priority => 100;
    public string Title => _ipv6 ? "Update IPv6" : "Update IPv4";
    public string Purpose => "Update a NetworkManager connection profile. Activate the profile from the host console to avoid losing this SSH session.";
    public string ParameterName => "connection";
    public string ParameterLabel => "NetworkManager connection name";
    public IReadOnlyList<PluginMutationParameter> Parameters =>
    [
        new("connection", "NetworkManager connection name"),
        new("address", _ipv6 ? "IPv6 address/prefix (for example, 2001:db8::10/64)" : "IPv4 address/prefix (for example, 192.0.2.10/24)"),
        new("gateway", _ipv6 ? "IPv6 gateway" : "IPv4 gateway"),
        new("dns", _ipv6 ? "IPv6 DNS servers, comma-separated" : "IPv4 DNS servers, comma-separated")
    ];
    public PluginMutationRisk Risk => PluginMutationRisk.Destructive;
    public bool RequiresElevation => true;
    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public PluginMutationRequest CreateRequest(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var connection = Required(parameters, "connection");
        if (connection.Length > 128 || connection.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid connection name.", nameof(parameters));
        }

        var address = Required(parameters, "address");
        ValidateCidr(address, family);
        var gateway = Required(parameters, "gateway");
        ValidateAddress(gateway, family);
        var dns = Required(parameters, "dns");
        foreach (var server in dns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateAddress(server, family);
        }

        if (dns.Split(',', StringSplitOptions.RemoveEmptyEntries).Length is 0 or > 8)
        {
            throw new ArgumentException("One to eight DNS servers are required.", nameof(parameters));
        }

        var prefix = _ipv6 ? "ipv6" : "ipv4";
        return new(Title, Purpose, Risk, "nmcli",
            [new("connection"), new("modify"), new(connection),
                new($"{prefix}.method"), new("manual"),
                new($"{prefix}.addresses"), new(address),
                new($"{prefix}.gateway"), new(gateway),
                new($"{prefix}.dns"), new(dns)], TimeSpan.FromSeconds(20));
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{key} is required.", nameof(parameters));

    private static void ValidateCidr(string value, AddressFamily expectedFamily)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != expectedFamily || !int.TryParse(parts[1], out var prefix) ||
            prefix < 0 || prefix > (expectedFamily == AddressFamily.InterNetwork ? 32 : 128))
        {
            throw new ArgumentException("Invalid address and prefix.", nameof(value));
        }
    }

    private static void ValidateAddress(string value, AddressFamily expectedFamily)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != expectedFamily)
        {
            throw new ArgumentException("Invalid IP address.", nameof(value));
        }
    }
}
