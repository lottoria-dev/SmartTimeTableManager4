using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record DayTemplateResult(ChangePlan? Plan, string Message);
/// <summary>Copy the immutable weekday pattern onto one date. Other dates are untouched.</summary>
public static class DayTemplatePlanner
{
    public static ScheduleState OriginalPattern(ProjectDocument document)
    {
        var allocated = (document.TemplateSlotIds ?? []).ToHashSet();
        var pattern = document.Monthly?.Pattern ?? document.OriginalPattern ?? document.Baseline;
        return allocated.Count == 0 ? pattern : pattern with
        { Slots = pattern.Slots.Where(x => !allocated.Contains(x.Id)).ToImmutableArray() };
    }
    public const string SourcePrefix = "요일 적용:";
    public static string? PatternId(LessonInstance lesson)
    {
        if (!lesson.Source.StartsWith(SourcePrefix, StringComparison.Ordinal)) return null;
        var reference = lesson.Source[SourcePrefix.Length..];
        int separator = reference.IndexOf(':');
        return separator < 0 ? null : reference[(separator + 1)..];
    }
    public static LessonInstance? PatternLesson(ProjectDocument document, LessonInstance lesson) =>
        PatternId(lesson) is { } id ? OriginalPattern(document).Lessons.FirstOrDefault(x => x.Id == id) : null;
    public static bool IsCover(ProjectDocument document, LessonInstance lesson)
    {
        var original = document.SchemaVersion >= 5 ? document.Baseline.Lessons.FirstOrDefault(x => x.Id == lesson.Id) :
            PatternLesson(document, lesson) ?? document.Baseline.Lessons.FirstOrDefault(x => x.Id == lesson.Id);
        return original is not null && original.TeacherId != lesson.TeacherId;
    }
    public static ImmutableArray<LessonDelta> Difference(IEnumerable<LessonInstance> before, IEnumerable<LessonInstance> after)
    {
        var a = before.ToDictionary(x => x.Id); var b = after.ToDictionary(x => x.Id);
        return a.Keys.Concat(b.Keys).Distinct().Select(id => !a.TryGetValue(id, out var old)
            ? new LessonDelta(b[id], b[id], Added: true) : !b.TryGetValue(id, out var next)
            ? new LessonDelta(old, old, Removed: true) : new LessonDelta(old, next))
            .Where(x => x.Added || x.Removed || x.Before != x.After).ToImmutableArray();
    }
    public static int Periods(ProjectDocument document, DateOnly date)
        => PeriodCounts(document).GetValueOrDefault(date);
    public static Dictionary<DateOnly, int> PeriodCounts(ProjectDocument document)
    {
        if (document.Calendar is not { } calendar) return [];
        var allocated = (document.TemplateSlotIds ?? []).ToHashSet();
        var counts = document.Baseline.Slots.Where(x => !allocated.Contains(x.Id)).GroupBy(calendar.DateOf)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Period));
        foreach (var day in document.BaseDayPlans ?? []) counts[day.Date] = day.Periods;
        foreach (var operation in document.History.Take(document.HistoryCursor))
            if (operation.DayTemplate is { } day) counts[day.Date] = day.AfterPeriods;
        return counts;
    }
    public static bool ActiveSlot(ProjectDocument document, TimeSlot slot) => document.Calendar is not { } calendar ||
        slot.Period <= Periods(document, calendar.DateOf(slot));
    public static bool ActiveChanges(ProjectDocument document, IEnumerable<LessonDelta> changes)
    {
        if (document.Calendar is not { } calendar) return true;
        var counts = PeriodCounts(document); var slots = document.Current.Slots.ToDictionary(x => x.Id);
        return changes.SelectMany(x => new[]{x.Before.SlotId,x.After.SlotId}).Distinct().All(id =>
            slots.TryGetValue(id, out var slot) && slot.Period <= counts.GetValueOrDefault(calendar.DateOf(slot)));
    }
    public static ScheduleState Extend(ScheduleState state, DayTemplateChange day) => state with
    { Slots = state.Slots.Concat(day.AdditionalSlots.Where(x => !state.Slots.Any(s => s.Id == x.Id)))
        .OrderBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period).ToImmutableArray() };
    public static ScheduleState Apply(ScheduleState state, DayTemplateChange day, bool forward)
    {
        state = Extend(state, day);
        var remove = (forward ? day.BeforeLessons : day.AfterLessons).Select(x => x.Id).ToHashSet();
        var put = (forward ? day.AfterLessons : day.BeforeLessons).ToDictionary(x => x.Id);
        var kept = state.Lessons.Where(x => !remove.Contains(x.Id) || put.ContainsKey(x.Id))
            .Select(x => put.GetValueOrDefault(x.Id, x)).ToList();
        var present = kept.Select(x => x.Id).ToHashSet();
        kept.AddRange(put.Values.Where(x => !present.Contains(x.Id)));
        if (!forward)
        {
            var order = day.BeforeOrder.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            kept = kept.OrderBy(x => order.GetValueOrDefault(x.Id, int.MaxValue)).ToList();
        }
        return state with { Lessons = kept.ToImmutableArray() };
    }
    public static DayTemplateResult Create(ProjectSession session, DateOnly date, int sourceDay, bool allowLocked = false)
    {
        var document = session.Document; var calendar = document.Calendar;
        if (calendar is null || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || sourceDay is < 1 or > 5)
            return new(null, "변경 날짜와 기초 요일(월~금)을 확인하세요.");
        if (date < calendar.EditableFrom) return new(null, "편집 허용 시작일 이전 날짜입니다.");
        var state = document.Current;
        var at = state.Slots.Where(x => calendar.DateOf(x) == date).ToDictionary(x => x.Period);
        if (at.Count == 0) return new(null, "선택 날짜가 저장된 시간표 범위 밖입니다.");
        var pattern = OriginalPattern(document);
        var allocated = (document.TemplateSlotIds ?? []).ToHashSet();
        if (pattern.Slots.IsEmpty) return new(null, "기초 시간표가 비어 있습니다.");
        var cycle = pattern.Slots.Max(x => x.Week);
        var monday = document.Monthly?.PatternMonday ?? calendar.FirstWeekMonday;
        int offset = (ScheduleCalendar.MondayOf(date).DayNumber - monday.DayNumber) / 7;
        int week = (offset % cycle + cycle) % cycle + 1;
        var sourceSlots = pattern.Slots.Where(x => x.Week == week && x.Day == sourceDay && !allocated.Contains(x.Id)).OrderBy(x => x.Period).ToArray();
        if (sourceSlots.Length == 0) return new(null, "기초 시간표에 선택한 요일 자료가 없습니다.");
        var additions = sourceSlots.Where(x => !at.ContainsKey(x.Period)).Select(x =>
            new TimeSlot(Guid.NewGuid().ToString("N"), at.Values.First().Week, at.Values.First().Day, x.Period)).ToImmutableArray();
        foreach (var slot in additions) at.Add(slot.Period, slot);
        var existingSlots = state.Slots.ToDictionary(x => x.Id);
        var before = state.Lessons.Where(x => calendar.DateOf(existingSlots[x.SlotId]) == date).OrderBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray();
        var existing = before.ToDictionary(x => (x.ClassId, existingSlots[x.SlotId].Period));
        var sourceLookup = sourceSlots.ToDictionary(x => x.Id);
        var after = pattern.Lessons.Where(x => sourceLookup.ContainsKey(x.SlotId)).Select(x =>
        {
            int period = sourceLookup[x.SlotId].Period;
            return x with { Id = existing.GetValueOrDefault((x.ClassId, period))?.Id ?? Guid.NewGuid().ToString("N"),
                SlotId = at[period].Id, Locked = existing.GetValueOrDefault((x.ClassId, period))?.Locked == true, Source = $"{SourcePrefix}{sourceDay}:{x.Id}" };
        }).OrderBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray();
        if (after.IsEmpty) return new(null, "선택한 기초 요일에 수업이 없습니다.");
        if (Periods(document, date) == sourceSlots.Max(x => x.Period) && before.Length == after.Length &&
            before.Zip(after).All(x => (x.First with { Source = x.Second.Source }) == x.Second))
            return new(null, "해당 날짜가 이미 선택한 기초 요일 시간표와 같습니다.");
        var day = new DayTemplateChange(date, sourceDay, Periods(document, date), sourceSlots.Max(x => x.Period), additions, before, after,
            state.Lessons.Select(x => x.Id).ToImmutableArray(), allowLocked);
        var changes = Difference(before, after);
        var plan = new ChangePlan(session.Revision, ChangeKind.DayTemplate,
            $"기초 {"월화수목금"[sourceDay - 1]}요일 적용 · {date:M/d} · {day.BeforePeriods}→{day.AfterPeriods}교시", changes, day);
        var validation = session.Preview(plan);
        if (!validation.IsValid) return new(null, string.Join(" / ", validation.Issues.Where(x => x.Severity == Severity.Error).Select(x => x.Message).Distinct()));
        return new(plan, $"{date:M/d}에 기초 {"월화수목금"[sourceDay - 1]}요일 수업 {after.Length}개를 적용합니다. 다른 날짜는 유지합니다. 이 날짜의 기초 배정으로 반영합니다.");
    }
    public static ValidationResult Validate(ScheduleState state, DayTemplateChange day, ScheduleCalendar? calendar)
    {
        if (calendar is null || day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || day.SourceDay is < 1 or > 5 || day.BeforePeriods is < 1 or > 20 || day.AfterPeriods is < 1 or > 20 ||
            day.AdditionalSlots.IsDefault || day.BeforeLessons.IsDefault || day.AfterLessons.IsDefaultOrEmpty ||
            day.BeforeOrder.IsDefault || day.BeforeOrder.Distinct().Count() != day.BeforeOrder.Length ||
            day.AdditionalSlots.Select(x => x.Id).Distinct().Count() != day.AdditionalSlots.Length ||
            day.AfterLessons.Select(x => x.Id).Distinct().Count() != day.AfterLessons.Length ||
            day.BeforeLessons.Select(x => x.Id).Distinct().Count() != day.BeforeLessons.Length)
            return ValidationResult.Error("DAY_TEMPLATE", "요일 적용 정보가 올바르지 않습니다.");
        if (!day.BeforeOrder.SequenceEqual(state.Lessons.Select(x => x.Id)))
            return ValidationResult.Error("DAY_ORDER", "요일 변경의 이전 수업 순서가 일치하지 않습니다.");
        var extended = Extend(state, day); var slots = extended.Slots.ToDictionary(x => x.Id);
        if (day.AdditionalSlots.Any(x => calendar.DateOf(x) != day.Date ||
            state.Slots.Any(s => s.Id == x.Id && s != x)) ||
            !day.BeforeLessons.OrderBy(x => x.Id, StringComparer.Ordinal).SequenceEqual(state.Lessons
                .Where(x => calendar.DateOf(slots[x.SlotId]) == day.Date).OrderBy(x => x.Id, StringComparer.Ordinal)))
            return ValidationResult.Error("STALE_DAY", "해당 날짜 시간표가 변경되었습니다. 다시 선택하세요.");
        if (!day.AllowLocked && day.BeforeLessons.Any(x => x.Locked)) return ValidationResult.Error("DAY_LOCK", "해당 날짜에 잠긴 수업이 있습니다. 먼저 잠금을 해제하세요.");
        if (day.AfterLessons.Any(x => x.TeacherId is null || !slots.TryGetValue(x.SlotId, out var slot) ||
            calendar.DateOf(slot) != day.Date || slot.Period > day.AfterPeriods || !x.Source.StartsWith(SourcePrefix, StringComparison.Ordinal)))
            return ValidationResult.Error("DAY_CONTENT", "기초 요일의 수업·담당·교시 정보를 확인하세요.");
        var locks = day.BeforeLessons.ToDictionary(x => (x.ClassId, slots[x.SlotId].Period), x => x.Locked);
        if (day.AfterLessons.Any(x => x.Locked != locks.GetValueOrDefault((x.ClassId, slots[x.SlotId].Period))))
            return ValidationResult.Error("DAY_LOCK_STATE", "같은 학급·교시의 잠금 상태가 일치하지 않습니다.");
        return ConstraintValidator.Validate(Apply(extended, day, true));
    }
    public static bool MatchesPattern(ProjectDocument document, DayTemplateChange day)
    {
        if (document.Calendar is not { } calendar) return false;
        var pattern = OriginalPattern(document);
        var allocated = (document.TemplateSlotIds ?? []).ToHashSet();
        if (pattern.Slots.IsDefaultOrEmpty) return false;
        int cycle = pattern.Slots.Max(x => x.Week);
        int offset = (ScheduleCalendar.MondayOf(day.Date).DayNumber - (document.Monthly?.PatternMonday ?? calendar.FirstWeekMonday).DayNumber) / 7;
        int week = (offset % cycle + cycle) % cycle + 1;
        var sourceSlots = pattern.Slots.Where(x => x.Week == week && x.Day == day.SourceDay && !allocated.Contains(x.Id)).ToDictionary(x => x.Id);
        var lessons = pattern.Lessons.Where(x => sourceSlots.ContainsKey(x.SlotId)).ToDictionary(x => x.Id);
        var targets = Extend(document.Current, day).Slots.ToDictionary(x => x.Id);
        return sourceSlots.Count > 0 && day.AfterPeriods == sourceSlots.Values.Max(x => x.Period) && lessons.Count == day.AfterLessons.Length &&
            day.AfterLessons.Select(PatternId).Distinct().Count() == lessons.Count && day.AfterLessons.All(x =>
                x.Source.StartsWith($"{SourcePrefix}{day.SourceDay}:", StringComparison.Ordinal) && PatternId(x) is { } id && lessons.TryGetValue(id, out var original) &&
                x.ClassId == original.ClassId && x.Subject == original.Subject && x.TeacherId == original.TeacherId && x.Room == original.Room &&
                targets.TryGetValue(x.SlotId, out var slot) && slot.Period == sourceSlots[original.SlotId].Period);
    }
}
