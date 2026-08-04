using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Orvian.Diagnostics;

public static partial class DiagnosticSanitizer
{
    private const string Redacted = "[REDACTED]";

    public static SafeDiagnosticEvent Sanitize(SafeDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (diagnosticEvent.EventId == Guid.Empty ||
            diagnosticEvent.CorrelationId == Guid.Empty ||
            diagnosticEvent.OccurredAt == default ||
            !Enum.IsDefined(diagnosticEvent.Severity) ||
            !ValidIdentifier(diagnosticEvent.Category) ||
            !ValidIdentifier(diagnosticEvent.Code))
        {
            throw new ArgumentException(
                "Diagnostic event identity or classification is invalid.",
                nameof(diagnosticEvent));
        }

        var properties = diagnosticEvent.SafeProperties ??
            ImmutableDictionary<string, string>.Empty;
        if (properties.Count > 32)
        {
            throw new ArgumentException(
                "Diagnostic events may contain at most 32 properties.",
                nameof(diagnosticEvent));
        }

        return diagnosticEvent with
        {
            SafeMessage = SanitizeText(diagnosticEvent.SafeMessage, 2048),
            SafeProperties = properties.ToImmutableDictionary(
                pair => ValidatePropertyName(pair.Key),
                pair => SensitivePropertyName().IsMatch(pair.Key)
                    ? Redacted
                    : SanitizeText(pair.Value, 1024),
                StringComparer.Ordinal)
        };
    }

    private static string ValidatePropertyName(string value)
    {
        if (!ValidIdentifier(value))
        {
            throw new ArgumentException("Diagnostic property name is invalid.");
        }

        return value;
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        Identifier().IsMatch(value);

    private static string SanitizeText(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        normalized = PemMaterial().Replace(normalized, Redacted);
        normalized = SensitiveAssignment().Replace(
            normalized,
            match => $"{match.Groups[1].Value}={Redacted}");
        normalized = UriUserInfo().Replace(normalized, "://[REDACTED]@");
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    [GeneratedRegex("""^[A-Za-z0-9_.-]+$""", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(
        """password|passphrase|private.?key|token|credential|authorization|secret""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePropertyName();

    [GeneratedRegex(
        """-----BEGIN [^-]+-----.*?-----END [^-]+-----""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemMaterial();

    [GeneratedRegex(
        """(?i)\b(password|passphrase|token|secret|authorization)\s*=\s*[^\s,;]+""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(
        """://[^/\s:@]+:[^/\s@]+@""",
        RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfo();
}
