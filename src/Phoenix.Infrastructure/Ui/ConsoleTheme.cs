using System.Globalization;
using Spectre.Console;

namespace Phoenix.Infrastructure.Ui;

/// <summary>
/// A deliberately small palette. Colour carries meaning here, so it is worth keeping rare:
/// accent for identity, green for done, yellow for attention, red for something the user must
/// act on, grey for everything secondary.
/// </summary>
public sealed class ConsoleTheme
{
    public ConsoleTheme(string accentColor)
    {
        Accent = ParseColor(accentColor, Color.DeepSkyBlue1);
    }

    public Color Accent { get; }

    public static Color Success => Color.Green;

    public static Color Warning => Color.Yellow;

    public static Color Failure => Color.Red;

    public static Color Muted => Color.Grey;

    public string AccentMarkup => Accent.ToMarkup();

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            // Accepts both names ("deepskyblue1") and hex ("#1f6feb").
            return value.StartsWith('#')
                ? HexToColor(value, fallback)
                : Color.FromConsoleColor(Enum.Parse<ConsoleColor>(value, ignoreCase: true));
        }
        catch (ArgumentException)
        {
            var parsed = typeof(Color)
                .GetProperties()
                .FirstOrDefault(p =>
                    p.PropertyType == typeof(Color) &&
                    string.Equals(p.Name, value.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

            return parsed?.GetValue(null) is Color color ? color : fallback;
        }
    }

    private static Color HexToColor(string value, Color fallback)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex[2..4], NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex[4..], NumberStyles.HexNumber, null, out var b))
        {
            return fallback;
        }

        return new Color(r, g, b);
    }
}
