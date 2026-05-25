namespace TodoApi.Services.Pnl;

public static class UtcTimestampParser
{
    public static bool TryParseUtc(string raw, out DateTimeOffset value, out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Timestamp is required";
            return false;
        }

        raw = raw.Trim();

        // Accept Unix seconds/milliseconds for convenience.
        if (long.TryParse(raw, out var unix))
        {
            try
            {
                // Heuristic: 13+ digits => milliseconds since epoch.
                value = raw.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix).ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds(unix).ToUniversalTime();
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "Unix timestamp is out of range";
                return false;
            }
        }

        // Preferred format: ISO-8601 / RFC3339 date-time (e.g. 2026-05-25T02:30:00Z).
        if (DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            value = dto.ToUniversalTime();
            return true;
        }

        error = "Invalid timestamp format. Use ISO-8601 (e.g. 2026-05-25T02:30:00Z) or Unix seconds/milliseconds.";
        return false;
    }
}
