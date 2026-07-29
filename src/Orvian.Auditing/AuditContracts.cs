using Orvian.Core.Commands;

namespace Orvian.Auditing;

public enum AuditStatus
{
    Started,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Interrupted,
    PrivilegeDenied
}

public sealed record CommandAuditEntry
{
    public required Guid Id { get; init; }
    public required Guid OperationId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string HostId { get; init; }
    public required string HostName { get; init; }
    public required string SshUser { get; init; }
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public PrivilegeLevel Privilege { get; init; }
    public InvocationSource InvocationSource { get; init; }
    public AuditStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
    public string? FailureReason { get; init; }
}

public interface IAuditSink
{
    Task CommandStartedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default);
    Task CommandCompletedAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default);
}
