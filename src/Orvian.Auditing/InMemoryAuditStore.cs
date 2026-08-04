using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Auditing;

public sealed record ActivityQuery(
    string? HostProfileId = null,
    string? PluginId = null,
    InvocationSource? InvocationSource = null,
    AuditStatus? Status = null,
    PrivilegeLevel? Privilege = null,
    OperationRisk? Risk = null,
    DateTimeOffset? StartedAtOrAfter = null,
    DateTimeOffset? StartedBefore = null,
    bool IncludeDiagnostics = false,
    int Offset = 0,
    int Limit = 100,
    string? SearchText = null,
    bool ExcludeOperationCommands = false);

public sealed record ActivityItem(
    Guid CommandId,
    Guid OperationId,
    string HostProfileId,
    string RemoteUserName,
    string PluginId,
    string Executable,
    ImmutableArray<string> RedactedArguments,
    InvocationSource InvocationSource,
    AuditStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    bool OutputTruncated,
    CommandFailureClassification FailureClassification,
    string? SafeFailureMessage);

public interface IActivityReader
{
    Task<IReadOnlyList<ActivityItem>> QueryAsync(
        ActivityQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record OperationAuditQuery(
    string? HostProfileId = null,
    string? PluginId = null,
    InvocationSource? InvocationSource = null,
    OperationAuditStatus? Status = null,
    PrivilegeLevel? Privilege = null,
    OperationRisk? Risk = null,
    DateTimeOffset? StartedAtOrAfter = null,
    DateTimeOffset? StartedBefore = null,
    int Offset = 0,
    int Limit = 100,
    string? SearchText = null);

public interface IOperationAuditReader
{
    Task<IReadOnlyList<OperationAuditEntry>> QueryOperationsAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryAuditStore :
    IAuditSink,
    IOperationAuditSink,
    IActivityReader,
    IOperationAuditReader,
    ICommandAuditDetailReader
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CommandAuditEntry> _entries = [];
    private readonly Dictionary<Guid, OperationAuditEntry> _operations = [];

    public Task OperationStartedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (entry.Status != OperationAuditStatus.Started ||
                entry.CompletedAt is not null ||
                InvalidRemoteUserName(entry.RemoteUserName) ||
                !_operations.TryAdd(entry.OperationId, entry))
            {
                throw new InvalidOperationException(
                    "Operation audit start entry is invalid or duplicated.");
            }
        }

        return Task.CompletedTask;
    }

    public Task OperationCompletedAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(entry.OperationId, out var started) ||
                started.Status != OperationAuditStatus.Started ||
                entry.Status == OperationAuditStatus.Started ||
                entry.CompletedAt is null ||
                !string.Equals(
                    started.HostProfileId,
                    entry.HostProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    started.RemoteUserName,
                    entry.RemoteUserName,
                    StringComparison.Ordinal) ||
                !string.Equals(started.PluginId, entry.PluginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Operation audit completion does not match a pending start.");
            }

            _operations[entry.OperationId] = entry;
        }

        return Task.CompletedTask;
    }

    public Task CommandStartedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (entry.Status != AuditStatus.Started)
            {
                throw new InvalidOperationException("Audit start entry must have Started status.");
            }

            if (InvalidRemoteUserName(entry.RemoteUserName))
            {
                throw new InvalidOperationException(
                    "Audit start entry must identify the authenticated remote user.");
            }

            if (!_entries.TryAdd(entry.CommandId, entry))
            {
                throw new InvalidOperationException(
                    $"Command '{entry.CommandId}' already has an audit start entry.");
            }
        }

        return Task.CompletedTask;
    }

    public Task CommandCompletedAsync(
        CommandAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.CommandId, out var started))
            {
                throw new InvalidOperationException(
                    $"Command '{entry.CommandId}' has no audit start entry.");
            }

            if (started.OperationId != entry.OperationId ||
                !string.Equals(started.PluginId, entry.PluginId, StringComparison.Ordinal) ||
                !string.Equals(
                    started.HostProfileId,
                    entry.HostProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    started.RemoteUserName,
                    entry.RemoteUserName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Audit completion identity does not match its start entry.");
            }

            if (entry.CompletedAt is null || entry.Status == AuditStatus.Started)
            {
                throw new InvalidOperationException(
                    "Audit completion must contain terminal status and completion time.");
            }

            _entries[entry.CommandId] = entry;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityItem>> QueryAsync(
        ActivityQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var items = _entries.Values
                .Where(entry =>
                    query.HostProfileId is null ||
                    string.Equals(
                        entry.HostProfileId,
                        query.HostProfileId,
                        StringComparison.Ordinal))
                .Where(entry =>
                    query.PluginId is null ||
                    string.Equals(entry.PluginId, query.PluginId, StringComparison.Ordinal))
                .Where(entry =>
                    query.InvocationSource is null ||
                    entry.InvocationSource == query.InvocationSource)
                .Where(entry => query.Status is null || entry.Status == query.Status)
                .Where(entry =>
                    query.Privilege is null ||
                    entry.Privilege == query.Privilege)
                .Where(entry =>
                    query.Risk is null ||
                    (_operations.TryGetValue(entry.OperationId, out var operation) &&
                     operation.Risk == query.Risk))
                .Where(entry =>
                    query.StartedAtOrAfter is null ||
                    entry.StartedAt >= query.StartedAtOrAfter)
                .Where(entry =>
                    query.StartedBefore is null ||
                    entry.StartedAt < query.StartedBefore)
                .Where(entry =>
                    query.IncludeDiagnostics ||
                    entry.InvocationSource != InvocationSource.Discovery)
                .Where(entry =>
                    !query.ExcludeOperationCommands ||
                    !_operations.ContainsKey(entry.OperationId))
                .Where(entry => MatchesSearch(
                    query.SearchText,
                    entry.RemoteUserName,
                    entry.PluginId,
                    entry.Executable,
                    string.Join(' ', entry.RedactedArguments)))
                .OrderByDescending(entry => entry.StartedAt)
                .ThenBy(entry => entry.CommandId)
                .Skip(query.Offset)
                .Take(query.Limit)
                .Select(ToActivityItem)
                .ToArray();
            return Task.FromResult<IReadOnlyList<ActivityItem>>(items);
        }
    }

    public Task<IReadOnlyList<OperationAuditEntry>> QueryOperationsAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 ||
            query.Limit is < 1 or > 500 ||
            query.SearchText?.Length > 200 ||
            InvalidDateRange(query.StartedAtOrAfter, query.StartedBefore))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OperationAuditEntry>>(
                _operations.Values
                    .Where(entry =>
                        query.HostProfileId is null ||
                        string.Equals(
                            entry.HostProfileId,
                            query.HostProfileId,
                            StringComparison.Ordinal))
                    .Where(entry =>
                        query.PluginId is null ||
                        string.Equals(
                            entry.PluginId,
                            query.PluginId,
                            StringComparison.Ordinal))
                    .Where(entry =>
                        query.InvocationSource is null ||
                        entry.InvocationSource == query.InvocationSource)
                    .Where(entry =>
                        query.Status is null || entry.Status == query.Status)
                    .Where(entry =>
                        query.Privilege is null ||
                        entry.Privilege == query.Privilege)
                    .Where(entry =>
                        query.Risk is null || entry.Risk == query.Risk)
                    .Where(entry =>
                        query.StartedAtOrAfter is null ||
                        entry.StartedAt >= query.StartedAtOrAfter)
                    .Where(entry =>
                        query.StartedBefore is null ||
                        entry.StartedAt < query.StartedBefore)
                    .Where(entry => MatchesSearch(
                        query.SearchText,
                        entry.RemoteUserName,
                        entry.PluginId,
                        entry.Title,
                        entry.Purpose))
                    .OrderByDescending(entry => entry.StartedAt)
                    .ThenBy(entry => entry.OperationId)
                    .Skip(query.Offset)
                    .Take(query.Limit)
                    .ToArray());
        }
    }

    public Task<CommandAuditEntry?> GetCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _entries.TryGetValue(commandId, out var entry) ? entry : null);
        }
    }

    private static void ValidateQuery(ActivityQuery query)
    {
        if (query.Offset < 0 ||
            query.Limit is < 1 or > 500 ||
            query.SearchText?.Length > 200 ||
            InvalidDateRange(query.StartedAtOrAfter, query.StartedBefore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "Activity offset must be nonnegative and limit must be between 1 and 500.");
        }
    }

    private static bool InvalidDateRange(
        DateTimeOffset? startedAtOrAfter,
        DateTimeOffset? startedBefore) =>
        startedAtOrAfter is not null &&
        startedBefore is not null &&
        startedAtOrAfter >= startedBefore;

    private static bool MatchesSearch(string? searchText, params string[] values)
    {
        var terms = (searchText ?? string.Empty).Split(
            ' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0 ||
            terms.All(term => values.Any(value =>
                value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static ActivityItem ToActivityItem(CommandAuditEntry entry) =>
        new(
            entry.CommandId,
            entry.OperationId,
            entry.HostProfileId,
            entry.RemoteUserName,
            entry.PluginId,
            entry.Executable,
            entry.RedactedArguments,
            entry.InvocationSource,
            entry.Status,
            entry.StartedAt,
            entry.CompletedAt,
            entry.ExitCode,
            entry.OutputTruncated,
            entry.FailureClassification,
            entry.SafeFailureMessage);

    private static bool InvalidRemoteUserName(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length > 256 ||
        value.IndexOfAny(['\0', '\r', '\n']) >= 0;
}
