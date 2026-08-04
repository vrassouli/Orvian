using System.Collections.Immutable;
using System.Diagnostics;
using Orvian.Auditing;
using Orvian.Core.Commands;
using Orvian.Security;

namespace Orvian.Application.Execution;

public sealed class CommandExecutor(
    ICommandPermissionPolicy permissionPolicy,
    IAuditSink auditSink,
    IAuditExecutionIdentityResolver identityResolver,
    ICommandTransport transport,
    IPrivilegeCommandPreparer? privilegePreparer = null,
    TimeProvider? timeProvider = null) : ICommandExecutor
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IPrivilegeCommandPreparer _privilegePreparer =
        privilegePreparer ?? DenyingPrivilegeCommandPreparer.Instance;

    public async Task<CommandResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var commandId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        var validationMessage = Validate(request);
        if (validationMessage is not null)
        {
            return Failure(
                commandId,
                request.OperationId,
                CommandStatus.ValidationDenied,
                CommandFailureClassification.Validation,
                validationMessage,
                stopwatch.Elapsed);
        }

        var permission = permissionPolicy.Authorize(request);
        if (!permission.IsAllowed)
        {
            return Failure(
                commandId,
                request.OperationId,
                CommandStatus.PermissionDenied,
                CommandFailureClassification.Policy,
                permission.SafeReason ?? "The plugin is not allowed to execute this command.",
                stopwatch.Elapsed);
        }

        AuditExecutionIdentity? identity;
        try
        {
            identity = await identityResolver.ResolveAsync(
                request.HostProfileId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            identity = null;
        }

        if (identity is null || !ValidRemoteUserName(identity.RemoteUserName))
        {
            return Failure(
                commandId,
                request.OperationId,
                CommandStatus.AuditUnavailable,
                CommandFailureClassification.AuditPersistence,
                "The command was blocked because its authenticated user identity could not be resolved for audit.",
                stopwatch.Elapsed);
        }

        var startEntry = CreateStartEntry(commandId, request, identity, startedAt);
        try
        {
            await auditSink.CommandStartedAsync(startEntry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(
                commandId,
                request.OperationId,
                CommandStatus.AuditUnavailable,
                CommandFailureClassification.AuditPersistence,
                request.Kind == CommandKind.Mutation
                    ? "The change was blocked because its audit start could not be saved."
                    : "The command was blocked because its audit start could not be saved.",
                stopwatch.Elapsed);
        }

        CommandResult result;
        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            var privilege = await _privilegePreparer.PrepareAsync(
                request,
                linkedSource.Token).ConfigureAwait(false);
            if (!privilege.IsAllowed || privilege.TransportRequest is null)
            {
                result = Failure(
                    commandId,
                    request.OperationId,
                    CommandStatus.PrivilegeDenied,
                    CommandFailureClassification.PrivilegeDenied,
                    privilege.SafeFailureMessage ??
                        "Required privilege is not available for this connection.",
                    stopwatch.Elapsed);
            }
            else
            {
                try
                {
                    var transportResult = await transport.ExecuteAsync(
                        commandId,
                        privilege.TransportRequest,
                        linkedSource.Token).ConfigureAwait(false);
                    result = FromTransport(
                        commandId,
                        request.OperationId,
                        privilege.TransportRequest,
                        transportResult,
                        stopwatch.Elapsed);
                }
                finally
                {
                    privilege.TransportRequest.StandardInput?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = Failure(
                commandId,
                request.OperationId,
                CommandStatus.TimedOut,
                CommandFailureClassification.Timeout,
                "The remote command timed out.",
                stopwatch.Elapsed) with
            {
                RemoteTerminationCertainty = RemoteTerminationCertainty.Uncertain
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = Failure(
                commandId,
                request.OperationId,
                CommandStatus.CancellationUncertain,
                CommandFailureClassification.Cancellation,
                "Cancellation was requested, but remote termination could not be confirmed.",
                stopwatch.Elapsed) with
            {
                RemoteTerminationCertainty = RemoteTerminationCertainty.Uncertain
            };
        }
        catch (RemoteCommandInterruptionException exception)
        {
            result = Failure(
                commandId,
                request.OperationId,
                CommandStatus.Interrupted,
                CommandFailureClassification.ConnectionInterruption,
                exception.Message,
                stopwatch.Elapsed);
        }
        catch (Exception)
        {
            result = Failure(
                commandId,
                request.OperationId,
                CommandStatus.Failed,
                CommandFailureClassification.Transport,
                "The remote command could not be executed.",
                stopwatch.Elapsed);
        }

        stopwatch.Stop();
        result = result with { Duration = stopwatch.Elapsed };
        try
        {
            await auditSink.CommandCompletedAsync(
                CreateCompletionEntry(startEntry, result, _timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return result with
            {
                Status = CommandStatus.AuditCompletionFailed,
                FailureClassification = CommandFailureClassification.AuditPersistence,
                SafeFailureMessage =
                    "The command finished, but its audit completion could not be saved."
            };
        }

        return result;
    }

    private static string? Validate(CommandRequest request)
    {
        if (request.OperationId == Guid.Empty)
        {
            return "Operation ID is required.";
        }

        if (string.IsNullOrWhiteSpace(request.HostProfileId) ||
            string.IsNullOrWhiteSpace(request.ConnectionId) ||
            string.IsNullOrWhiteSpace(request.PluginId) ||
            string.IsNullOrWhiteSpace(request.RequiredPermission))
        {
            return "Host, connection, plugin, and permission identity are required.";
        }

        if (string.IsNullOrWhiteSpace(request.Executable) ||
            ContainsControlSeparator(request.Executable))
        {
            return "Executable is invalid.";
        }

        if (request.Arguments.Any(argument =>
                argument.Value is null || argument.Value.Contains('\0')))
        {
            return "A command argument is invalid.";
        }

        if (request.StandardInput is not null)
        {
            return "Standard input is reserved for core execution services.";
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > MaximumTimeout)
        {
            return "Command timeout is outside the allowed range.";
        }

        if (request.Privilege != PrivilegeLevel.User &&
            string.IsNullOrWhiteSpace(request.Reason))
        {
            return "Privileged commands require a reason.";
        }

        return null;
    }

    private static bool ContainsControlSeparator(string value) =>
        value.Contains('\0') || value.Contains('\r') || value.Contains('\n');

    private static bool ValidRemoteUserName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !ContainsControlSeparator(value);

    private static CommandAuditEntry CreateStartEntry(
        Guid commandId,
        CommandRequest request,
        AuditExecutionIdentity identity,
        DateTimeOffset startedAt) =>
        new()
        {
            CommandId = commandId,
            OperationId = request.OperationId,
            StartedAt = startedAt,
            HostProfileId = request.HostProfileId,
            ConnectionId = request.ConnectionId,
            RemoteUserName = identity.RemoteUserName,
            PluginId = request.PluginId,
            PluginVersion = request.PluginVersion.ToString(),
            Executable = request.Executable,
            RedactedArguments =
            [
                .. request.Arguments.Select(argument =>
                    argument.IsSensitive ? "[REDACTED]" : argument.Value)
            ],
            Privilege = request.Privilege,
            OutputLogging = request.OutputLogging,
            InvocationSource = request.InvocationSource,
            Status = AuditStatus.Started
        };

    private static CommandAuditEntry CreateCompletionEntry(
        CommandAuditEntry startEntry,
        CommandResult result,
        DateTimeOffset completedAt) =>
        startEntry with
        {
            CompletedAt = completedAt,
            Status = MapAuditStatus(result.Status),
            ExitCode = result.ExitCode,
            StandardOutputBytes = result.StandardOutput.ObservedBytes,
            StandardErrorBytes = result.StandardError.ObservedBytes,
            OutputTruncated =
                result.StandardOutput.IsTruncated || result.StandardError.IsTruncated,
            RetainedStandardOutput = RetainOutput(
                startEntry.OutputLogging,
                result.StandardOutput),
            RetainedStandardError = RetainOutput(
                startEntry.OutputLogging,
                result.StandardError),
            FailureClassification = result.FailureClassification,
            SafeFailureMessage = result.SafeFailureMessage
        };

    private static string? RetainOutput(
        OutputLoggingMode mode,
        CapturedOutput output) =>
        mode switch
        {
            OutputLoggingMode.Full => output.Content,
            OutputLoggingMode.Redacted when output.Content.Length > 0 =>
                StructuralRedactor.Replacement,
            OutputLoggingMode.Redacted => string.Empty,
            OutputLoggingMode.MetadataOnly or OutputLoggingMode.Disabled => null,
            _ => null
        };

    private static CommandResult FromTransport(
        Guid commandId,
        Guid operationId,
        CommandRequest transportRequest,
        TransportCommandResult transportResult,
        TimeSpan duration)
    {
        var privilegeDenied =
            transportResult.ExitCode != 0 &&
            IsPrivilegeAuthenticationFailure(transportRequest, transportResult);
        return new()
        {
            CommandId = commandId,
            OperationId = operationId,
            Status = transportResult.ExitCode == 0
                ? CommandStatus.Succeeded
                : privilegeDenied
                    ? CommandStatus.PrivilegeDenied
                    : CommandStatus.Failed,
            ExitCode = transportResult.ExitCode,
            StandardOutput = transportResult.StandardOutput,
            StandardError = transportResult.StandardError,
            Duration = duration,
            FailureClassification = transportResult.ExitCode == 0
                ? CommandFailureClassification.None
                : privilegeDenied
                    ? CommandFailureClassification.PrivilegeDenied
                    : CommandFailureClassification.NonzeroExit,
            SafeFailureMessage = transportResult.ExitCode == 0
                ? null
                : privilegeDenied
                    ? "The privilege credential was missing or rejected."
                    : "The remote command returned a nonzero exit code."
        };
    }

    private static bool IsPrivilegeAuthenticationFailure(
        CommandRequest request,
        TransportCommandResult result)
    {
        if (request.Executable is not ("sudo" or "doas"))
        {
            return false;
        }

        var error = result.StandardError.Content;
        return error.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("sorry, try again", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("no tty present", StringComparison.OrdinalIgnoreCase);
    }

    private static CommandResult Failure(
        Guid commandId,
        Guid operationId,
        CommandStatus status,
        CommandFailureClassification classification,
        string message,
        TimeSpan duration) =>
        new()
        {
            CommandId = commandId,
            OperationId = operationId,
            Status = status,
            FailureClassification = classification,
            SafeFailureMessage = message,
            Duration = duration
        };

    private static AuditStatus MapAuditStatus(CommandStatus status) =>
        status switch
        {
            CommandStatus.Succeeded => AuditStatus.Succeeded,
            CommandStatus.ValidationDenied => AuditStatus.ValidationDenied,
            CommandStatus.PermissionDenied => AuditStatus.PermissionDenied,
            CommandStatus.AuditUnavailable => AuditStatus.AuditUnavailable,
            CommandStatus.Cancelled => AuditStatus.Cancelled,
            CommandStatus.CancellationUncertain => AuditStatus.Interrupted,
            CommandStatus.TimedOut => AuditStatus.TimedOut,
            CommandStatus.Interrupted => AuditStatus.Interrupted,
            CommandStatus.AuditCompletionFailed => AuditStatus.CompletionPersistenceFailed,
            _ => AuditStatus.Failed
        };

    private sealed class DenyingPrivilegeCommandPreparer : IPrivilegeCommandPreparer
    {
        public static DenyingPrivilegeCommandPreparer Instance { get; } = new();

        public ValueTask<PrivilegePreparationResult> PrepareAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                request.Privilege == PrivilegeLevel.User
                    ? PrivilegePreparationResult.Allow(request)
                    : PrivilegePreparationResult.Deny(
                        "Privilege elevation is not configured for this connection."));
        }
    }
}
