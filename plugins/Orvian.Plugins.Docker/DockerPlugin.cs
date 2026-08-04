using System.Collections.Immutable;
using System.Text.Json;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.Docker;

public sealed class DockerPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.docker",
        "Docker",
        new Version(0, 1, 0),
        "Orvian",
        "Inspect and safely manage Docker images and containers.",
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
            "/docker",
            "Docker",
            "docker",
            typeof(DockerPageViewModel),
            DockerFeatureIds.Management);
        builder.Features.Add(new DockerInventoryProvider());
        builder.Features.Add(DockerMutationProvider.PullImage());
        builder.Features.Add(DockerMutationProvider.RemoveImage());
        builder.Features.Add(DockerMutationProvider.StartContainer());
        builder.Features.Add(DockerMutationProvider.StopContainer());
        builder.Features.Add(DockerMutationProvider.RestartContainer());
        builder.Features.Add(DockerMutationProvider.RemoveContainer());
        builder.Features.Add(new DockerContainerLogsProvider());
        builder.Features.Add(DockerMutationProvider.PruneSystem());
    }
}

public sealed class DockerContainerLogsProvider : IParameterizedReadFeatureProvider
{
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "container.docker", "docker.read");
    public string FeatureId => DockerFeatureIds.Management;
    public string ProviderId => "docker.cli.container.logs";
    public int Priority => 100;
    public IReadOnlySet<string> RequiredCapabilities => Capabilities;
    public string ActionId => "container.logs";
    public string Title => "Container logs";
    public string Purpose => "Read recent container logs.";
    public IReadOnlyList<PluginMutationParameter> Parameters { get; } =
        [new("container", "Container ID or name")];

    public PluginReadRequest CreateRequest(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var container = DockerMutationProvider.RequireResourceIdentifier(parameters, "container");
        return new(
            "docker",
            [new("container"), new("logs"), new("--tail"), new("200"), new(container)],
            TimeSpan.FromSeconds(20));
    }

    public PluginFeatureParseResult Parse(PluginCommandOutput output) =>
        output.ExitCode != 0 || output.IsTruncated
            ? PluginFeatureParseResult.Failure("Container logs were incomplete.")
            : new(new PluginFeatureReadModel(
                ImmutableDictionary<string, string>.Empty.Add(
                    "Logs",
                    string.Join(
                        Environment.NewLine,
                        new[] { output.StandardOutput, output.StandardError }
                            .Where(value => !string.IsNullOrEmpty(value))))));
}

public sealed class DockerPageViewModel;

public static class DockerFeatureIds
{
    public const string Management = "docker";
}

public sealed class DockerInventoryProvider : IMultiCommandReadFeatureProvider
{
    private const int MaximumRecords = 2_000;
    private const int MaximumOutputLength = 1024 * 1024;
    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "container.docker", "docker.read");
    private static readonly ImmutableArray<string> ImageFields =
        ["ID", "Repository", "Tag", "Digest", "CreatedSince", "Size"];
    private static readonly ImmutableArray<string> ContainerFields =
        ["ID", "Names", "Image", "State", "Status", "Ports", "Size", "CreatedAt"];

    public string FeatureId => DockerFeatureIds.Management;

    public string ProviderId => "docker.cli.inventory";

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities => Capabilities;

    public ImmutableArray<PluginReadRequest> CreateRequests() =>
    [
        new(
            "docker",
            [
                new("image"), new("ls"), new("--no-trunc"),
                new("--format"), new(CreateFormat(ImageFields))
            ],
            TimeSpan.FromSeconds(30)),
        new(
            "docker",
            [
                new("container"), new("ls"), new("--all"), new("--no-trunc"),
                new("--size"), new("--format"), new(CreateFormat(ContainerFields))
            ],
            TimeSpan.FromSeconds(30))
    ];

    public PluginFeatureParseResult Parse(ImmutableArray<PluginCommandOutput> outputs)
    {
        if (outputs.Length != 2 || outputs.Any(output =>
                output.ExitCode != 0 || output.IsTruncated))
        {
            return PluginFeatureParseResult.Failure(
                "The Docker inventory was incomplete.");
        }

        if (!TryParseRecords(outputs[0].StandardOutput, ImageFields, out var images))
        {
            return PluginFeatureParseResult.Failure(
                "The Docker image inventory contained malformed output.");
        }

        if (!TryParseRecords(outputs[1].StandardOutput, ContainerFields, out var containers))
        {
            return PluginFeatureParseResult.Failure(
                "The Docker container inventory contained malformed output.");
        }

        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        AddRecords(values, "Image", images);
        AddRecords(values, "Container", containers);
        return new(new PluginFeatureReadModel(values.ToImmutable()));
    }

    private static bool TryParseRecords(
        string output,
        ImmutableArray<string> fields,
        out ImmutableArray<ImmutableDictionary<string, string>> records)
    {
        records = [];
        if (output.Length > MaximumOutputLength || output.Contains('\0'))
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<ImmutableDictionary<string, string>>();
        foreach (var line in output.Split(
                     '\n',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Count >= MaximumRecords || line.Length > 16_384)
            {
                return false;
            }

            try
            {
                var encodedValues = line.TrimEnd('\r').Split('\t');
                if (encodedValues.Length != fields.Length)
                {
                    return false;
                }

                var record = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                for (var index = 0; index < fields.Length; index++)
                {
                    using var document = JsonDocument.Parse(encodedValues[index]);
                    if (document.RootElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var value = document.RootElement.GetString() ?? string.Empty;
                    if (!IsSafeValue(value) || !record.TryAdd(fields[index], value))
                    {
                        return false;
                    }
                }

                if (string.IsNullOrWhiteSpace(record["ID"]))
                {
                    return false;
                }

                builder.Add(record.ToImmutable());
            }
            catch (JsonException)
            {
                return false;
            }
        }

        records = builder.ToImmutable();
        return true;
    }

    private static string CreateFormat(IEnumerable<string> fields) =>
        string.Join('\t', fields.Select(field => $"{{{{json .{field}}}}}"));

    private static void AddRecords(
        ImmutableDictionary<string, string>.Builder values,
        string kind,
        ImmutableArray<ImmutableDictionary<string, string>> records)
    {
        values.Add($"{kind} count", records.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < records.Length; index++)
        {
            foreach (var field in records[index].OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                values.Add($"{kind} {index + 1:D4}.{field.Key}", field.Value);
            }
        }
    }

    private static bool IsSafeValue(string value) =>
        value.Length <= 8_192 &&
        value.All(character => character is '\t' || !char.IsControl(character));
}

public sealed class DockerMutationProvider : IMutationFeatureProvider
{
    private static readonly ImmutableHashSet<string> ManageCapabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, "container.docker", "docker.manage");
    private readonly string _mutationId;
    private readonly string _title;
    private readonly string _purpose;
    private readonly ImmutableArray<string> _fixedArguments;
    private readonly ImmutableArray<PluginMutationParameter> _parameters;
    private readonly Func<IReadOnlyDictionary<string, string>, ImmutableArray<string>> _arguments;
    private readonly bool _displaysOutput;

    private DockerMutationProvider(
        string mutationId,
        string title,
        string purpose,
        PluginMutationRisk risk,
        ImmutableArray<string> fixedArguments,
        ImmutableArray<PluginMutationParameter> parameters,
        Func<IReadOnlyDictionary<string, string>, ImmutableArray<string>> arguments,
        bool displaysOutput = false)
    {
        _mutationId = mutationId;
        _title = title;
        _purpose = purpose;
        Risk = risk;
        _fixedArguments = fixedArguments;
        _parameters = parameters;
        _arguments = arguments;
        _displaysOutput = displaysOutput;
    }

    public string FeatureId => DockerFeatureIds.Management;
    public string MutationId => _mutationId;
    public string ProviderId => $"docker.cli.{_mutationId}";
    public int Priority => 100;
    public string Title => _title;
    public string Purpose => _purpose;
    public string ParameterName => _parameters.IsDefaultOrEmpty ? string.Empty : _parameters[0].Name;
    public string ParameterLabel => _parameters.IsDefaultOrEmpty ? string.Empty : _parameters[0].Label;
    public IReadOnlyList<PluginMutationParameter> Parameters => _parameters;
    public PluginMutationRisk Risk { get; }
    public bool RequiresElevation => false;
    public bool DisplaysOutput => _displaysOutput;
    public IReadOnlySet<string> RequiredCapabilities => ManageCapabilities;

    public PluginMutationRequest CreateRequest(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var variableArguments = _arguments(parameters);
        return new(
            Title,
            Purpose,
            Risk,
            "docker",
            [.. _fixedArguments.Concat(variableArguments).Select(value => new PluginCommandArgument(value))],
            _mutationId == "system.prune" ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(2));
    }

    public static DockerMutationProvider PullImage() => new(
        "image.pull", "Pull image", "Pull a Docker image from its configured registry.",
        PluginMutationRisk.Elevated, ["image", "pull"],
        [new("image", "Image reference")],
        parameters => [RequireImageReference(parameters, "image")]);

    public static DockerMutationProvider RemoveImage() => new(
        "image.remove", "Delete image", "Delete a Docker image from the remote host.",
        PluginMutationRisk.Destructive, ["image", "rm"],
        [new("image", "Image ID or reference")],
        parameters => [RequireResourceIdentifier(parameters, "image")]);

    public static DockerMutationProvider StartContainer() => ContainerAction(
        "container.start", "Start container", "start", PluginMutationRisk.Low);

    public static DockerMutationProvider StopContainer() => ContainerAction(
        "container.stop", "Stop container", "stop", PluginMutationRisk.Elevated);

    public static DockerMutationProvider RestartContainer() => ContainerAction(
        "container.restart", "Restart container", "restart", PluginMutationRisk.Elevated);

    public static DockerMutationProvider RemoveContainer() => ContainerAction(
        "container.remove", "Delete container", "rm", PluginMutationRisk.Destructive);

    public static DockerMutationProvider PruneSystem() => new(
        "system.prune", "Prune unused Docker data",
        "Delete stopped containers, unused networks, dangling images, and build cache.",
        PluginMutationRisk.Destructive, ["system", "prune", "--force"], [], _ => []);

    private static DockerMutationProvider ContainerAction(
        string id,
        string title,
        string dockerAction,
        PluginMutationRisk risk) => new(
        id, title, $"{title} on the remote host.", risk,
        ["container", dockerAction], [new("container", "Container ID or name")],
        parameters => [RequireResourceIdentifier(parameters, "container")]);

    internal static string RequireResourceIdentifier(
        IReadOnlyDictionary<string, string> parameters,
        string name)
    {
        if (!parameters.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.StartsWith("-", StringComparison.Ordinal) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '.' or '-')))
        {
            throw new ArgumentException("Docker resource identifier is invalid.", nameof(parameters));
        }

        return value;
    }

    private static string RequireImageReference(
        IReadOnlyDictionary<string, string> parameters,
        string name)
    {
        if (!parameters.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.StartsWith("-", StringComparison.Ordinal) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '.' or '-' or '/' or ':' or '@')))
        {
            throw new ArgumentException("Docker image reference is invalid.", nameof(parameters));
        }

        return value;
    }
}
