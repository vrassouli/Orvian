using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Orvian.PluginHost;

public sealed record PluginManifestDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("orvianApiVersion")]
    public string OrvianApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("entryAssembly")]
    public string EntryAssembly { get; init; } = string.Empty;

    [JsonPropertyName("entryType")]
    public string EntryType { get; init; } = string.Empty;

    [JsonPropertyName("permissions")]
    public ImmutableArray<string> Permissions { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public ImmutableArray<string> Dependencies { get; init; } = [];
}

public enum PluginState
{
    Discovered,
    Validated,
    Compatible,
    Loaded,
    Configured,
    Active,
    Disabled,
    Incompatible,
    Quarantined,
    Faulted,
    Unloaded
}

public sealed record PluginDiagnostic(string Code, string Message);

public sealed record PluginLoadResult(
    string PluginId,
    PluginState State,
    ImmutableArray<PluginDiagnostic> Diagnostics);
