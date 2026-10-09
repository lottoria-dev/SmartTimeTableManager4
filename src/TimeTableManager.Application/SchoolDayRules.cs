using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

/// <summary>Actual dates, not recurring weekdays. Applies to both occupied and empty destinations.</summary>
public sealed class SchoolDayRules
{
    private readonly ScheduleCalendar? calendar;
    private readonly Dictionary<string, TimeSlot> slots;
    private readonly Dictionary<string, int?> grades;
    private readonly HashSet<(DateOnly Date, int Grade)> excluded;
    public SchoolDayRules(ProjectDocument document)
    {
        calendar = document.Calendar;
        slots = document.Current.Slots.ToDictionary(x => x.Id);
        grades = document.Current.Classes.ToDictionary(x => x.Id, x => GradeOf(x.Name));
        excluded = Entries(document).Select(x => (x.Date, x.Grade)).ToHashSet();
    }
    public static int? GradeOf(string className)
    {
        var match = Regex.Match(className, @"^\s*([1-3])\s*(?:학년|[-_/\.–])", RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
    public static ImmutableArray<GradeDateExclusion> Entries(ProjectDocument document) => document.GradeDateExclusions ?? [];
    public bool IsExcluded(string classId, string slotId) => calendar is not null &&
        grades.GetValueOrDefault(classId) is { } grade && slots.TryGetValue(slotId, out var slot) && excluded.Contains((calendar.DateOf(slot), grade));
    public bool IsExcluded(LessonInstance lesson) => IsExcluded(lesson.ClassId, lesson.SlotId);
    public bool Allows(LessonDelta delta) => !IsExcluded(delta.Before) && !IsExcluded(delta.After);
}
