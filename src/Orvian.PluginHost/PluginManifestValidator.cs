using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;

namespace Orvian.PluginHost;

public sealed class PluginManifestValidator(Version supportedApiVersion)
{
    public ImmutableArray<PluginDiagnostic> Validate(
        PluginManifestDocument manifest,
        string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var diagnostics = ImmutableArray.CreateBuilder<PluginDiagnostic>();

        Require(manifest.Id, "manifest.id_required", "Plugin ID is required.", diagnostics);
        Require(manifest.Name, "manifest.name_required", "Plugin name is required.", diagnostics);
        Require(manifest.Publisher, "manifest.publisher_required", "Plugin publisher is required.", diagnostics);
        Require(manifest.EntryAssembly, "manifest.assembly_required", "Entry assembly is required.", diagnostics);
        Require(manifest.EntryType, "manifest.type_required", "Entry type is required.", diagnostics);

        if (!Version.TryParse(manifest.Version, out _))
        {
            diagnostics.Add(new("manifest.version_invalid", "Plugin version is invalid."));
        }

        if (!Version.TryParse(manifest.OrvianApiVersion, out var apiVersion))
        {
            diagnostics.Add(new("manifest.api_version_invalid", "Orvian API version is invalid."));
        }
        else if (apiVersion.Major != supportedApiVersion.Major ||
                 apiVersion.Minor > supportedApiVersion.Minor)
        {
            diagnostics.Add(new(
                "manifest.api_incompatible",
                $"Plugin requires Orvian API {apiVersion}; supported API is {supportedApiVersion}."));
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            var root = Path.GetFullPath(pluginDirectory);
            var candidate = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(candidate), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(
                    "manifest.assembly_path_invalid",
                    "Entry assembly must be a DLL inside the plugin directory."));
            }
        }

        foreach (var permission in manifest.Permissions)
        {
            if (!PluginPermissions.Known.Contains(permission))
            {
                diagnostics.Add(new(
                    "manifest.permission_unknown",
                    $"Plugin declares unknown permission '{permission}'."));
            }
        }

        AddDuplicates(manifest.Permissions, "manifest.permission_duplicate", "permission", diagnostics);
        AddDuplicates(manifest.Dependencies, "manifest.dependency_duplicate", "dependency", diagnostics);

        if (manifest.Dependencies.Contains(manifest.Id, StringComparer.Ordinal))
        {
            diagnostics.Add(new(
                "manifest.self_dependency",
                "Plugin cannot depend on itself."));
        }

        return diagnostics.ToImmutable();
    }

    private static void Require(
        string value,
        string code,
        string message,
        ICollection<PluginDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new(code, message));
        }
    }

    private static void AddDuplicates(
        IEnumerable<string> values,
        string code,
        string kind,
        ICollection<PluginDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                diagnostics.Add(new(code, $"Duplicate {kind} '{value}'."));
            }
        }
    }
}
