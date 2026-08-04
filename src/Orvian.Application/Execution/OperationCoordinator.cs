using Orvian.Core.Commands;
using Orvian.Auditing;
using Orvian.Security;

namespace Orvian.Application.Execution;

public sealed class OperationCoordinator(
    ICommandExecutor commandExecutor,
    IOperationAuditSink auditSink,
    IAuditExecutionIdentityResolver identityResolver,
    IOperationConfirmationService? confirmations = null,
    TimeProvider? timeProvider = null)
    : IOperationCoordinator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly OperationResourceLockManager _locks = new();

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        AuditExecutionIdentity? identity;
        try
        {
            identity = await identityResolver.ResolveAsync(
                request.Intent.HostProfileId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            identity = null;
        }

        if (identity is null ||
            string.IsNullOrWhiteSpace(identity.RemoteUserName) ||
            identity.RemoteUserName.Length > 256 ||
            identity.RemoteUserName.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return BuildResult(
                request.Intent.OperationId,
                [],
                OperationStatus.Denied);
        }

        var auditStart = CreateAuditStart(
            request.Intent,
            identity,
            _timeProvider.GetUtcNow());
        try
        {
            await auditSink.OperationStartedAsync(auditStart, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BuildResult(
                request.Intent.OperationId,
                [],
                OperationStatus.Denied);
        }

        var confirmationRequirement = ConfirmationPolicy.GetRequirement(
            request.Intent.Risk,
            request.Intent.Privilege);
        if (confirmationRequirement is not ConfirmationRequirement.None)
        {
            bool confirmed;
            try
            {
                confirmed = confirmations is not null &&
                    await confirmations.ConfirmAsync(
                        new(
                            request.Intent,
                            confirmationRequirement,
                            [
                                .. request.Commands.Select(command =>
                                    FormatRedactedCommand(command))
                            ]),
                        cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CompleteAuditAsync(
                    auditStart,
                    request.Intent.OperationId,
                    [],
                    OperationStatus.Cancelled,
                    "The operation was cancelled during confirmation.")
                    .ConfigureAwait(false);
            }

            if (!confirmed)
            {
                return await CompleteAuditAsync(
                    auditStart,
                    request.Intent.OperationId,
                    [],
                    OperationStatus.Denied,
                    "The operation was not confirmed.").ConfigureAwait(false);
            }
        }

        IAsyncDisposable? resourceLock = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Intent.ResourceLockKey))
            {
                resourceLock = await _locks.AcquireAsync(
                    request.Intent.ResourceLockKey,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return await CompleteAuditAsync(
                auditStart,
                request.Intent.OperationId,
                [],
                OperationStatus.Cancelled,
                "The operation was cancelled while waiting for its resource.")
                .ConfigureAwait(false);
        }

        await using var heldResourceLock = resourceLock;
        var results = new List<CommandResult>(request.Commands.Length);
        foreach (var command in request.Commands)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CompleteAuditAsync(
                    auditStart,
                    request.Intent.OperationId,
                    results,
                    OperationStatus.Cancelled,
                    "The operation was cancelled.").ConfigureAwait(false);
            }

            CommandResult result;
            try
            {
                result = await commandExecutor
                    .ExecuteAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CompleteAuditAsync(
                    auditStart,
                    request.Intent.OperationId,
                    results,
                    OperationStatus.Cancelled,
                    "The operation was cancelled.").ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await CompleteAuditAsync(
                    auditStart,
                    request.Intent.OperationId,
                    results,
                    OperationStatus.Failed,
                    "The operation failed unexpectedly.").ConfigureAwait(false);
            }
            results.Add(result);
            if (!result.IsSuccess)
            {
                break;
            }
        }

        var status = DetermineStatus(results, request.Commands.Length);
        return await CompleteAuditAsync(
            auditStart,
            request.Intent.OperationId,
            results,
            status,
            results.LastOrDefault()?.SafeFailureMessage).ConfigureAwait(false);
    }

    private static void Validate(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Intent);
        if (request.Intent.OperationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID is required.", nameof(request));
        }

        if (request.Commands.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Operation must contain at least one command.",
                nameof(request));
        }

        foreach (var command in request.Commands)
        {
            if (command.OperationId != request.Intent.OperationId ||
                !string.Equals(
                    command.HostProfileId,
                    request.Intent.HostProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    command.ConnectionId,
                    request.Intent.ConnectionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    command.PluginId,
                    request.Intent.PluginId,
                    StringComparison.Ordinal) ||
                command.PluginVersion != request.Intent.PluginVersion ||
                command.InvocationSource != request.Intent.InvocationSource)
            {
                throw new ArgumentException(
                    "Command ownership and target must match the operation intent.",
                    nameof(request));
            }

            if (!string.Equals(
                    command.RequiredPermission,
                    request.Intent.RequiredPermission,
                    StringComparison.Ordinal) ||
                command.Privilege != request.Intent.Privilege)
            {
                throw new ArgumentException(
                    "Command security requirements must match the operation intent.",
                    nameof(request));
            }
        }
    }

    private static OperationStatus DetermineStatus(
        IReadOnlyList<CommandResult> results,
        int requestedCommandCount)
    {
        if (results.Count == requestedCommandCount && results.All(result => result.IsSuccess))
        {
            return OperationStatus.Succeeded;
        }

        var terminal = results[^1].Status;
        if (results.Take(results.Count - 1).Any(result => result.IsSuccess))
        {
            return OperationStatus.PartiallySucceeded;
        }

        return terminal switch
        {
            CommandStatus.ValidationDenied or
            CommandStatus.PermissionDenied or
            CommandStatus.AuditUnavailable or
            CommandStatus.PrivilegeDenied => OperationStatus.Denied,
            CommandStatus.Cancelled or
            CommandStatus.CancellationUncertain => OperationStatus.Cancelled,
            CommandStatus.TimedOut => OperationStatus.TimedOut,
            CommandStatus.Interrupted => OperationStatus.Interrupted,
            _ => OperationStatus.Failed
        };
    }

    private static OperationResult BuildResult(
        Guid operationId,
        IEnumerable<CommandResult> results,
        OperationStatus status) =>
        new(operationId, status, [.. results]);

    private async Task<OperationResult> CompleteAuditAsync(
        OperationAuditEntry auditStart,
        Guid operationId,
        IEnumerable<CommandResult> results,
        OperationStatus status,
        string? safeFailureMessage)
    {
        var materialized = results.ToArray();
        var completion = auditStart with
        {
            CompletedAt = _timeProvider.GetUtcNow(),
            Status = MapAuditStatus(status),
            CommandCount = materialized.Length,
            SafeFailureMessage = safeFailureMessage
        };
        try
        {
            await auditSink.OperationCompletedAsync(completion, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return BuildResult(
                operationId,
                materialized,
                OperationStatus.AuditCompletionFailed);
        }

        return BuildResult(operationId, materialized, status);
    }

    private static OperationAuditEntry CreateAuditStart(
        OperationIntent intent,
        AuditExecutionIdentity identity,
        DateTimeOffset startedAt) =>
        new()
        {
            OperationId = intent.OperationId,
            StartedAt = startedAt,
            HostProfileId = intent.HostProfileId,
            ConnectionId = intent.ConnectionId,
            RemoteUserName = identity.RemoteUserName,
            PluginId = intent.PluginId,
            PluginVersion = intent.PluginVersion.ToString(),
            Title = intent.Title,
            Purpose = intent.Purpose,
            RequiredPermission = intent.RequiredPermission,
            Risk = intent.Risk,
            Privilege = intent.Privilege,
            InvocationSource = intent.InvocationSource,
            ResourceLockKey = intent.ResourceLockKey,
            Status = OperationAuditStatus.Started
        };

    private static OperationAuditStatus MapAuditStatus(OperationStatus status) =>
        status switch
        {
            OperationStatus.Succeeded => OperationAuditStatus.Succeeded,
            OperationStatus.PartiallySucceeded => OperationAuditStatus.PartiallySucceeded,
            OperationStatus.Denied => OperationAuditStatus.Denied,
            OperationStatus.Cancelled => OperationAuditStatus.Cancelled,
            OperationStatus.TimedOut => OperationAuditStatus.TimedOut,
            OperationStatus.Interrupted => OperationAuditStatus.Interrupted,
            OperationStatus.AuditCompletionFailed =>
                OperationAuditStatus.CompletionPersistenceFailed,
            _ => OperationAuditStatus.Failed
        };

    private static string FormatRedactedCommand(CommandRequest command) =>
        string.Join(
            ' ',
            new[] { command.Executable }.Concat(
                StructuralRedactor.RedactArguments(command.Arguments)));

    private sealed class OperationResourceLockManager
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries =
            new(StringComparer.Ordinal);

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            string key,
            CancellationToken cancellationToken)
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new();
                    _entries.Add(key, entry);
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Lease(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry, releaseSemaphore: false);
                throw;
            }
        }

        private void ReleaseReference(
            string key,
            Entry entry,
            bool releaseSemaphore)
        {
            if (releaseSemaphore)
            {
                entry.Semaphore.Release();
            }

            lock (_gate)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int ReferenceCount { get; set; }
        }

        private sealed class Lease(
            OperationResourceLockManager owner,
            string key,
            Entry entry) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.ReleaseReference(key, entry, releaseSemaphore: true);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
