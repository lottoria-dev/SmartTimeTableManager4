using TimeTableManager.Domain;

namespace TimeTableManager.Application;

/// <summary>Today anchors editing. Month selection only changes which dates are viewed.</summary>
public static class WorkPeriod
{
    public const int DefaultWeeksAhead = 1;

    public static ProjectDocument Prepare(ProjectDocument source, DateOnly today, int weeksAhead = DefaultWeeksAhead)
    {
        var until = BoardDateRange.Friday(today, weeksAhead);
        var month = MonthlyTimetable.MonthOf(today);
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var calendar = source.Calendar ?? new ScheduleCalendar(ScheduleCalendar.MondayOf(today), today);
        var configured = source with { Calendar = calendar with { EditableFrom = today, EditableUntil = until, WeeksAhead = weeksAhead } };
        // Full current month is available for viewing; forward dates are extended by actual date, not month switches.
        return MonthlyTimetable.EnsureDates(configured, month, until > monthEnd ? until : monthEnd, month);
    }

    public static ProjectDocument IncludeMonth(ProjectDocument source, DateOnly requested)
    {
        var month = MonthlyTimetable.MonthOf(requested);
        return MonthlyTimetable.EnsureDates(source, month, month.AddMonths(1).AddDays(-1));
    }

    public static IEnumerable<TimeSlot> EditableSlots(ProjectDocument document) => document.Calendar is { } calendar
        ? MonthlyTimetable.ActiveSlots(document).Where(calendar.CanEdit)
        : [];
}
