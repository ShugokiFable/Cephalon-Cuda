namespace CephalonCuda.Services;

/// <summary>Human-friendly countdown formatting shared by the dashboard and context payloads.</summary>
public static class TimeUtil
{
    public static DateTimeOffset? Parse(string? iso) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var t) ? t : null;

    /// <summary>e.g. "2d 4h", "3h 12m", "47s", or "expired". Null timestamp → "—".</summary>
    public static string Countdown(DateTimeOffset? target, DateTimeOffset? now = null)
    {
        if (target is null) return "—";
        var span = target.Value - (now ?? DateTimeOffset.UtcNow);
        if (span <= TimeSpan.Zero) return "expired";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    public static string Countdown(string? iso, DateTimeOffset? now = null) => Countdown(Parse(iso), now);
}
