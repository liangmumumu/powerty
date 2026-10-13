namespace Qwaacty.Core;

/// <summary>Length-bounded string helpers that never split a UTF-16 surrogate pair
/// (emoji and other non-BMP characters would otherwise be cut into invalid halves).</summary>
public static class TextBoundary
{
    public static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
