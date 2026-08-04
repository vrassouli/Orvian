using Orvian.Application.Execution;
using Orvian.Auditing;
using Orvian.Core.Commands;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public async Task Multi_command_operation_succeeds_through_audited_fake_transport()
    {
        var operationId = Guid.NewGuid();
        var audit = new InMemoryAuditStore();
        var transport = new DeterministicCommandTransport(
        [
            KeyValuePair.Create(
                DeterministicCommandTransport.BuildKey("uname", [new("-s")]),
                Success("Linux")),
            KeyValuePair.Create(
                DeterministicCommandTransport.BuildKey("uname", [new("-m")]),
                Success("arm64"))
        ]);
        var executor = new CommandExecutor(
            new AllowPolicy(),
            audit,
            new FixedIdentityResolver(),
            transport);
        var coordinator = new OperationCoordinator(
            executor,
            audit,
            new FixedIdentityResolver());

        var result = await coordinator.ExecuteAsync(new(
            CreateIntent(operationId),
            [
                CreateCommand(operationId, "-s"),
                CreateCommand(operationId, "-m")
            ]));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Commands.Length);
        var activity = await audit.QueryAsync(new ActivityQuery());
        Assert.Equal(2, activity.Count);
        Assert.All(activity, item => Assert.Equal(AuditStatus.Succeeded, item.Status));
        Assert.All(activity, item => Assert.Equal("operator", item.RemoteUserName));
    }

    [Fact]
    public async Task MissingAuthenticatedUserIdentityDeniesOperationBeforeAuditOrCommands()
    {
        var operationId = Guid.NewGuid();
        var executor = new SequenceExecutor(
            [Result(operationId, CommandStatus.Succeeded, 0)]);
        var audit = new InMemoryAuditStore();
        var coordinator = new OperationCoordinator(
            executor,
            audit,
            new FixedIdentityResolver(remoteUserName: null));

        var result = await coordinator.ExecuteAsync(new(
            CreateIntent(operationId),
            [CreateCommand(operationId, "-s")]));

        Assert.Equal(OperationStatus.Denied, result.Status);
        Assert.Equal(0, executor.CallCount);
        Assert.Empty(await audit.QueryOperationsAsync(new()));
    }

    [Fact]
    public async Task Failure_stops_later_commands_and_reports_partial_success()
    {
        var operationId = Guid.NewGuid();
        var executor = new SequenceExecutor(
        [
            Result(operationId, CommandStatus.Succeeded, 0),
            Result(operationId, CommandStatus.Failed, 1),
            Result(operationId, CommandStatus.Succeeded, 0)
        ]);
        var coordinator = new OperationCoordinator(
            executor,
            new InMemoryAuditStore(),
            new FixedIdentityResolver());

        var result = await coordinator.ExecuteAsync(new(
            CreateIntent(operationId),
            [
                CreateCommand(operationId, "-s"),
                CreateCommand(operationId, "-m"),
                CreateCommand(operationId, "-r")
            ]));

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.Equal(2, result.Commands.Length);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task Spoofed_command_ownership_is_rejected()
    {
        var operationId = Guid.NewGuid();
        var coordinator = new OperationCoordinator(
            new SequenceExecutor([]),
            new InMemoryAuditStore(),
            new FixedIdentityResolver());
        var spoofed = CreateCommand(operationId, "-s") with
        {
            PluginId = "orvian.impostor"
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.ExecuteAsync(
                new OperationRequest(CreateIntent(operationId), [spoofed])));
    }

    [Fact]
    public async Task Elevated_operation_is_denied_without_core_confirmation()
    {
        var operationId = Guid.NewGuid();
        var executor = new SequenceExecutor(
            [Result(operationId, CommandStatus.Succeeded, 0)]);
        var coordinator = new OperationCoordinator(
            executor,
            new InMemoryAuditStore(),
            new FixedIdentityResolver());
        var intent = CreateIntent(operationId) with
        {
            Risk = OperationRisk.Elevated,
            Privilege = PrivilegeLevel.Elevated
        };
        var command = CreateCommand(operationId, "-s") with
        {
            Privilege = PrivilegeLevel.Elevated,
            Reason = "Test elevated operation."
        };

        var result = await coordinator.ExecuteAsync(new(intent, [command]));

        Assert.Equal(OperationStatus.Denied, result.Status);
        Assert.Empty(result.Commands);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ConfirmationReceivesStructurallyRedactedCommand()
    {
        var operationId = Guid.NewGuid();
        var confirmations = new RecordingConfirmationService();
        var coordinator = new OperationCoordinator(
            new SequenceExecutor(
                [Result(operationId, CommandStatus.Succeeded, 0)]),
            new InMemoryAuditStore(),
            new FixedIdentityResolver(),
            confirmations);
        var intent = CreateIntent(operationId) with
        {
            Risk = OperationRisk.Elevated,
            Privilege = PrivilegeLevel.Elevated
        };
        var command = CreateCommand(operationId, "-s") with
        {
            Arguments = [new("secret", IsSensitive: true)],
            Privilege = PrivilegeLevel.Elevated,
            Reason = "Test elevated operation."
        };

        var result = await coordinator.ExecuteAsync(new(intent, [command]));

        Assert.True(result.IsSuccess);
        Assert.NotNull(confirmations.Request);
        Assert.Contains("[REDACTED]", confirmations.Request.RedactedCommands[0]);
        Assert.DoesNotContain("secret", confirmations.Request.RedactedCommands[0]);
    }

    [Fact]
    public async Task OperationFailsClosedWhenAuditStartCannotPersist()
    {
        var operationId = Guid.NewGuid();
        var executor = new SequenceExecutor(
            [Result(operationId, CommandStatus.Succeeded, 0)]);
        var coordinator = new OperationCoordinator(
            executor,
            new FailingOperationAuditSink(failStart: true),
            new FixedIdentityResolver());

        var result = await coordinator.ExecuteAsync(
            new(
                CreateIntent(operationId),
                [CreateCommand(operationId, "-s")]));

        Assert.Equal(OperationStatus.Denied, result.Status);
        Assert.Empty(result.Commands);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task OperationCompletionPersistenceFailureIsNotSuccess()
    {
        var operationId = Guid.NewGuid();
        var coordinator = new OperationCoordinator(
            new SequenceExecutor(
                [Result(operationId, CommandStatus.Succeeded, 0)]),
            new FailingOperationAuditSink(failStart: false),
            new FixedIdentityResolver());

        var result = await coordinator.ExecuteAsync(
            new(
                CreateIntent(operationId),
                [CreateCommand(operationId, "-s")]));

        Assert.Equal(OperationStatus.AuditCompletionFailed, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SameResourceOperationsAreSerialized()
    {
        var executor = new ConcurrencyExecutor();
        var audit = new InMemoryAuditStore();
        var coordinator = new OperationCoordinator(
            executor,
            audit,
            new FixedIdentityResolver());
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await Task.WhenAll(
            coordinator.ExecuteAsync(
                new(
                    CreateIntent(firstId) with { ResourceLockKey = "host:service:nginx" },
                    [CreateCommand(firstId, "-s")])),
            coordinator.ExecuteAsync(
                new(
                    CreateIntent(secondId) with { ResourceLockKey = "host:service:nginx" },
                    [CreateCommand(secondId, "-m")])));

        Assert.Equal(1, executor.MaximumConcurrency);
    }

    [Fact]
    public async Task CancelledConfirmationCompletesOperationAudit()
    {
        var operationId = Guid.NewGuid();
        var audit = new InMemoryAuditStore();
        var coordinator = new OperationCoordinator(
            new SequenceExecutor([]),
            audit,
            new FixedIdentityResolver(),
            new CancellingConfirmationService());
        var intent = CreateIntent(operationId) with
        {
            Risk = OperationRisk.Elevated,
            Privilege = PrivilegeLevel.Elevated
        };
        var command = CreateCommand(operationId, "-s") with
        {
            Privilege = PrivilegeLevel.Elevated,
            Reason = "Test cancellation."
        };

        var result = await coordinator.ExecuteAsync(new(intent, [command]));
        var persisted = Assert.Single(await audit.QueryOperationsAsync(new()));

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(OperationAuditStatus.Cancelled, persisted.Status);
        Assert.NotNull(persisted.CompletedAt);
    }

    private static OperationIntent CreateIntent(Guid operationId) =>
        new()
        {
            OperationId = operationId,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.sample",
            PluginVersion = new Version(0, 1),
            Title = "Inspect operating system",
            Purpose = "Demonstrate the audited Sprint 0 operation pipeline.",
            RequiredPermission = "command.read.execute"
        };

    private static CommandRequest CreateCommand(Guid operationId, string argument) =>
        new()
        {
            OperationId = operationId,
            HostProfileId = "host-1",
            ConnectionId = "connection-1",
            PluginId = "orvian.sample",
            PluginVersion = new Version(0, 1),
            RequiredPermission = "command.read.execute",
            Executable = "uname",
            Arguments = [new(argument)]
        };

    private static TransportCommandResult Success(string output) =>
        new(0, new(output, output.Length, false), new(string.Empty, 0, false));

    private static CommandResult Result(
        Guid operationId,
        CommandStatus status,
        int exitCode) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OperationId = operationId,
            Status = status,
            ExitCode = exitCode,
            FailureClassification = status == CommandStatus.Succeeded
                ? CommandFailureClassification.None
                : CommandFailureClassification.NonzeroExit
        };

    private sealed class AllowPolicy : ICommandPermissionPolicy
    {
        public PermissionDecision Authorize(CommandRequest request) =>
            PermissionDecision.Allow;
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

    private sealed class SequenceExecutor(IEnumerable<CommandResult> results)
        : ICommandExecutor
    {
        private readonly Queue<CommandResult> _results = new(results);

        public int CallCount { get; private set; }

        public Task<CommandResult> ExecuteAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingConfirmationService : IOperationConfirmationService
    {
        public OperationConfirmationRequest? Request { get; private set; }

        public Task<bool> ConfirmAsync(
            OperationConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(true);
        }
    }

    private sealed class FailingOperationAuditSink(bool failStart)
        : IOperationAuditSink
    {
        public Task OperationStartedAsync(
            OperationAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            failStart
                ? Task.FromException(new IOException("Simulated start failure."))
                : Task.CompletedTask;

        public Task OperationCompletedAsync(
            OperationAuditEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated completion failure."));
    }

    private sealed class ConcurrencyExecutor : ICommandExecutor
    {
        private int _active;
        private int _maximum;

        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public async Task<CommandResult> ExecuteAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximum, active);
            try
            {
                await Task.Delay(30, cancellationToken);
                return Result(request.OperationId, CommandStatus.Succeeded, 0);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class CancellingConfirmationService : IOperationConfirmationService
    {
        public Task<bool> ConfirmAsync(
            OperationConfirmationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new OperationCanceledException());
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
