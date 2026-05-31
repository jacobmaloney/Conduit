using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Conduit.Web.Shared.Design;

/// <summary>
/// Visual-schedule frequencies the <see cref="CcDateTimePicker"/> exposes. Each
/// maps deterministically to a 5-field NCrontab expression (minute hour
/// day-of-month month day-of-week) — the exact dialect
/// <c>Conduit.Sync.Orchestration.ScheduledSyncRunnerJob</c> evaluates.
/// </summary>
public enum CcScheduleFrequency
{
    Manual = 0,
    Minutes,
    Hourly,
    Daily,
    Weekly,
    Monthly
}

/// <summary>
/// The visual selection the picker round-trips. Holds enough state to (a) emit a
/// valid 5-field cron string and (b) be re-populated from a previously-stored
/// cron value where the shape is recognized.
/// </summary>
public sealed class CcScheduleModel
{
    public CcScheduleFrequency Frequency { get; set; } = CcScheduleFrequency.Manual;

    /// <summary>For <see cref="CcScheduleFrequency.Minutes"/> — every N minutes (1..59).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>For <see cref="CcScheduleFrequency.Hourly"/> — every N hours (1..23).</summary>
    public int IntervalHours { get; set; } = 1;

    /// <summary>Time-of-day for Daily / Weekly / Monthly (local to the server clock NCrontab uses).</summary>
    public int Hour { get; set; } = 2;
    public int Minute { get; set; } = 0;

    /// <summary>Day-of-month (1..28) for <see cref="CcScheduleFrequency.Monthly"/>.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Selected weekdays (0=Sunday..6=Saturday) for <see cref="CcScheduleFrequency.Weekly"/>.</summary>
    public List<int> DaysOfWeek { get; set; } = new() { 0 };
}

/// <summary>
/// Dependency-free converter between a <see cref="CcScheduleModel"/> visual
/// selection and a 5-field cron string. Pure string assembly on the way out;
/// best-effort pattern recognition on the way back so an existing
/// <c>CronSchedule</c> re-populates the visual builder when its shape is one the
/// builder can produce. Unrecognized expressions (including the legacy
/// <c>@hourly</c> / <c>@every:Nm</c> shorthands and arbitrary power-user cron)
/// round-trip untouched via the raw field — they simply leave the visual
/// frequency on <see cref="CcScheduleFrequency.Manual"/>.
/// </summary>
public static class CcCronConverter
{
    private static readonly string[] DayAbbrev = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public static string DayLabel(int dow) => dow >= 0 && dow <= 6 ? DayAbbrev[dow] : dow.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a 5-field cron string from the visual model. Returns an empty
    /// string for <see cref="CcScheduleFrequency.Manual"/> (blank = manual only).
    /// </summary>
    public static string ToCron(CcScheduleModel m)
    {
        switch (m.Frequency)
        {
            case CcScheduleFrequency.Minutes:
                var n = Clamp(m.IntervalMinutes, 1, 59);
                // Every-N-minutes. N==1 is just "* * * * *".
                return n == 1 ? "* * * * *" : string.Concat("*/", n.ToString(CultureInfo.InvariantCulture), " * * * *");

            case CcScheduleFrequency.Hourly:
                var h = Clamp(m.IntervalHours, 1, 23);
                var minAt = Clamp(m.Minute, 0, 59).ToString(CultureInfo.InvariantCulture);
                return h == 1
                    ? string.Concat(minAt, " * * * *")
                    : string.Concat(minAt, " */", h.ToString(CultureInfo.InvariantCulture), " * * *");

            case CcScheduleFrequency.Daily:
                return string.Concat(Field(m.Minute, 0, 59), " ", Field(m.Hour, 0, 23), " * * *");

            case CcScheduleFrequency.Weekly:
                var days = (m.DaysOfWeek ?? new List<int>())
                    .Where(d => d >= 0 && d <= 6)
                    .Distinct()
                    .OrderBy(d => d)
                    .ToList();
                if (days.Count == 0) days.Add(0);
                var dowField = string.Join(",", days.Select(d => d.ToString(CultureInfo.InvariantCulture)));
                return string.Concat(Field(m.Minute, 0, 59), " ", Field(m.Hour, 0, 23), " * * ", dowField);

            case CcScheduleFrequency.Monthly:
                var dom = Clamp(m.DayOfMonth, 1, 28).ToString(CultureInfo.InvariantCulture);
                return string.Concat(Field(m.Minute, 0, 59), " ", Field(m.Hour, 0, 23), " ", dom, " * *");

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Best-effort reverse: re-populates a visual model from an existing cron
    /// string. Returns true when the expression is a shape the builder can
    /// produce (so the visual UI should drive it); false when it should stay in
    /// raw/power-user mode (model left on Manual). Never throws.
    /// </summary>
    public static bool TryFromCron(string? cron, out CcScheduleModel model)
    {
        model = new CcScheduleModel();
        if (string.IsNullOrWhiteSpace(cron)) return false;

        var s = cron.Trim();

        // Legacy shorthands are valid but not visually editable — keep them raw.
        if (s.StartsWith("@", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        var min = parts[0];
        var hour = parts[1];
        var dom = parts[2];
        var month = parts[3];
        var dow = parts[4];

        // Month must be wildcard for every shape this builder emits.
        if (month != "*") return false;

        // Every-N-minutes: "*/N * * * *" or "* * * * *".
        if (hour == "*" && dom == "*" && dow == "*")
        {
            if (min == "*")
            {
                model.Frequency = CcScheduleFrequency.Minutes;
                model.IntervalMinutes = 1;
                return true;
            }
            if (TryStep(min, out var stepN))
            {
                model.Frequency = CcScheduleFrequency.Minutes;
                model.IntervalMinutes = stepN;
                return true;
            }
            return false;
        }

        // The remaining shapes all need a single concrete minute value.
        if (!TrySingle(min, 0, 59, out var minuteVal)) return false;

        // Hourly: "M * * * *" or "M */H * * *".
        if (dom == "*" && dow == "*")
        {
            if (hour == "*")
            {
                model.Frequency = CcScheduleFrequency.Hourly;
                model.IntervalHours = 1;
                model.Minute = minuteVal;
                return true;
            }
            if (TryStep(hour, out var stepH))
            {
                model.Frequency = CcScheduleFrequency.Hourly;
                model.IntervalHours = stepH;
                model.Minute = minuteVal;
                return true;
            }
            return false;
        }

        // The day/week/month shapes need a single concrete hour value.
        if (!TrySingle(hour, 0, 23, out var hourVal)) return false;
        model.Minute = minuteVal;
        model.Hour = hourVal;

        // Daily: "M H * * *".
        if (dom == "*" && dow == "*")
        {
            model.Frequency = CcScheduleFrequency.Daily;
            return true;
        }

        // Weekly: "M H * * <dow list>".
        if (dom == "*" && dow != "*")
        {
            var days = ParseDowList(dow);
            if (days is null) return false;
            model.Frequency = CcScheduleFrequency.Weekly;
            model.DaysOfWeek = days;
            return true;
        }

        // Monthly: "M H <dom> * *".
        if (dom != "*" && dow == "*")
        {
            if (!TrySingle(dom, 1, 28, out var domVal)) return false;
            model.Frequency = CcScheduleFrequency.Monthly;
            model.DayOfMonth = domVal;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Plain-language summary of a cron string for preview chips. Falls back to
    /// echoing the raw expression when it is not a builder-shaped value.
    /// </summary>
    public static string Describe(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "Manual only (no schedule)";
        var s = cron.Trim();
        if (s.Equals("@hourly", StringComparison.OrdinalIgnoreCase)) return "Every hour";
        if (s.StartsWith("@every:", StringComparison.OrdinalIgnoreCase)) return string.Concat("Every ", s.Substring("@every:".Length));

        if (!TryFromCron(s, out var m)) return s;

        switch (m.Frequency)
        {
            case CcScheduleFrequency.Minutes:
                return m.IntervalMinutes == 1 ? "Every minute" : string.Concat("Every ", m.IntervalMinutes.ToString(CultureInfo.InvariantCulture), " minutes");
            case CcScheduleFrequency.Hourly:
                var at = string.Concat(":", m.Minute.ToString("00", CultureInfo.InvariantCulture));
                return m.IntervalHours == 1
                    ? string.Concat("Hourly at ", at)
                    : string.Concat("Every ", m.IntervalHours.ToString(CultureInfo.InvariantCulture), " hours at ", at);
            case CcScheduleFrequency.Daily:
                return string.Concat("Daily at ", Time(m.Hour, m.Minute));
            case CcScheduleFrequency.Weekly:
                var dayNames = string.Join(", ", m.DaysOfWeek.OrderBy(d => d).Select(DayLabel));
                return string.Concat("Weekly on ", dayNames, " at ", Time(m.Hour, m.Minute));
            case CcScheduleFrequency.Monthly:
                return string.Concat("Monthly on the ", Ordinal(m.DayOfMonth), " at ", Time(m.Hour, m.Minute));
            default:
                return s;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static string Field(int v, int lo, int hi) => Clamp(v, lo, hi).ToString(CultureInfo.InvariantCulture);

    private static bool TryStep(string token, out int n)
    {
        n = 0;
        if (!token.StartsWith("*/", StringComparison.Ordinal)) return false;
        return int.TryParse(token.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0;
    }

    private static bool TrySingle(string token, int lo, int hi, out int value)
    {
        value = 0;
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
        if (v < lo || v > hi) return false;
        value = v;
        return true;
    }

    private static List<int>? ParseDowList(string token)
    {
        var result = new List<int>();
        foreach (var piece in token.Split(','))
        {
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) return null;
            // NCrontab accepts 0..6 (and 7 as Sunday); normalize 7 -> 0.
            if (d == 7) d = 0;
            if (d < 0 || d > 6) return null;
            if (!result.Contains(d)) result.Add(d);
        }
        return result.Count == 0 ? null : result.OrderBy(d => d).ToList();
    }

    private static string Time(int hour, int minute)
    {
        var dt = new DateTime(2000, 1, 1, Clamp(hour, 0, 23), Clamp(minute, 0, 59), 0);
        return dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private static string Ordinal(int n)
    {
        var suffix = (n % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }
        };
        return string.Concat(n.ToString(CultureInfo.InvariantCulture), suffix);
    }
}
