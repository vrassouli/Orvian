namespace Orvian.UI.Abstractions;

public enum ShellContentState
{
    Empty,
    Loading,
    Disconnected,
    Unsupported,
    Error,
    PermissionDenied,
    Ready
}

public sealed record ShellNavigationItem(
    string Id,
    string Title,
    string AccessibleName,
    string? FeatureId = null);
