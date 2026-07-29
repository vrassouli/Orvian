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
    string Description);

public interface IPluginBuilder
{
    INavigationRegistry Navigation { get; }
    ICommandRegistry Commands { get; }
    ICapabilityRegistry Capabilities { get; }
}

public interface INavigationRegistry
{
    void AddPage(string route, string title, string icon, Type viewModelType);
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
