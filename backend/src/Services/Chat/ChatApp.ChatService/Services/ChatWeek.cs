using System.Globalization;

namespace ChatApp.ChatService.Services;

public static class ChatWeek
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);
    public static DateTime StartUtc(DateTimeOffset at)
    {
        var local = at.ToOffset(Offset);
        var monday = local.Date.AddDays(-((int)local.DayOfWeek + 6) % 7);
        return new DateTimeOffset(monday, Offset).UtcDateTime;
    }
    public static DateTime? Parse(string? weekStart)
    {
        if (string.IsNullOrEmpty(weekStart)) return null;
        if (!DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day.DayOfWeek != DayOfWeek.Monday)
            throw new ArgumentException("weekStart phải là thứ Hai, dạng yyyy-MM-dd (giờ Việt Nam).");
        return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Offset).UtcDateTime;
    }
}
