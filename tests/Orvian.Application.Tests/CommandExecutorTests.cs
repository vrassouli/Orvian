using Orvian.Application.Execution;
using Orvian.Auditing;
using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class CommandExecutorTests
{
    [Fact]
    public async Task Audit_start_happens_before_transport_and_completion_follows()
    {
        var events = new List<string>();
        var audit = new RecordingAuditSink(events);
        var transport = new DelegateTransport((_, _, _) =>
        {
            events.Add("transport");
            return Task.FromResult(SuccessfulTransportResult());
        });
        var executor = CreateExecutor(audit, transport);

        var result = await executor.ExecuteAsync(ValidRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(["audit-start", "transport", "audit-complete"], events);
        Assert.Equal(audit.Started!.CommandId, audit.Completed!.CommandId);
        Assert.Equal("operator", audit.Started.RemoteUserName);
    }

    [Fact]
    public async Task MissingAuthenticatedUserIdentityFailsClosedBeforeAuditAndTransport()
    {
        var audit = new RecordingAuditSink([]);
        var transport = new DelegateTransport((_, _, _) =>
            Task.FromResult(SuccessfulTransportResult()));
        var executor = new CommandExecutor(
            new FixedPermissionPolicy(PermissionDecision.Allow),
            audit,
            new FixedIdentityResolver(remoteUserName: null),
            transport);

        var result = await executor.ExecuteAsync(ValidRequest());

        Assert.Equal(CommandStatus.AuditUnavailable, result.Status);
        Assert.Equal(CommandFailureClassification.AuditPersistence, result.FailureClassification);
        Assert.Equal(0, audit.StartCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Mutation_fails_closed_when_audit_start_fails()
    {
        var transport = new DelegateTransport((_, _, _) =>
            throw new Xunit.Sdk.XunitException("Transport must not execute."));
        var executor = CreateExecutor(new FailingStartAuditSink(), transport);

        var result = await executor.ExecuteAsync(
            ValidRequest() with { Kind = CommandKind.Mutation });

        Assert.Equal(CommandStatus.AuditUnavailable, result.Status);
        Assert.Equal(
            CommandFailureClassification.AuditPersistence,
            result.FailureClassification);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Permission_denial_does_not_reach_audit_or_transport()
    {
        var audit = new RecordingAuditSink([]);
        var transport = new DelegateTransport((_, _, _) =>
            Task.FromResult(SuccessfulTransportResult()));
        var executor = new CommandExecutor(
            new FixedPermissionPolicy(PermissionDecision.Deny("Denied by test policy.")),
            audit,
            new FixedIdentityResolver(),
            transport);

        var result = await executor.ExecuteAsync(ValidRequest());

        Assert.Equal(CommandStatus.PermissionDenied, result.Status);
        Assert.Equal(0, audit.StartCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Privileged_command_fails_closed_and_is_audited_without_provider()
    {
        var audit = new RecordingAuditSink([]);
        var transport = new DelegateTransport((_, _, _) =>
            throw new Xunit.Sdk.XunitException("Transport must not execute."));
        var executor = CreateExecutor(audit, transport);

        var result = await executor.ExecuteAsync(
            ValidRequest() with
            {
                Privilege = PrivilegeLevel.Elevated,
                Reason = "Test elevated execution."
            });

        Assert.Equal(CommandStatus.PrivilegeDenied, result.Status);
        Assert.Equal(
            CommandFailureClassification.PrivilegeDenied,
            result.FailureClassification);
        Assert.Equal(1, audit.StartCount);
        Assert.NotNull(audit.Completed);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Prepared_sensitive_input_is_disposed_after_transport()
    {
        var input = new SensitiveStandardInput("privilege-secret\n");
        var transport = new DelegateTransport((_, request, _) =>
        {
            Assert.Equal(
                "privilege-secret\n",
                new string(request.StandardInput!.Memory.Span));
            return Task.FromResult(SuccessfulTransportResult());
        });
        var executor = new CommandExecutor(
            new FixedPermissionPolicy(PermissionDecision.Allow),
            new RecordingAuditSink([]),
            new FixedIdentityResolver(),
            transport,
            new FixedPrivilegePreparer(input));

        var result = await executor.ExecuteAsync(
            ValidRequest() with
            {
                Privilege = PrivilegeLevel.Elevated,
                Reason = "Test elevation."
            });

        Assert.True(result.IsSuccess);
        Assert.Throws<ObjectDisposedException>(() => input.Memory.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\ncommand")]
    [InlineData("bad\0command")]
    public async Task Invalid_executable_is_denied_before_transport(string executable)
    {
        var transport = new DelegateTransport((_, _, _) =>
            Task.FromResult(SuccessfulTransportResult()));
        var executor = CreateExecutor(new RecordingAuditSink([]), transport);

        var result = await executor.ExecuteAsync(
            ValidRequest() with { Executable = executable });

        Assert.Equal(CommandStatus.ValidationDenied, result.Status);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Sensitive_arguments_are_redacted_before_audit()
    {
        var audit = new RecordingAuditSink([]);
        var executor = CreateExecutor(
            audit,
            new DelegateTransport((_, _, _) =>
                Task.FromResult(SuccessfulTransportResult())));
        var request = ValidRequest() with
        {
            Arguments =
            [
                new("--user"),
                new("secret-value", IsSensitive: true)
            ]
        };

        await executor.ExecuteAsync(request);

        Assert.Equal(
            ["--user", "[REDACTED]"],
            audit.Started!.RedactedArguments.ToArray());
        Assert.DoesNotContain("secret-value", audit.Started.RedactedArguments);
    }

    [Theory]
    [InlineData(OutputLoggingMode.Full, "remote-secret", true)]
    [InlineData(OutputLoggingMode.Redacted, "[REDACTED]", true)]
    [InlineData(OutputLoggingMode.MetadataOnly, null, false)]
    [InlineData(OutputLoggingMode.Disabled, null, false)]
    public async Task OutputLoggingModeControlsRetainedAuditContent(
        OutputLoggingMode mode,
        string? expected,
        bool shouldRetain)
    {
        var audit = new RecordingAuditSink([]);
        var executor = CreateExecutor(
            audit,
            new DelegateTransport((_, _, _) =>
                Task.FromResult(new TransportCommandResult(
                    0,
                    new("remote-secret", 13, false),
                    new(string.Empty, 0, false)))));

        await executor.ExecuteAsync(
            ValidRequest() with { OutputLogging = mode });

        Assert.Equal(shouldRetain, audit.Completed!.RetainedStandardOutput is not null);
        Assert.Equal(expected, audit.Completed.RetainedStandardOutput);
        if (mode == OutputLoggingMode.Redacted)
        {
            Assert.DoesNotContain(
                "remote-secret",
                audit.Completed.RetainedStandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_distinct_from_timeout()
    {
        var transport = BlockingTransport();
        var executor = CreateExecutor(new RecordingAuditSink([]), transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await executor.ExecuteAsync(ValidRequest(), cancellation.Token);

        Assert.Equal(CommandStatus.CancellationUncertain, result.Status);
        Assert.Equal(CommandFailureClassification.Cancellation, result.FailureClassification);
        Assert.Equal(
            RemoteTerminationCertainty.Uncertain,
            result.RemoteTerminationCertainty);
    }

    [Fact]
    public async Task Timeout_is_distinct_from_cancellation()
    {
        var executor = CreateExecutor(
            new RecordingAuditSink([]),
            BlockingTransport());

        var result = await executor.ExecuteAsync(
            ValidRequest() with { Timeout = TimeSpan.FromMilliseconds(20) });

        Assert.Equal(CommandStatus.TimedOut, result.Status);
        Assert.Equal(CommandFailureClassification.Timeout, result.FailureClassification);
        Assert.Equal(
            RemoteTerminationCertainty.Uncertain,
            result.RemoteTerminationCertainty);
    }

    [Fact]
    public async Task Audit_completion_failure_is_not_reported_as_success()
    {
        var executor = CreateExecutor(
            new FailingCompletionAuditSink(),
            new DelegateTransport((_, _, _) =>
                Task.FromResult(SuccessfulTransportResult())));

        var result = await executor.ExecuteAsync(ValidRequest());

        Assert.Equal(CommandStatus.AuditCompletionFailed, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            CommandFailureClassification.AuditPersistence,
            result.FailureClassification);
    }

    [Fact]
    public async Task Nonzero_exit_remains_distinguishable()
    {
        var executor = CreateExecutor(
            new RecordingAuditSink([]),
            new DelegateTransport((_, _, _) =>
                Task.FromResult(new TransportCommandResult(
                    7,
                    new("", 0, false),
                    new("denied", 6, false)))));

        var result = await executor.ExecuteAsync(ValidRequest());

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(CommandFailureClassification.NonzeroExit, result.FailureClassification);
    }

    [Fact]
    public async Task Sudo_password_failure_is_classified_as_privilege_denial()
    {
        var executor = new CommandExecutor(
            new FixedPermissionPolicy(PermissionDecision.Allow),
            new RecordingAuditSink([]),
            new FixedIdentityResolver(),
            new DelegateTransport((_, _, _) =>
                Task.FromResult(new TransportCommandResult(
                    1,
                    new("", 0, false),
                    new("sudo: a password is required", 28, false)))),
            new TransformingPrivilegePreparer(request => request with
            {
                Executable = "sudo",
                Arguments = [new("-n"), new("--"), new(request.Executable)]
            }));

        var result = await executor.ExecuteAsync(
            ValidRequest() with
            {
                Privilege = PrivilegeLevel.Elevated,
                Reason = "Test elevated execution."
            });

        Assert.Equal(CommandStatus.PrivilegeDenied, result.Status);
        Assert.Equal(
            CommandFailureClassification.PrivilegeDenied,
            result.FailureClassification);
        Assert.Equal(
            "The privilege credential was missing or rejected.",
            result.SafeFailureMessage);
    }

    private static CommandExecutor CreateExecutor(
        IAuditSink auditSink,
        ICommandTransport transport) =>
        new(
            new FixedPermissionPolicy(PermissionDecision.Allow),
            auditSink,
            new FixedIdentityResolver(),
            transport);

    private static CommandRequest ValidRequest() =>
        new()
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.test",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = "uname"
        };

    private static TransportCommandResult SuccessfulTransportResult() =>
        new(0, new("Linux", 5, false), new("", 0, false));

    private static DelegateTransport BlockingTransport() =>
        new(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulTransportResult();
        });

    private sealed class FixedPermissionPolicy(PermissionDecision decision)
        : ICommandPermissionPolicy
    {
        public PermissionDecision Authorize(CommandRequest request) => decision;
    }

    private sealed class FixedIdentityResolver(
        string? remoteUserName = "operator") : IAuditExecutionIdentityResolver
    {
        public Task<AuditExecutionIdentity?> ResolveAsync(
            string hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuditExecutionIdentity?>(
                remoteUserName is null ? null : new(remoteUserName));
    }

    private sealed class FixedPrivilegePreparer(SensitiveStandardInput input)
        : IPrivilegeCommandPreparer
    {
        public ValueTask<PrivilegePreparationResult> PrepareAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                PrivilegePreparationResult.Allow(
                    request with { StandardInput = input }));
    }

    private sealed class TransformingPrivilegePreparer(
        Func<CommandRequest, CommandRequest> transform)
        : IPrivilegeCommandPreparer
    {
        public ValueTask<PrivilegePreparationResult> PrepareAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                PrivilegePreparationResult.Allow(transform(request)));
    }

    private sealed class DelegateTransport(
        Func<Guid, CommandRequest, CancellationToken, Task<TransportCommandResult>> execute)
        : ICommandTransport
    {
        public int CallCount { get; private set; }

        public Task<TransportCommandResult> ExecuteAsync(
            Guid commandId,
            CommandRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return execute(commandId, request, cancellationToken);
        }
    }

    private sealed class RecordingAuditSink(List<string> events) : IAuditSink
    {
        public int StartCount { get; private set; }

        public CommandAuditEntry? Started { get; private set; }

        public CommandAuditEntry? Completed { get; private set; }

        public Task CommandStartedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            Started = entry;
            events.Add("audit-start");
            return Task.CompletedTask;
        }

        public Task CommandCompletedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default)
        {
            Completed = entry;
            events.Add("audit-complete");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStartAuditSink : IAuditSink
    {
        public Task CommandStartedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated audit failure."));

        public Task CommandCompletedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingCompletionAuditSink : IAuditSink
    {
        public Task CommandStartedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CommandCompletedAsync(
            CommandAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated audit failure."));
    }
}
