using System.Text;

namespace Orvian.Ssh;

public static class RemoteOutputSanitizer
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\u001b')
            {
                index = SkipEscapeSequence(value, index);
                continue;
            }

            if (IsBidiControl(character))
            {
                continue;
            }

            if (char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t')
            {
                continue;
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private static int SkipEscapeSequence(string value, int escapeIndex)
    {
        var index = escapeIndex + 1;
        if (index >= value.Length)
        {
            return escapeIndex;
        }

        if (value[index] == '[')
        {
            index++;
            while (index < value.Length)
            {
                var character = value[index];
                if (character is >= '@' and <= '~')
                {
                    return index;
                }

                index++;
            }

            return value.Length - 1;
        }

        if (value[index] == ']')
        {
            index++;
            while (index < value.Length)
            {
                if (value[index] == '\a')
                {
                    return index;
                }

                if (value[index] == '\u001b' &&
                    index + 1 < value.Length &&
                    value[index + 1] == '\\')
                {
                    return index + 1;
                }

                index++;
            }

            return value.Length - 1;
        }

        return index;
    }

    private static bool IsBidiControl(char character) =>
        character is >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069';
}
