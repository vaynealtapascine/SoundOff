using System.Globalization;

namespace SoundOff.Core;

// Human-entered timing in exact microseconds: "H:MM:SS.ffffff" out, and "SS", "MM:SS" or "H:MM:SS" with up to six
// fractional digits in. Culture-invariant, no rounding: more than six fractional digits is an error, not a guess.
public static class TimeText
{
    public const long MaxMicroseconds = 1000L * 3_600_000_000; // 1000 hours

    public static string Format(long microseconds)
    {
        if (microseconds < 0 || microseconds > MaxMicroseconds) throw new ArgumentOutOfRangeException(nameof(microseconds));
        var hours = microseconds / 3_600_000_000; var rest = microseconds % 3_600_000_000;
        var minutes = rest / 60_000_000; rest %= 60_000_000; var seconds = rest / 1_000_000; var fraction = rest % 1_000_000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}.{fraction:000000}");
    }

    // Null for blank input. Throws InvalidDataException with a plain-language reason otherwise.
    public static long? Parse(string? text)
    {
        if (text is null || text.Trim().Length == 0) return null;
        var trimmed = text.Trim();
        var parts = trimmed.Split(':');
        if (parts.Length > 3) throw new InvalidDataException("Use seconds, MM:SS or H:MM:SS, for example 1:02:03.250.");
        long hours = 0, minutes = 0;
        if (parts.Length == 3) hours = Whole(parts[0], "hours");
        if (parts.Length >= 2) minutes = Whole(parts[^2], "minutes");
        if (parts.Length >= 2 && minutes > 59) throw new InvalidDataException("Minutes must be 0-59 when hours are given as well.");
        var secondsText = parts[^1]; var dot = secondsText.IndexOf('.');
        var wholeSeconds = Whole(dot < 0 ? secondsText : secondsText[..dot], "seconds");
        if (parts.Length >= 2 && wholeSeconds > 59) throw new InvalidDataException("Seconds must be 0-59 when minutes are given as well.");
        long fraction = 0;
        if (dot >= 0)
        {
            var digits = secondsText[(dot + 1)..];
            if (digits.Length == 0 || digits.Length > 6 || !digits.All(char.IsAsciiDigit))
                throw new InvalidDataException("Fractions of a second use one to six digits (microsecond precision); nothing is rounded.");
            fraction = long.Parse(digits.PadRight(6, '0'), CultureInfo.InvariantCulture);
        }
        var total = ((hours * 60 + minutes) * 60 + wholeSeconds) * 1_000_000 + fraction;
        if (total > MaxMicroseconds) throw new InvalidDataException("Timing beyond 1000 hours is not supported.");
        return total;
    }

    // Both blank means untimed; one blank is a mistake, not a half-known interval.
    public static TimeRange? ParseRange(string? start, string? end)
    {
        var s = Parse(start); var e = Parse(end);
        if (s is null && e is null) return null;
        if (s is null || e is null) throw new InvalidDataException("Enter both a start and an end, or leave both blank for untimed.");
        if (e <= s) throw new InvalidDataException("The end must be later than the start.");
        return new TimeRange(s.Value, e.Value);
    }

    private static long Whole(string text, string unit)
    {
        if (text.Length == 0 || text.Length > 7 || !text.All(char.IsAsciiDigit)) throw new InvalidDataException($"The {unit} part must be plain digits.");
        return long.Parse(text, CultureInfo.InvariantCulture);
    }
}
