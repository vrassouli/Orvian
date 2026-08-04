using System.Xml.Linq;
using Xunit;

namespace Orvian.Architecture.Tests;

public sealed class ProjectBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Repository_projects_obey_dependency_boundaries()
    {
        var violations = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .SelectMany(ProjectBoundaryRules.Validate)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Architecture violations:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Deliberately_forbidden_plugin_reference_is_detected()
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../src/Orvian.Ssh/Orvian.Ssh.csproj" />
                <PackageReference Include="SSH.NET" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """;

        var violations = ProjectBoundaryRules.Validate(
            "/repo/plugins/Orvian.Bad/Orvian.Bad.csproj",
            XDocument.Parse(project));

        Assert.Contains(violations, violation => violation.Contains("Orvian.Ssh"));
        Assert.Contains(violations, violation => violation.Contains("SSH.NET"));
    }

    private static bool IsBuildArtifact(string path) =>
        path.Split(Path.DirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orvian.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

internal static class ProjectBoundaryRules
{
    private static readonly string[] PluginForbiddenProjects =
    [
        "Orvian.Ssh",
        "Orvian.Persistence"
    ];

    private static readonly string[] PluginForbiddenPackages =
    [
        "SSH.NET",
        "Microsoft.Data.Sqlite",
        "System.Data.SQLite"
    ];

    private static readonly string[] ContractForbiddenPackages =
    [
        "Avalonia",
        "SSH.NET",
        "Microsoft.Data.Sqlite",
        "System.Data.SQLite"
    ];

    public static IEnumerable<string> Validate(string projectPath)
    {
        using var stream = File.OpenRead(projectPath);
        return Validate(projectPath, XDocument.Load(stream)).ToArray();
    }

    public static IEnumerable<string> Validate(string projectPath, XDocument document)
    {
        var normalizedPath = projectPath.Replace('\\', '/');
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var projectReferences = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        var packageReferences = document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        if (normalizedPath.Contains("/plugins/", StringComparison.Ordinal))
        {
            foreach (var forbidden in PluginForbiddenProjects)
            {
                if (projectReferences.Any(reference =>
                        reference.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return $"{projectName}: plugin references forbidden project {forbidden}.";
                }
            }

            foreach (var forbidden in PluginForbiddenPackages)
            {
                if (packageReferences.Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                {
                    yield return $"{projectName}: plugin references forbidden package {forbidden}.";
                }
            }
        }

        if (normalizedPath.Contains("/src/", StringComparison.Ordinal) &&
            projectReferences.Any(reference =>
                reference.Replace('\\', '/')
                    .Contains("/plugins/", StringComparison.OrdinalIgnoreCase)))
        {
            yield return $"{projectName}: core/platform project references a concrete plugin.";
        }

        if (projectName == "Orvian.Core")
        {
            foreach (var forbidden in ContractForbiddenPackages)
            {
                if (packageReferences.Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                {
                    yield return $"Orvian.Core references forbidden package {forbidden}.";
                }
            }

            if (projectReferences.Any(reference =>
                    reference.Contains(
                        "Orvian.Plugin.Abstractions",
                        StringComparison.OrdinalIgnoreCase)))
            {
                yield return "Orvian.Core must not depend on plugin abstractions.";
            }
        }

        if (projectName is
            "Orvian.Plugin.Abstractions" or
            "Orvian.UI.Abstractions" or
            "Orvian.Diagnostics")
        {
            foreach (var forbidden in ContractForbiddenPackages)
            {
                if (packageReferences.Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                {
                    yield return $"{projectName} references platform package {forbidden}.";
                }
            }
        }

        if (projectName == "Orvian.Shell" &&
            projectReferences.Any(reference =>
                reference.Contains("Orvian.Plugins.", StringComparison.OrdinalIgnoreCase)))
        {
            yield return "Orvian.Shell references a concrete plugin.";
        }
    }
}
