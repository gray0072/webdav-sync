namespace WebDavSync.Core.Model;

/// <summary>Name validity rules (§13 #2).</summary>
public static class NameRules
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns null when the name is valid on Windows, otherwise the reason.</summary>
    public static string? WindowsInvalidReason(string name)
    {
        if (name.Length == 0 || name is "." or "..")
        {
            return "empty or dot name";
        }

        foreach (var c in name)
        {
            if (c < 32)
            {
                return "control character";
            }

            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return $"invalid character '{c}'";
            }
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "trailing dot or space";
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        return ReservedWindowsNames.Contains(stem) ? $"reserved name '{stem}'" : null;
    }
}
