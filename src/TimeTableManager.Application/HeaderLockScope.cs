using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class HeaderLockScope
{
    public static IReadOnlyList<string> LessonIds(ScheduleState state, string slotId, IReadOnlySet<int> grades)
    {
        var classes = state.Classes.Where(x => SchoolDayRules.GradeOf(x.Name) is { } grade && grades.Contains(grade))
            .Select(x => x.Id).ToHashSet();
        return state.Lessons.Where(x => x.SlotId == slotId && classes.Contains(x.ClassId)).Select(x => x.Id).ToArray();
    }
}
