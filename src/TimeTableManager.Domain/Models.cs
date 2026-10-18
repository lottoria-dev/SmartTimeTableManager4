using System.Collections.Immutable;

namespace TimeTableManager.Domain;

public sealed record Teacher(string Id, string Name);
public sealed record ClassGroup(string Id, string Name, string? HomeroomTeacherId = null);
public sealed record TimeSlot(string Id, int Week, int Day, int Period)
{
    public string Label => $"{Week}주 {"월화수목금토일"[Math.Clamp(Day, 1, 7) - 1]} {Period}교시";
}
public sealed record LessonInstance(string Id, string ClassId, string Subject, string? TeacherId,
    string SlotId, bool Locked = false, string? Room = null, string Source = "");
public sealed record TeacherBlock(string TeacherId, string SlotId);
public sealed record GradeDateExclusion(DateOnly Date, int Grade);
public sealed record MonthlySchedule(DateOnly Month, DateOnly PatternMonday, ScheduleState Pattern, Guid CoverageId);
public sealed record ImportSource(string FileName, string Sha256, string Adapter, DateTimeOffset ImportedAt,
    string Encoding, string Sheet);
public sealed record ScheduleState(ImmutableArray<Teacher> Teachers, ImmutableArray<ClassGroup> Classes,
    ImmutableArray<TimeSlot> Slots, ImmutableArray<LessonInstance> Lessons, ImmutableArray<TeacherBlock> Blocks)
{
    public static ScheduleState Empty => new([], [], [], [], []);
}
public enum Severity { Warning, Error }
public sealed record Issue(string Code, Severity Severity, string Message, string Location = "");
public sealed record ValidationResult(ImmutableArray<Issue> Issues)
{
    public bool IsValid => !Issues.Any(x => x.Severity == Severity.Error);
    public static ValidationResult Error(string code, string message) => new([new(code, Severity.Error, message)]);
}
public enum ChangeKind { Move, AssignTeacher, ToggleLock, DayTemplate }
public sealed record LessonDelta(LessonInstance Before, LessonInstance After, bool Added = false, bool Removed = false);
public sealed record DayTemplateChange(DateOnly Date, int SourceDay, int BeforePeriods, int AfterPeriods,
    ImmutableArray<TimeSlot> AdditionalSlots, ImmutableArray<LessonInstance> BeforeLessons, ImmutableArray<LessonInstance> AfterLessons,
    ImmutableArray<string> BeforeOrder, bool AllowLocked = false);
public sealed record ChangePlan(Guid Revision, ChangeKind Kind, string Description, ImmutableArray<LessonDelta> Changes,
    DayTemplateChange? DayTemplate = null);
public sealed record Operation(Guid BeforeStateId, Guid AfterStateId, DateTimeOffset At, ChangeKind Kind,
    string Description, ImmutableArray<LessonDelta> Changes, DayTemplateChange? DayTemplate = null);
public sealed record ScheduleCalendar(DateOnly FirstWeekMonday, DateOnly EditableFrom,
    DateOnly? EditableUntil = null, int WeeksAhead = 1)
{
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    public DateOnly DateOf(TimeSlot slot) => FirstWeekMonday.AddDays((slot.Week - 1) * 7 + slot.Day - 1);
    public DateOnly DisplayUntil => EditableUntil ?? MondayOf(EditableFrom).AddDays(11);
    public bool CanEdit(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
        date >= EditableFrom && (EditableUntil is null || date <= EditableUntil);
    public bool CanEdit(TimeSlot slot) => CanEdit(DateOf(slot));
    public string Label(TimeSlot slot) => $"{DateOf(slot):M/d} ({"월화수목금토일"[slot.Day - 1]}) {slot.Period}교시";
}
public sealed record BaseDayPlan(DateOnly Date, int SourceDay, int Periods);
public sealed record ProjectDocument(int SchemaVersion, string ProjectId, string Name, Guid InitialStateId,
    Guid CurrentStateId, ScheduleState Baseline, ScheduleState Current, ImmutableArray<Operation> History,
    int HistoryCursor, ImportSource? Source, ScheduleCalendar? Calendar = null,
    ImmutableArray<GradeDateExclusion>? GradeDateExclusions = null, MonthlySchedule? Monthly = null,
    ImmutableArray<string>? TemplateSlotIds = null, ScheduleState? OriginalPattern = null,
    ImmutableArray<BaseDayPlan>? BaseDayPlans = null)
{
    public static ProjectDocument Create(string name, ScheduleState state, ImportSource? source = null)
    {
        var id = Guid.NewGuid();
        return new(1, Guid.NewGuid().ToString("N"), name, id, id, state, state, [], 0, source);
    }
}
