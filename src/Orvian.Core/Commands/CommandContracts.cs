namespace Orvian.Core.Commands;

public enum PrivilegeLevel
{
    User,
    Elevated,
    RootOnly
}

public enum InvocationSource
{
    UserInterface,
    BackgroundRefresh,
    ScheduledTask,
    PluginStartup,
    Discovery,
    AiAssistant,
    CommandPalette,
    Retry
}

public enum OutputLoggingMode
{
    Full,
    Redacted,
    MetadataOnly,
    Disabled
}

public sealed record CommandRequest
{
    public required string PluginId { get; init; }
    public required string OperationId { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.User;
    public InvocationSource InvocationSource { get; init; } = InvocationSource.UserInterface;
    public OutputLoggingMode OutputLogging { get; init; } = OutputLoggingMode.Full;
    public IReadOnlySet<int> SensitiveArgumentIndexes { get; init; } = new HashSet<int>();
    public string? Reason { get; init; }
}

public sealed record CommandResult(
    Guid CommandId,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool IsSuccess => ExitCode == 0;
}

public interface ICommandExecutor
{
    Task<CommandResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default);
}
