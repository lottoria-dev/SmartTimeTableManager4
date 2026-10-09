using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class BoardDateRange
{
    public static DateOnly NextFriday(DateOnly today) => ScheduleCalendar.MondayOf(today).AddDays(11);
    public static bool IsDefaultVisible(DateOnly date, DateOnly today) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date >= today && date <= NextFriday(today);
}
