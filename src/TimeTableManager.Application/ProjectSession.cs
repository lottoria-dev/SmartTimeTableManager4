using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

/// <summary>Single writer. Immutable drafts never alter the current document.</summary>
public sealed class ProjectSession
{
    public ProjectDocument Document { get; private set; }
    public Guid Revision { get; private set; } = Guid.NewGuid();
    public Guid? SavedStateId { get; private set; }
    private ScheduleCalendar? savedCalendar;
    private ImmutableArray<GradeDateExclusion> savedExclusions = [];
    private MonthlySchedule? savedMonthly;
    private int savedSchema, savedBaselineSlotCount;
    private SchoolDayRules schoolRules;
    public bool IsDirty => SavedStateId != Document.CurrentStateId || savedSchema != Document.SchemaVersion || savedBaselineSlotCount != Document.Baseline.Slots.Length || savedCalendar != Document.Calendar || savedMonthly != Document.Monthly || !savedExclusions.SequenceEqual(SchoolDayRules.Entries(Document));
    public bool CanUndo => Document.HistoryCursor > 0 && HistoryAllowed(Document.History[Document.HistoryCursor - 1]);
    public bool CanRedo => Document.HistoryCursor < Document.History.Length && HistoryAllowed(Document.History[Document.HistoryCursor]);
    private bool HistoryAllowed(Operation operation)
    {
        if (operation.Kind == ChangeKind.ToggleLock) return true;
        if (!operation.Changes.All(schoolRules.Allows)) return false;
        if (Document.SchemaVersion < 6 || Document.Calendar is not { } calendar) return true;
        var slots = Document.Current.Slots.ToDictionary(x => x.Id);
        return operation.Changes.All(x => calendar.CanEdit(slots[x.Before.SlotId]) && calendar.CanEdit(slots[x.After.SlotId]));
    }

    public ProjectSession(ProjectDocument document, bool saved = false)
    {
        DocumentIntegrity.EnsureValid(document);
        Document = document.History.Any(x => x.DayTemplate is not null) ? SchoolPlanRebaser.Apply(document) : document;
        schoolRules = new SchoolDayRules(Document);
        SavedStateId = saved ? document.CurrentStateId : null;
        savedCalendar = saved ? document.Calendar : null;
        savedExclusions = saved ? SchoolDayRules.Entries(document) : [];
        savedMonthly = saved ? document.Monthly : null;
        savedSchema = saved ? document.SchemaVersion : 0; savedBaselineSlotCount = saved ? document.Baseline.Slots.Length : 0;
    }

    public ValidationResult Preview(ChangePlan plan)
    {
        if (plan.Revision != Revision) return ValidationResult.Error("STALE_PLAN", "시간표가 변경되었습니다. 미리보기를 다시 만드세요.");
        if (plan.Kind == ChangeKind.DayTemplate)
        {
            if (plan.DayTemplate is not { } day)
                return ValidationResult.Error("DAY_TEMPLATE", "요일 변경 정보가 없습니다.");
            var content = DayTemplatePlanner.Validate(Document.Current, day, Document.Calendar);
            if (!content.IsValid) return content;
            if (!DayTemplatePlanner.MatchesPattern(Document, day) || !plan.Changes.SequenceEqual(DayTemplatePlanner.Difference(day.BeforeLessons, day.AfterLessons)))
                return ValidationResult.Error("DAY_TEMPLATE", "요일 변경 미리보기 정보가 일치하지 않습니다.");
            if (Document.Calendar is not { } dates || !dates.CanEdit(day.Date))
                return ValidationResult.Error("WORK_PERIOD", "오늘 이전이거나 선택한 작업 기간 밖입니다.");
            if (day.BeforePeriods != DayTemplatePlanner.Periods(Document, day.Date))
                return ValidationResult.Error("DAY_PERIODS", "해당 날짜의 교시 수가 변경되었습니다. 다시 선택하세요.");
            var extended = DayTemplatePlanner.Extend(Document.Current, day);
            var rules = new SchoolDayRules(Document with { Current = extended });
            if (day.BeforeLessons.Concat(day.AfterLessons).Any(rules.IsExcluded))
                return ValidationResult.Error("GRADE_DATE_EXCLUDED", "행사 제외 학년이 있어 날짜 전체를 변경할 수 없습니다.");
            return content;
        }
        if (plan.DayTemplate is not null) return ValidationResult.Error("DAY_TEMPLATE", "요일 적용 명령 종류가 일치하지 않습니다.");
        if (Document.SchemaVersion >= 4 && !plan.Changes.IsDefaultOrEmpty && plan.Kind is (ChangeKind.Move or ChangeKind.AssignTeacher) && !DayTemplatePlanner.ActiveChanges(Document, plan.Changes))
            return ValidationResult.Error("INACTIVE_PERIOD", "이 날짜에 사용하지 않는 교시입니다. 먼저 기초 요일 시간표를 적용하세요.");
        if ((plan.Kind is ChangeKind.Move or ChangeKind.AssignTeacher) && !plan.Changes.IsDefaultOrEmpty &&
            !plan.Changes.All(schoolRules.Allows))
            return ValidationResult.Error("GRADE_DATE_EXCLUDED", "행사 제외 날짜·학년의 수업은 이동·보강하거나 빈 시간에 배치할 수 없습니다.");
        if ((plan.Kind is ChangeKind.Move or ChangeKind.AssignTeacher) && Document.Calendar is { } calendar && !plan.Changes.IsDefaultOrEmpty)
        {
            var slots = Document.Current.Slots.ToDictionary(x => x.Id);
            if (plan.Changes.Any(x => slots.TryGetValue(x.Before.SlotId, out var before) && !calendar.CanEdit(before) ||
                slots.TryGetValue(x.After.SlotId, out var after) && !calendar.CanEdit(after)))
                return ValidationResult.Error("PAST_DATE", "오늘 이전이거나 선택한 작업 기간 밖의 수업은 교체할 수 없습니다.");
        }
        return ValidateChanges(Document.Current, plan.Kind, plan.Changes);
    }

    internal static ValidationResult ValidateChanges(ScheduleState state, ChangeKind kind, ImmutableArray<LessonDelta> changes)
    {
        if (changes.IsDefaultOrEmpty) return ValidationResult.Error("NO_CHANGE", "변경할 내용이 없습니다.");
        var current = state.Lessons.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var affectedSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var delta in changes)
        {
            if (delta.Added || delta.Removed) return ValidationResult.Error("IDENTITY", "수업 추가·삭제는 날짜 전체 변경에서만 허용합니다.");
            if (!changed.Add(delta.Before.Id) || !current.TryGetValue(delta.Before.Id, out var old) || old != delta.Before)
                return ValidationResult.Error("STALE_LESSON", "수업이 바뀌었거나 같은 수업이 중복 지정되었습니다.");
            var next = delta.After;
            if (old.Id != next.Id || old.ClassId != next.ClassId || old.Subject != next.Subject || old.Room != next.Room || old.Source != next.Source)
                return ValidationResult.Error("IDENTITY", "수업 ID·학급·과목·교실·원본 좌표는 이 명령으로 변경할 수 없습니다.");
            var validKind = kind switch
            {
                ChangeKind.Move => old.TeacherId == next.TeacherId && old.Locked == next.Locked && !old.Locked && old.SlotId != next.SlotId,
                ChangeKind.AssignTeacher => old.SlotId == next.SlotId && old.Locked == next.Locked && !old.Locked && old.TeacherId != next.TeacherId && next.TeacherId is not null,
                ChangeKind.ToggleLock => old.SlotId == next.SlotId && old.TeacherId == next.TeacherId && old.Locked != next.Locked,
                _ => false
            };
            if (!validKind) return ValidationResult.Error("LOCK_OR_KIND", "잠긴 수업이거나 명령에서 허용하지 않는 변경입니다.");
            affectedSlots.Add(old.SlotId);
            affectedSlots.Add(next.SlotId);
            current[old.Id] = next;
        }
        // Unknown teachers cannot be assumed free during timetable movement.
        if (kind == ChangeKind.Move && state.Lessons.Any(x => x.TeacherId is null && affectedSlots.Contains(x.SlotId)))
            return ValidationResult.Error("UNKNOWN_AVAILABILITY", "관련 시간에 교사 미배정 수업이 있어 충돌 여부를 확인할 수 없습니다. 담당을 먼저 연결하세요.");
        return ConstraintValidator.Validate(state with { Lessons = state.Lessons.Select(x => current[x.Id]).ToImmutableArray() });
    }

    public ValidationResult Apply(ChangePlan plan)
    {
        var result = Preview(plan); // Every entry point uses this latest-state check.
        if (!result.IsValid) return result;
        if (plan.DayTemplate is { } planningDay)
        {
            try
            {
                var next = SchoolPlanRebaser.Apply(Document, planningDay);
                Document = next; schoolRules = new SchoolDayRules(next); Revision = Guid.NewGuid();
                return result;
            }
            catch (InvalidDataException ex) { return ValidationResult.Error("BASE_REBUILD", ex.Message); }
        }
        var operation = new Operation(Document.CurrentStateId, Guid.NewGuid(), DateTimeOffset.UtcNow, plan.Kind, plan.Description, plan.Changes, plan.DayTemplate);
        Document = Document with
        {
            Current = ApplyDeltas(Document.Current, plan.Changes, true),
            CurrentStateId = operation.AfterStateId,
            History = Document.History.Take(Document.HistoryCursor).Append(operation).ToImmutableArray(),
            HistoryCursor = Document.HistoryCursor + 1
        };
        schoolRules = new SchoolDayRules(Document);
        Revision = Guid.NewGuid();
        return result;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var operation = Document.History[Document.HistoryCursor - 1];
        Document = Document with { Current = operation.DayTemplate is { } day ? DayTemplatePlanner.Apply(Document.Current, day, false) : ApplyDeltas(Document.Current, operation.Changes, false), CurrentStateId = operation.BeforeStateId, HistoryCursor = Document.HistoryCursor - 1 };
        Revision = Guid.NewGuid();
        return true;
    }
    public ValidationResult ApplySpecialCover(string lessonId, string teacherId, Guid expectedRevision)
    {
        if (Revision != expectedRevision) return ValidationResult.Error("STALE_PLAN", "시간표가 바뀌었습니다. 보강 수업을 다시 선택하세요.");
        if (!SpecialCover.IsSpecial(teacherId)) return ValidationResult.Error("SPECIAL_ROLE", "교장·교감 특별 보강을 선택하세요.");
        // Validate and apply on a detached session; cancellation/failure never adds a teacher.
        var candidate = new ProjectSession(SpecialCover.WithTeacher(Document, teacherId));
        var plan = ChangePlanner.AssignTeacher(candidate, lessonId, teacherId) with
        { Description = SpecialCover.Name(teacherId) + " 특별 보강" };
        var result = candidate.Apply(plan);
        if (!result.IsValid) return result;
        Document = candidate.Document; schoolRules = new SchoolDayRules(Document); Revision = Guid.NewGuid();
        return result;
    }
    public void ResetToBaseline()
    {
        // Explicit whole-project reset, including locks and history. Dates and event settings remain.
        var initial = Guid.NewGuid();
        var baseline = Document.Baseline;
        var allocated = (Document.TemplateSlotIds ?? []).ToHashSet();
        baseline = baseline with { Slots = baseline.Slots.Where(x => !allocated.Contains(x.Id)).ToImmutableArray() };
        var used = baseline.Lessons.Select(x => x.TeacherId).Concat(baseline.Classes.Select(x => x.HomeroomTeacherId))
            .Concat(baseline.Blocks.Select(x => (string?)x.TeacherId)).ToHashSet();
        baseline = baseline with { Teachers = baseline.Teachers.Where(x => !SpecialCover.IsSpecial(x.Id) || used.Contains(x.Id)).ToImmutableArray() };
        var reset = Document with { Baseline = baseline, Current = baseline, InitialStateId = initial, CurrentStateId = initial, History = [], HistoryCursor = 0, TemplateSlotIds = null };
        DocumentIntegrity.EnsureValid(reset);
        Document = reset; schoolRules = new SchoolDayRules(Document); Revision = Guid.NewGuid();
    }
    public bool Redo()
    {
        if (!CanRedo) return false;
        var operation = Document.History[Document.HistoryCursor];
        Document = Document with { Current = operation.DayTemplate is { } day ? DayTemplatePlanner.Apply(Document.Current, day, true) : ApplyDeltas(Document.Current, operation.Changes, true), CurrentStateId = operation.AfterStateId, HistoryCursor = Document.HistoryCursor + 1 };
        Revision = Guid.NewGuid();
        return true;
    }
    public void SetCalendar(ScheduleCalendar calendar)
    {
        DocumentIntegrity.ValidateCalendar(calendar);
        if (Document.Monthly is not null && calendar.FirstWeekMonday != Document.Calendar?.FirstWeekMonday)
            throw new InvalidOperationException("기존 수업의 실제 날짜는 다시 해석할 수 없습니다. 조회할 월을 선택하세요.");
        if ((Document.History.Any(x => x.DayTemplate is not null) || Document.BaseDayPlans?.IsEmpty == false) && calendar.FirstWeekMonday != Document.Calendar?.FirstWeekMonday)
            throw new InvalidOperationException("기초 요일을 적용한 뒤에는 수업의 실제 날짜를 다시 해석할 수 없습니다.");
        if (Document.Calendar == calendar) return;
        Document = Document with { Calendar = calendar };
        schoolRules = new SchoolDayRules(Document);
        Revision = Guid.NewGuid(); // Any preview made under the old date interpretation is stale.
    }
    public void SetGradeDateExclusions(IEnumerable<GradeDateExclusion> entries)
    {
        var values = entries.Distinct().OrderBy(x => x.Date).ThenBy(x => x.Grade).ToImmutableArray();
        if (values.Any(x => x.Grade is < 1 or > 3) || !values.IsEmpty && Document.Calendar is null)
            throw new InvalidDataException("행사 제외는 날짜 설정 후 1~3학년으로 지정하세요.");
        if (values.SequenceEqual(SchoolDayRules.Entries(Document))) return;
        Document = Document with { SchemaVersion = Math.Max(2, Document.SchemaVersion), GradeDateExclusions = values };
        schoolRules = new SchoolDayRules(Document);
        Revision = Guid.NewGuid();
    }
    public void SelectMonth(DateOnly month)
    {
        var next = MonthlyTimetable.EnsureMonth(Document, month);
        Document = next; schoolRules = new SchoolDayRules(next); Revision = Guid.NewGuid();
    }
    public void SetWorkPeriod(DateOnly today, int weeksAhead)
    {
        var next = WorkPeriod.Prepare(Document, today, weeksAhead);
        Document = next; schoolRules = new SchoolDayRules(next); Revision = Guid.NewGuid();
    }
    public void IncludeViewMonth(DateOnly month)
    {
        var next = WorkPeriod.IncludeMonth(Document, month);
        Document = next; schoolRules = new SchoolDayRules(next); Revision = Guid.NewGuid();
    }
    public void MarkSaved(Guid writtenStateId) { SavedStateId = writtenStateId; savedCalendar = Document.Calendar; savedExclusions = SchoolDayRules.Entries(Document); savedMonthly = Document.Monthly; savedSchema = Document.SchemaVersion; savedBaselineSlotCount = Document.Baseline.Slots.Length; }
    public void MarkSaved(ProjectDocument written)
    {
        if (written.ProjectId != Document.ProjectId) throw new InvalidOperationException("다른 프로젝트의 저장 결과입니다.");
        SavedStateId = written.CurrentStateId;
        savedCalendar = written.Calendar;
        savedExclusions = SchoolDayRules.Entries(written);
        savedMonthly = written.Monthly;
        savedSchema = written.SchemaVersion; savedBaselineSlotCount = written.Baseline.Slots.Length;
    }

    internal static ScheduleState ApplyDeltas(ScheduleState state, ImmutableArray<LessonDelta> deltas, bool forward)
    {
        var map = deltas.ToDictionary(x => x.Before.Id, x => forward ? x.After : x.Before, StringComparer.Ordinal);
        return state with { Lessons = state.Lessons.Select(x => map.GetValueOrDefault(x.Id, x)).ToImmutableArray() };
    }
    internal static ProjectDocument RebuildExtendedLedger(ProjectDocument document)
    {
        var state = document.Baseline;
        var current = state;
        var history = ImmutableArray.CreateBuilder<Operation>();
        foreach (var (original, index) in document.History.Select((x, i) => (x, i)))
        {
            var operation = original;
            if (original.DayTemplate is { } day)
            {
                // Added baseline dates precede previously inserted lessons during forward replay.
                // Update the inverse ordering too, so Undo equals the preceding replay state.
                day = day with { BeforeOrder = state.Lessons.Select(x => x.Id).ToImmutableArray() };
                operation = original with { DayTemplate = day };
                state = DayTemplatePlanner.Apply(state, day, true);
            }
            else state = ApplyDeltas(state, original.Changes, true);
            history.Add(operation);
            if (index + 1 == document.HistoryCursor) current = state;
        }
        return document with { Current = current, History = history.ToImmutable() };
    }
}

public static class DocumentIntegrity
{
    public static void ValidateCalendar(ScheduleCalendar calendar)
    {
        if (calendar.FirstWeekMonday.DayOfWeek != DayOfWeek.Monday || calendar.FirstWeekMonday > DateOnly.MaxValue.AddDays(-27) ||
            calendar.EditableFrom > DateOnly.MaxValue.AddDays(-27) || calendar.WeeksAhead is < 0 or > 3 ||
            calendar.EditableUntil is { } end && end != BoardDateRange.Friday(calendar.EditableFrom, calendar.WeeksAhead))
            throw new InvalidDataException("1주 시작일은 월요일이어야 하며 날짜가 지원 범위를 벗어날 수 없습니다.");
    }
    public static void EnsureValid(ProjectDocument doc)
    {
        if (doc.SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6)) throw new InvalidDataException("지원하지 않는 프로젝트 버전입니다.");
        if (doc.SchemaVersion >= 6 && (doc.Calendar?.EditableUntil is null || doc.Monthly is null))
            throw new InvalidDataException("작업 기간 또는 반복 시간표 정보가 없습니다.");
        var result = ConstraintValidator.Validate(doc.Baseline);
        if (!result.IsValid) throw new InvalidDataException(string.Join("\n", result.Issues.Where(x => x.Severity == Severity.Error).Select(x => x.Message)));
        if (doc.History.IsDefault) throw new InvalidDataException("작업 이력이 없습니다.");
        if (doc.TemplateSlotIds is { } ids && (doc.SchemaVersion < 4 || ids.IsDefault || ids.Distinct().Count() != ids.Length || ids.Any(id => !doc.Baseline.Slots.Any(x => x.Id == id))))
            throw new InvalidDataException("요일 변경 교시 정보가 손상되었습니다.");
        var allocated = (doc.TemplateSlotIds ?? []).ToHashSet();
        if (doc.Baseline.Lessons.Any(x => allocated.Contains(x.SlotId)) || doc.Baseline.Blocks.Any(x => allocated.Contains(x.SlotId)) ||
            doc.History.Where(x => x.DayTemplate is not null).SelectMany(x => x.DayTemplate!.AdditionalSlots).Any(x => !allocated.Contains(x.Id)))
            throw new InvalidDataException("추가 교시와 기초 시간표·이력이 일치하지 않습니다.");
        if (doc.SchemaVersion < 3 && (doc.Monthly is not null || doc.Baseline.Slots.Any(x => x.Week > 4)))
            throw new InvalidDataException("월 단위 자료에는 새 저장 형식이 필요합니다.");
        if (doc.SchemaVersion < 5 && (doc.OriginalPattern is not null || doc.BaseDayPlans is not null) ||
            doc.SchemaVersion >= 5 && (doc.OriginalPattern is null || doc.OriginalPattern.Slots.IsDefaultOrEmpty ||
                !ConstraintValidator.Validate(doc.OriginalPattern).IsValid || doc.TemplateSlotIds is not null || doc.History.Any(x => x.DayTemplate is not null)))
            throw new InvalidDataException("학교 기초 배정 정보가 손상되었습니다.");
        if (doc.BaseDayPlans is { } plans && (plans.IsDefault || plans.Select(x => x.Date).Distinct().Count() != plans.Length ||
            doc.Calendar is null || plans.Any(x => x.SourceDay is < 1 or > 5 || x.Periods is < 1 or > 20 ||
                x.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                doc.Baseline.Slots.Where(s => doc.Calendar.DateOf(s) == x.Date).Select(s => s.Period).DefaultIfEmpty(0).Max() < x.Periods)))
            throw new InvalidDataException("날짜별 기초 요일 정보가 손상되었습니다.");
        if (doc.SchemaVersion >= 5 && doc.Calendar is { } plannedCalendar)
        {
            var limits = (doc.BaseDayPlans ?? []).ToDictionary(x => x.Date, x => x.Periods);
            var slotsById = doc.Baseline.Slots.ToDictionary(x => x.Id);
            if (doc.Baseline.Lessons.Concat(doc.Current.Lessons).Any(x => slotsById.TryGetValue(x.SlotId, out var slot) &&
                limits.TryGetValue(plannedCalendar.DateOf(slot), out var limit) && slot.Period > limit))
                throw new InvalidDataException("기초 요일의 활성 교시 밖에 수업이 배정되었습니다.");
        }
        var exclusions = SchoolDayRules.Entries(doc);
        if (exclusions.IsDefault || exclusions.Any(x => x is null || x.Grade is < 1 or > 3) ||
            exclusions.Distinct().Count() != exclusions.Length || !exclusions.IsEmpty && (doc.Calendar is null || doc.SchemaVersion < 2))
            throw new InvalidDataException("행사 제외 날짜·학년 설정이 손상되었습니다.");
        if (doc.Calendar is { } calendar)
        {
            ValidateCalendar(calendar);
            if (doc.Baseline.Slots.Any(x => (long)calendar.FirstWeekMonday.DayNumber + (long)(x.Week - 1) * 7 + x.Day - 1 > DateOnly.MaxValue.DayNumber))
                throw new InvalidDataException("시간표의 날짜 범위가 너무 큽니다.");
        }
        if (doc.Monthly is { } monthly)
        {
            if (doc.Calendar is null || monthly.Month.Day != 1 || monthly.Month.Year is < 1901 or > 9998 ||
                monthly.PatternMonday.DayOfWeek != DayOfWeek.Monday || monthly.CoverageId == Guid.Empty || monthly.Pattern is null ||
                monthly.Pattern.Slots.IsDefaultOrEmpty || monthly.Pattern.Slots.Any(x => x.Week is < 1 or > 4) || !ConstraintValidator.Validate(monthly.Pattern).IsValid)
                throw new InvalidDataException("반복 기초 시간표 정보가 손상되었습니다.");
            var range = MonthlyTimetable.Window(monthly.Month);
            if (doc.Baseline.Slots.IsDefaultOrEmpty || doc.SchemaVersion < 6 && (doc.Calendar.FirstWeekMonday > range.Start ||
                doc.Calendar.FirstWeekMonday.AddDays((doc.Baseline.Slots.Max(x => x.Week) - 1) * 7) < ScheduleCalendar.MondayOf(range.End)))
                throw new InvalidDataException("관리 월과 인접 주의 시간표 범위가 부족합니다.");
        }
        if (!Guid.TryParseExact(doc.ProjectId, "N", out _) || string.IsNullOrWhiteSpace(doc.Name) || doc.InitialStateId == Guid.Empty || doc.History.IsDefault || doc.HistoryCursor < 0 || doc.HistoryCursor > doc.History.Length)
            throw new InvalidDataException("프로젝트 정보·작업 이력이 손상되었습니다.");
        var replay = new ProjectSessionUnchecked(doc.Baseline, doc.InitialStateId);
        ScheduleState expected = doc.Baseline;
        Guid expectedId = doc.InitialStateId;
        var stateIds = new HashSet<Guid> { doc.InitialStateId };
        foreach (var (operation, index) in doc.History.Select((x, i) => (x, i)))
        {
            if (operation.BeforeStateId != replay.Id || !stateIds.Add(operation.AfterStateId) || operation.Changes.IsDefaultOrEmpty)
                throw new InvalidDataException("작업 이력 연결이 손상되었습니다.");
            var changes = operation.Changes;
            var atOperation = doc with { Current = replay.State, HistoryCursor = index };
            var valid = operation.Kind == ChangeKind.DayTemplate && operation.DayTemplate is { } template && doc.SchemaVersion >= 4
                ? DayTemplatePlanner.Validate(replay.State, template, doc.Calendar).IsValid && template.BeforePeriods == DayTemplatePlanner.Periods(atOperation, template.Date) && DayTemplatePlanner.MatchesPattern(doc, template) &&
                    changes.SequenceEqual(DayTemplatePlanner.Difference(template.BeforeLessons, template.AfterLessons))
                : operation.DayTemplate is null && ProjectSession.ValidateChanges(replay.State, operation.Kind, changes).IsValid &&
                    (doc.SchemaVersion < 4 || operation.Kind == ChangeKind.ToggleLock || DayTemplatePlanner.ActiveChanges(atOperation, changes));
            if (!valid)
                throw new InvalidDataException("작업 이력의 변경 규칙·잠금·수업 정보가 손상되었습니다.");
            var next = operation.DayTemplate is { } day ? DayTemplatePlanner.Apply(replay.State, day, true) : ProjectSession.ApplyDeltas(replay.State, changes, true);
            replay = new(next, operation.AfterStateId);
            if (index + 1 == doc.HistoryCursor) { expected = next; expectedId = operation.AfterStateId; }
        }
        if (expectedId != doc.CurrentStateId || !StateEquals(expected, doc.Current))
            throw new InvalidDataException("현재 시간표와 작업 이력이 일치하지 않습니다.");
    }
    public static bool StateEquals(ScheduleState a, ScheduleState b) =>
        !b.Teachers.IsDefault && !b.Classes.IsDefault && !b.Slots.IsDefault && !b.Lessons.IsDefault && !b.Blocks.IsDefault &&
        a.Teachers.SequenceEqual(b.Teachers) && a.Classes.SequenceEqual(b.Classes) && a.Slots.SequenceEqual(b.Slots) && a.Lessons.SequenceEqual(b.Lessons) && a.Blocks.SequenceEqual(b.Blocks);
    private sealed record ProjectSessionUnchecked(ScheduleState State, Guid Id);
}
