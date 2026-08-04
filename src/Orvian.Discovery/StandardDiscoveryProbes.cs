using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Discovery;

public static class StandardDiscoveryProbes
{
    public static IReadOnlyList<DiscoveryProbe> Create() =>
    [
        new(
            "core.uname.system",
            "uname",
            [new("-s")],
            TimeSpan.FromSeconds(5),
            ParseOperatingSystem),
        new(
            "core.uname.release",
            "uname",
            [new("-r")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("kernel.release", output)),
        new(
            "core.uname.architecture",
            "uname",
            [new("-m")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("kernel.arch", output)),
        new(
            "core.hostname",
            "hostname",
            [],
            TimeSpan.FromSeconds(5),
            output => SingleFact("host.name", output)),
        new(
            "core.os-release",
            "cat",
            [new("/etc/os-release")],
            TimeSpan.FromSeconds(5),
            ParseOsRelease,
            IsOptional: true),
        new(
            "core.freebsd-version",
            "freebsd-version",
            [new("-u")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("os.version", output),
            IsOptional: true),
        new(
            "core.sw-vers.name",
            "sw_vers",
            [new("-productName")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("os.pretty_name", output),
            IsOptional: true),
        new(
            "core.sw-vers.version",
            "sw_vers",
            [new("-productVersion")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("os.version", output),
            IsOptional: true),
        new(
            "core.shell",
            "printenv",
            [new("SHELL")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("user.shell", output),
            IsOptional: true),
        new(
            "core.effective-user",
            "id",
            [new("-u")],
            TimeSpan.FromSeconds(5),
            ParseEffectiveUser),
        new(
            "core.groups",
            "id",
            [new("-Gn")],
            TimeSpan.FromSeconds(5),
            ParseGroups,
            IsOptional: true),
        new(
            "core.uptime",
            "uptime",
            [new("-s")],
            TimeSpan.FromSeconds(5),
            output => SingleFact("system.boot_time", output),
            IsOptional: true),
        ToolProbe(
            "core.getent",
            "getent",
            ["passwd", "root"],
            "tool.getent.available",
            ["identity.getent", "identity.read"]),
        new(
            "core.systemd",
            "systemctl",
            [new("--version")],
            TimeSpan.FromSeconds(5),
            output => HasContent(output)
                ? new(
                    [KeyValuePair.Create("tool.systemctl.available", "true")],
                    ["init.systemd", "service.read", "service.manage"])
                : DiscoveryParseResult.Failure("systemctl returned empty output."),
            IsOptional: true),
        ToolProbe(
            "core.openrc",
            "rc-status",
            ["--version"],
            "tool.openrc.available",
            ["init.openrc", "service.read"]),
        new(
            "core.rcd",
            "service",
            [new("-e")],
            TimeSpan.FromSeconds(5),
            ParseRcdServices,
            IsOptional: true),
        ToolProbe(
            "core.launchd",
            "launchctl",
            ["version"],
            "tool.launchctl.available",
            ["init.launchd", "service.read"]),
        ToolProbe(
            "core.sudo",
            "sudo",
            ["--version"],
            "tool.sudo.available",
            ["privilege.sudo"]),
        ToolProbe(
            "core.doas",
            "doas",
            ["-V"],
            "tool.doas.available",
            ["privilege.doas"]),
        ToolProbe(
            "core.date",
            "date",
            ["+%s"],
            "tool.date.available",
            ["time.read"]),
        ToolProbe(
            "core.timedatectl",
            "timedatectl",
            ["--version"],
            "tool.timedatectl.available",
            [
                "time.read",
                "time.systemd",
                "time.timezone.write",
                "time.datetime.write"
            ]),
        ToolProbe(
            "core.iproute2",
            "ip",
            ["-Version"],
            "tool.ip.available",
            ["network.ip.read"]),
        ToolProbe(
            "core.networkmanager",
            "nmcli",
            ["--version"],
            "network.provider",
            ["network.ip.read", "network.networkmanager.write"],
            factValue: "networkmanager"),
        ToolProbe(
            "core.netplan",
            "netplan",
            ["info"],
            "network.provider",
            ["network.ip.read", "network.netplan.write"],
            factValue: "netplan"),
        ToolProbe(
            "core.apt",
            "apt-get",
            ["--version"],
            "package.provider",
            ["package.apt", "package.read"],
            factValue: "apt"),
        ToolProbe(
            "core.dnf",
            "dnf",
            ["--version"],
            "package.provider",
            ["package.dnf", "package.read"],
            factValue: "dnf"),
        ToolProbe(
            "core.yum",
            "yum",
            ["--version"],
            "package.provider",
            ["package.yum", "package.read"],
            factValue: "yum"),
        ToolProbe(
            "core.pacman",
            "pacman",
            ["--version"],
            "package.provider",
            ["package.pacman", "package.read"],
            factValue: "pacman"),
        ToolProbe(
            "core.zypper",
            "zypper",
            ["--version"],
            "package.provider",
            ["package.zypper", "package.read"],
            factValue: "zypper"),
        ToolProbe(
            "core.apk",
            "apk",
            ["--version"],
            "package.provider",
            ["package.apk", "package.read"],
            factValue: "apk"),
        ToolProbe(
            "core.pkg",
            "pkg",
            ["--version"],
            "package.provider",
            ["package.pkg", "package.read"],
            factValue: "pkg"),
        ToolProbe(
            "core.brew",
            "brew",
            ["--version"],
            "package.provider",
            ["package.brew", "package.read"],
            factValue: "brew"),
        ToolProbe(
            "core.docker",
            "docker",
            ["--version"],
            "container.provider",
            ["container.docker", "docker.read", "docker.manage"],
            factValue: "docker")
    ];

    private static DiscoveryProbe ToolProbe(
        string id,
        string executable,
        string[] arguments,
        string factKey,
        string[] capabilities,
        string factValue = "true") =>
        new(
            id,
            executable,
            [.. arguments.Select(argument => new CommandArgument(argument))],
            TimeSpan.FromSeconds(5),
            output => HasContent(output)
                ? new([KeyValuePair.Create(factKey, factValue)], [.. capabilities])
                : DiscoveryParseResult.Failure(
                    $"{executable} returned empty output."),
            IsOptional: true);

    private static DiscoveryParseResult ParseEffectiveUser(string output)
    {
        var value = NormalizeSingleLine(output);
        if (!uint.TryParse(value, out var userId))
        {
            return DiscoveryParseResult.Failure(
                "The effective user ID was not a valid nonnegative integer.");
        }

        return new(
            [KeyValuePair.Create("user.effective_uid", userId.ToString())],
            userId == 0 ? ["privilege.root"] : []);
    }

    private static DiscoveryParseResult ParseGroups(string output)
    {
        var value = NormalizeSingleLine(output);
        if (value.Length is 0 or > 4_096)
        {
            return DiscoveryParseResult.Failure(
                "The effective group list was empty or too large.");
        }

        var groups = value.Split(
            ' ',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length is 0 or > 256 ||
            groups.Any(group =>
                group.Length > 256 ||
                group.Any(character =>
                    char.IsControl(character) || char.IsWhiteSpace(character))))
        {
            return DiscoveryParseResult.Failure(
                "The effective group list was malformed.");
        }

        return new(
            [KeyValuePair.Create("user.groups", string.Join(' ', groups))],
            ["identity.groups"]);
    }

    private static DiscoveryParseResult ParseRcdServices(string output)
    {
        if (output.Length is 0 or > 64 * 1024 || output.Contains('\0'))
        {
            return DiscoveryParseResult.Failure(
                "The rc.d service inventory was empty, malformed, or too large.");
        }

        var services = output.Split(
            '\n',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (services.Length is 0 or > 2_048 ||
            services.Any(path =>
                path.Contains('\r') ||
                !(path.StartsWith("/etc/rc.d/", StringComparison.Ordinal) ||
                  path.StartsWith("/usr/local/etc/rc.d/", StringComparison.Ordinal))))
        {
            return DiscoveryParseResult.Failure(
                "The rc.d service inventory contained an unexpected path.");
        }

        return new(
            [KeyValuePair.Create("tool.service.available", "true")],
            ["init.rcd", "service.read"]);
    }

    private static DiscoveryParseResult ParseOsRelease(string output)
    {
        if (output.Length > 64 * 1024 || output.Contains('\0'))
        {
            return DiscoveryParseResult.Failure(
                "The operating-system release data was malformed or too large.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            if (key.Any(character => !(character is >= 'A' and <= 'Z' or '_')))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        var facts = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        AddOsReleaseFact(values, "ID", "os.distribution", facts);
        AddOsReleaseFact(values, "VERSION_ID", "os.version", facts);
        AddOsReleaseFact(values, "PRETTY_NAME", "os.pretty_name", facts);
        return facts.Count == 0
            ? DiscoveryParseResult.Failure(
                "The operating-system release data contained no recognized fields.")
            : new(facts.ToImmutable(), []);
    }

    private static void AddOsReleaseFact(
        IReadOnlyDictionary<string, string> values,
        string sourceKey,
        string factKey,
        ICollection<KeyValuePair<string, string>> facts)
    {
        if (values.TryGetValue(sourceKey, out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            facts.Add(KeyValuePair.Create(factKey, value));
        }
    }

    private static DiscoveryParseResult ParseOperatingSystem(string output)
    {
        var value = NormalizeSingleLine(output);
        return value.ToLowerInvariant() switch
        {
            "linux" => new(
                [KeyValuePair.Create("os.family", "linux")],
                ["shell.posix"]),
            "freebsd" => new(
                [KeyValuePair.Create("os.family", "freebsd")],
                ["shell.posix"]),
            "darwin" => new(
                [KeyValuePair.Create("os.family", "macos")],
                ["shell.posix"]),
            _ => DiscoveryParseResult.Failure(
                "The operating-system family was not recognized.")
        };
    }

    private static DiscoveryParseResult SingleFact(string key, string output)
    {
        var value = NormalizeSingleLine(output);
        return value.Length == 0
            ? DiscoveryParseResult.Failure($"Probe for '{key}' returned empty output.")
            : new([KeyValuePair.Create(key, value)], []);
    }

    private static bool HasContent(string output) =>
        output.Length is > 0 and <= 64 * 1024 &&
        !output.Contains('\0') &&
        output.All(character =>
            character is '\r' or '\n' or '\t' || !char.IsControl(character)) &&
        output.Any(character => !char.IsWhiteSpace(character));

    private static string NormalizeSingleLine(string output)
    {
        var value = output.Trim();
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
        {
            throw new FormatException("Expected one line of output.");
        }

        return value;
    }
}
