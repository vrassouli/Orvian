using System.Text;
using Orvian.Core.Commands;

namespace Orvian.Ssh;

public static class PosixArgumentEncoder
{
    public static string Encode(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateExecutable(request.Executable);

        var builder = new StringBuilder(request.Executable);
        foreach (var argument in request.Arguments)
        {
            if (argument.Value.Contains('\0'))
            {
                throw new ArgumentException(
                    "Command arguments cannot contain NUL characters.",
                    nameof(request));
            }

            builder.Append(' ');
            AppendQuoted(builder, argument.Value);
        }

        return builder.ToString();
    }

    private static void ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            executable.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '_' or '-' or '.' or '/')))
        {
            throw new ArgumentException(
                "Executable contains characters outside the transport allow-list.",
                nameof(executable));
        }
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('\'');
        foreach (var character in value)
        {
            if (character == '\'')
            {
                builder.Append("'\"'\"'");
            }
            else
            {
                builder.Append(character);
            }
        }

        builder.Append('\'');
    }
}
