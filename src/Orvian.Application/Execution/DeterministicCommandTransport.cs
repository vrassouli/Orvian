using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Application.Execution;

public sealed class DeterministicCommandTransport : ICommandTransport
{
    private readonly ImmutableDictionary<string, TransportCommandResult> _responses;

    public DeterministicCommandTransport(
        IEnumerable<KeyValuePair<string, TransportCommandResult>> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _responses = responses.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    public Task<TransportCommandResult> ExecuteAsync(
        Guid commandId,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Kind != CommandKind.ReadOnly)
        {
            throw new InvalidOperationException(
                "The deterministic Sprint 0 transport executes read-only commands only.");
        }

        var key = BuildKey(request.Executable, request.Arguments);
        var result = _responses.TryGetValue(key, out var configured)
            ? configured
            : new TransportCommandResult(
                127,
                new(string.Empty, 0, false),
                new("Fake transport has no configured response.", 42, false));
        return Task.FromResult(result);
    }

    public static string BuildKey(
        string executable,
        IEnumerable<CommandArgument> arguments) =>
        string.Join(
            '\u001f',
            new[] { executable }.Concat(arguments.Select(argument => argument.Value)));
}
