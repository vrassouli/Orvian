using System.Collections.Immutable;

namespace Orvian.Plugin.Abstractions;

public interface IOrvianPlugin
{
    PluginManifest Manifest { get; }

    void Configure(IPluginBuilder builder);
}

public sealed record PluginManifest(
    string Id,
    string Name,
    Version Version,
    string Publisher,
    string Description,
    Version OrvianApiVersion,
    ImmutableArray<string> Permissions);

public static class PluginPermissions
{
    public const string HostRead = "host.read";
    public const string DiscoveryContribute = "discovery.contribute";
    public const string CommandReadExecute = "command.read.execute";
    public const string CommandMutateExecute = "command.mutate.execute";
    public const string CommandShellExecute = "command.shell.execute";
    public const string PrivilegeElevatedRequest = "privilege.elevated.request";
    public const string UiNavigationContribute = "ui.navigation.contribute";
    public const string UiCommandContribute = "ui.command.contribute";
    public const string SettingsPluginReadWrite = "settings.plugin.readwrite";
    public const string BackgroundRead = "background.read";
    public const string FileRead = "file.read";
    public const string FileWrite = "file.write";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            HostRead,
            DiscoveryContribute,
            CommandReadExecute,
            CommandMutateExecute,
            CommandShellExecute,
            PrivilegeElevatedRequest,
            UiNavigationContribute,
            UiCommandContribute,
            SettingsPluginReadWrite,
            BackgroundRead,
            FileRead,
            FileWrite
        ],
        StringComparer.Ordinal);
}

public interface IPluginBuilder
{
    INavigationRegistry Navigation { get; }
    ICommandRegistry Commands { get; }
    ICapabilityRegistry Capabilities { get; }
    ISettingsRegistry Settings { get; }
    IFeatureProviderRegistry Features { get; }
}

public sealed record PluginCommandArgument(string Value, bool IsSensitive = false);

public sealed record PluginReadRequest(
    string Executable,
    ImmutableArray<PluginCommandArgument> Arguments,
    TimeSpan Timeout);

public sealed record PluginCommandOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool IsTruncated);

public sealed record PluginFeatureReadModel(
    ImmutableDictionary<string, string> Values);

public sealed record LocalHostFeatureContext(
    string DisplayName,
    string Endpoint,
    string ConnectionState,
    string? TrustedHostKeyAlgorithm,
    string? TrustedHostKeyFingerprint,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? DiscoveryCompletedAt,
    bool IsDiscoveryPartial,
    string OperatingSystem,
    ImmutableDictionary<string, string> Facts,
    ImmutableHashSet<string> Capabilities);

public sealed record PluginFeatureParseResult(
    PluginFeatureReadModel? Model,
    string? SafeFailureMessage = null)
{
    public bool IsSuccess => Model is not null && SafeFailureMessage is null;

    public static PluginFeatureParseResult Failure(string safeMessage) =>
        new(null, safeMessage);
}

public interface IReadOnlyFeatureProvider
{
    string FeatureId { get; }

    string ProviderId { get; }

    int Priority { get; }

    IReadOnlySet<string> RequiredCapabilities { get; }

    PluginReadRequest CreateRequest();

    PluginFeatureParseResult Parse(PluginCommandOutput output);
}

public interface ILocalHostFeatureProvider
{
    string FeatureId { get; }

    string ProviderId { get; }

    int Priority { get; }

    IReadOnlySet<string> RequiredCapabilities { get; }

    PluginFeatureParseResult Build(LocalHostFeatureContext context);
}

public interface IMultiCommandReadFeatureProvider : IReadOnlyFeatureProvider
{
    ImmutableArray<PluginReadRequest> CreateRequests();

    PluginFeatureParseResult Parse(
        ImmutableArray<PluginCommandOutput> outputs);

    PluginReadRequest IReadOnlyFeatureProvider.CreateRequest() =>
        throw new NotSupportedException(
            "Multi-command providers construct a request sequence.");

    PluginFeatureParseResult IReadOnlyFeatureProvider.Parse(
        PluginCommandOutput output) =>
        throw new NotSupportedException(
            "Multi-command providers parse a result sequence.");
}

public interface IParameterizedReadFeatureProvider : IReadOnlyFeatureProvider
{
    string ActionId { get; }
    string Title { get; }
    string Purpose { get; }
    IReadOnlyList<PluginMutationParameter> Parameters { get; }
    PluginReadRequest CreateRequest(IReadOnlyDictionary<string, string> parameters);

    PluginReadRequest IReadOnlyFeatureProvider.CreateRequest() =>
        throw new NotSupportedException("Parameterized read providers require action parameters.");
}

public enum PluginMutationRisk
{
    Low,
    Elevated,
    Destructive
}

public sealed record PluginMutationRequest(
    string Title,
    string Purpose,
    PluginMutationRisk Risk,
    string Executable,
    ImmutableArray<PluginCommandArgument> Arguments,
    TimeSpan Timeout);

public sealed record PluginMutationParameter(
    string Name,
    string Label,
    bool IsRequired = true);

public interface IMutationFeatureProvider
{
    string FeatureId { get; }

    string MutationId { get; }

    string ProviderId { get; }

    int Priority { get; }

    string Title { get; }

    string Purpose { get; }

    string ParameterName { get; }

    string ParameterLabel { get; }

    IReadOnlyList<PluginMutationParameter> Parameters =>
        [new(ParameterName, ParameterLabel)];

    PluginMutationRisk Risk { get; }

    bool RequiresElevation { get; }

    bool DisplaysOutput => false;

    IReadOnlySet<string> RequiredCapabilities { get; }

    PluginMutationRequest CreateRequest(
        IReadOnlyDictionary<string, string> parameters);
}

public interface IMultiCommandMutationFeatureProvider : IMutationFeatureProvider
{
    ImmutableArray<PluginMutationRequest> CreateRequests(
        IReadOnlyDictionary<string, string> parameters);

    PluginMutationRequest IMutationFeatureProvider.CreateRequest(
        IReadOnlyDictionary<string, string> parameters) =>
        throw new NotSupportedException(
            "Multi-command mutation providers construct a request sequence.");
}

public interface IFeatureProviderRegistry
{
    void Add(ILocalHostFeatureProvider provider);

    void Add(IReadOnlyFeatureProvider provider);

    void Add(IMutationFeatureProvider provider);
}

public sealed record RegisteredReadFeatureProvider(
    string PluginId,
    Version PluginVersion,
    ImmutableHashSet<string> Permissions,
    IReadOnlyFeatureProvider Provider);

public sealed record RegisteredLocalHostFeatureProvider(
    string PluginId,
    Version PluginVersion,
    ImmutableHashSet<string> Permissions,
    ILocalHostFeatureProvider Provider);

public sealed record RegisteredMutationFeatureProvider(
    string PluginId,
    Version PluginVersion,
    ImmutableHashSet<string> Permissions,
    IMutationFeatureProvider Provider);

public interface IPluginFeatureCatalog
{
    ImmutableArray<RegisteredLocalHostFeatureProvider> LocalHostSnapshot => [];

    ImmutableArray<RegisteredReadFeatureProvider> Snapshot { get; }

    ImmutableArray<RegisteredMutationFeatureProvider> MutationSnapshot { get; }

    void Register(
        string pluginId,
        Version pluginVersion,
        IReadOnlySet<string> permissions,
        IEnumerable<IReadOnlyFeatureProvider> readProviders,
        IEnumerable<IMutationFeatureProvider> mutationProviders);

    void Register(
        string pluginId,
        Version pluginVersion,
        IReadOnlySet<string> permissions,
        IEnumerable<ILocalHostFeatureProvider> localHostProviders,
        IEnumerable<IReadOnlyFeatureProvider> readProviders,
        IEnumerable<IMutationFeatureProvider> mutationProviders)
    {
        ArgumentNullException.ThrowIfNull(localHostProviders);
        if (localHostProviders.Any())
        {
            throw new NotSupportedException(
                "This feature catalog does not support local host providers.");
        }

        Register(
            pluginId,
            pluginVersion,
            permissions,
            readProviders,
            mutationProviders);
    }

    void RemovePlugin(string pluginId);
}

public interface INavigationRegistry
{
    void AddPage(string route, string title, string icon, Type viewModelType);

    void AddFeaturePage(
        string route,
        string title,
        string icon,
        Type viewModelType,
        string featureId);
}

public interface ICommandRegistry
{
    void Add(string id, string title, Type handlerType);
}

public interface ICapabilityRegistry
{
    void Require(string capability);
    void Provide(string capability);
}

public interface ISettingsRegistry
{
    void AddPage(string id, string title, string description, Type viewModelType, int sortOrder = 0);
}

public interface IAsyncPluginLifecycle
{
    Task ActivateAsync(CancellationToken cancellationToken);

    Task DeactivateAsync(CancellationToken cancellationToken);
}
