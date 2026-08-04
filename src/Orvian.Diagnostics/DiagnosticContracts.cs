using System.Collections.Immutable;

namespace Orvian.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public sealed record SafeDiagnosticEvent(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    DiagnosticSeverity Severity,
    string Category,
    string Code,
    string SafeMessage,
    ImmutableDictionary<string, string> SafeProperties);

public interface IApplicationDiagnosticLog
{
    Task<bool> TryWriteAsync(
        SafeDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default);
}

public interface IApplicationDiagnosticReader
{
    Task<IReadOnlyList<SafeDiagnosticEvent>> QueryAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);
}
