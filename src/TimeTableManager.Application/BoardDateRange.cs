using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class BoardDateRange
{
    public static DateOnly Friday(DateOnly today, int weeksAhead)
    {
        if (weeksAhead is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(weeksAhead));
        return ScheduleCalendar.MondayOf(today).AddDays(4 + weeksAhead * 7);
    }
    public static DateOnly NextFriday(DateOnly today) => Friday(today, 1);
    public static bool IsVisible(DateOnly date, DateOnly today, int weeksAhead) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date >= today && date <= Friday(today, weeksAhead);
    public static bool IsDefaultVisible(DateOnly date, DateOnly today) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date >= today && date <= NextFriday(today);
}
