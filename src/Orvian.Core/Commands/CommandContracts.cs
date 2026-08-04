using System.Collections.Immutable;

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

public enum CommandKind
{
    ReadOnly,
    Mutation
}

public enum OperationRisk
{
    Informational,
    Low,
    Elevated,
    Destructive
}

public enum ConfirmationRequirement
{
    None,
    Optional,
    Required,
    ExplicitDestructive
}

public enum CommandStatus
{
    Succeeded,
    ValidationDenied,
    PermissionDenied,
    AuditUnavailable,
    Failed,
    Cancelled,
    CancellationUncertain,
    TimedOut,
    Interrupted,
    AuditCompletionFailed,
    PrivilegeDenied
}

public enum CommandFailureClassification
{
    None,
    Validation,
    Policy,
    AuditPersistence,
    Transport,
    NonzeroExit,
    Cancellation,
    Timeout,
    ConnectionInterruption,
    Unexpected,
    PrivilegeDenied
}

public enum RemoteTerminationCertainty
{
    NotApplicable,
    Confirmed,
    Uncertain
}

public sealed record CommandArgument(string Value, bool IsSensitive = false);

public sealed class SensitiveStandardInput : IDisposable
{
    private char[]? _buffer;

    public SensitiveStandardInput(ReadOnlySpan<char> value)
    {
        _buffer = value.ToArray();
    }

    public ReadOnlyMemory<char> Memory =>
        _buffer ?? throw new ObjectDisposedException(nameof(SensitiveStandardInput));

    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        Array.Clear(_buffer);
        _buffer = null;
    }
}

public sealed record CommandRequest
{
    public required Guid OperationId { get; init; }

    public required string HostProfileId { get; init; }

    public required string ConnectionId { get; init; }

    public required string PluginId { get; init; }

    public required Version PluginVersion { get; init; }

    public required string RequiredPermission { get; init; }

    public required string Executable { get; init; }

    public ImmutableArray<CommandArgument> Arguments { get; init; } = [];

    public CommandKind Kind { get; init; } = CommandKind.ReadOnly;

    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.User;

    public InvocationSource InvocationSource { get; init; } = InvocationSource.UserInterface;

    public OutputLoggingMode OutputLogging { get; init; } = OutputLoggingMode.Full;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public string? Reason { get; init; }

    public SensitiveStandardInput? StandardInput { get; init; }
}

public sealed record CapturedOutput(
    string Content,
    long ObservedBytes,
    bool IsTruncated);

public sealed record CommandResult
{
    public required Guid CommandId { get; init; }

    public required Guid OperationId { get; init; }

    public required CommandStatus Status { get; init; }

    public int? ExitCode { get; init; }

    public CapturedOutput StandardOutput { get; init; } = new(string.Empty, 0, false);

    public CapturedOutput StandardError { get; init; } = new(string.Empty, 0, false);

    public TimeSpan Duration { get; init; }

    public CommandFailureClassification FailureClassification { get; init; }

    public string? SafeFailureMessage { get; init; }

    public RemoteTerminationCertainty RemoteTerminationCertainty { get; init; }

    public bool IsSuccess => Status == CommandStatus.Succeeded && ExitCode == 0;
}

public sealed record TransportCommandResult(
    int ExitCode,
    CapturedOutput StandardOutput,
    CapturedOutput StandardError);

public sealed record PermissionDecision(bool IsAllowed, string? SafeReason = null)
{
    public static PermissionDecision Allow { get; } = new(true);

    public static PermissionDecision Deny(string safeReason) => new(false, safeReason);
}

public interface ICommandPermissionPolicy
{
    PermissionDecision Authorize(CommandRequest request);
}

public interface ICommandTransport
{
    Task<TransportCommandResult> ExecuteAsync(
        Guid commandId,
        CommandRequest request,
        CancellationToken cancellationToken);
}

public interface ICommandExecutor
{
    Task<CommandResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PrivilegePreparationResult(
    bool IsAllowed,
    CommandRequest? TransportRequest,
    string? SafeFailureMessage = null)
{
    public static PrivilegePreparationResult Allow(CommandRequest request) =>
        new(true, request);

    public static PrivilegePreparationResult Deny(string safeFailureMessage) =>
        new(false, null, safeFailureMessage);
}

public interface IPrivilegeCommandPreparer
{
    ValueTask<PrivilegePreparationResult> PrepareAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteCommandInterruptionException(string safeMessage)
    : Exception(safeMessage);
