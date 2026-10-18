using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

/// <summary>School plans change the baseline. Connected old edits are absorbed, unrelated edits remain undoable.</summary>
public static class SchoolPlanRebaser
{
    public static ProjectDocument Apply(ProjectDocument document, DayTemplateChange? replacement = null)
    {
        var calendar = document.Calendar ?? throw new InvalidDataException("날짜 설정이 필요합니다.");
        var history = document.History.Take(document.HistoryCursor).ToArray();
        var pattern = DayTemplatePlanner.OriginalPattern(document);
        var periods = DayTemplatePlanner.PeriodCounts(document);
        var planned = (document.BaseDayPlans ?? []).ToDictionary(x => x.Date);
        foreach (var operation in history)
            if (operation.DayTemplate is { } old) planned[old.Date] = new(old.Date, old.SourceDay, old.AfterPeriods);
        var desired = replacement is null ? document.Current : DayTemplatePlanner.Apply(document.Current, replacement, true);
        if (replacement is { } day)
        { periods[day.Date] = day.AfterPeriods; planned[day.Date] = new(day.Date, day.SourceDay, day.AfterPeriods); }
        var allSlots = desired.Slots.ToDictionary(x => x.Id);
        // Retain native slots and teacher availability while a planned day hides its last periods.
        // Only unused legacy neutral allocations can be removed safely.
        var legacyNeutral = (document.TemplateSlotIds ?? []).ToHashSet();
        var retainedSlots = desired.Slots.Where(x => !legacyNeutral.Contains(x.Id) || x.Period <= periods.GetValueOrDefault(calendar.DateOf(x))).ToImmutableArray();
        var retainedIds = retainedSlots.Select(x => x.Id).ToHashSet();
        desired = desired with { Slots = retainedSlots, Blocks = desired.Blocks.Where(x => retainedIds.Contains(x.SlotId)).ToImmutableArray() };
        var absorbed = new HashSet<string>();
        foreach (var operation in history)
            if (operation.DayTemplate is { } old)
            {
                absorbed.UnionWith(old.BeforeLessons.Concat(old.AfterLessons).Select(x => x.Id));
                absorbed.UnionWith(document.Baseline.Lessons.Where(x => calendar.DateOf(allSlots[x.SlotId]) == old.Date).Select(x => x.Id));
                absorbed.UnionWith(history.SelectMany(x => x.Changes).Where(x =>
                    calendar.DateOf(allSlots[x.Before.SlotId]) == old.Date || calendar.DateOf(allSlots[x.After.SlotId]) == old.Date).Select(x => x.Before.Id));
            }
        if (replacement is { } target)
        {
            absorbed.UnionWith(target.BeforeLessons.Concat(target.AfterLessons).Select(x => x.Id));
            absorbed.UnionWith(document.Baseline.Lessons.Where(x => calendar.DateOf(allSlots[x.SlotId]) == target.Date).Select(x => x.Id));
            absorbed.UnionWith(history.SelectMany(x => x.Changes).Where(x =>
                calendar.DateOf(allSlots[x.Before.SlotId]) == target.Date || calendar.DateOf(allSlots[x.After.SlotId]) == target.Date).Select(x => x.Before.Id));
        }
        absorbed.UnionWith(document.Baseline.Lessons.Where(x => !retainedIds.Contains(x.SlotId)).Select(x => x.Id));
        // Keep complete chains together. Otherwise a retained half-swap can collide with the new fixed baseline.
        bool expanded;
        ScheduleState baseline;
        do
        {
            expanded = false;
            bool linked;
            do
            {
                linked = false;
                foreach (var operation in history)
                    if (operation.Changes.Any(x => absorbed.Contains(x.Before.Id)))
                        foreach (var change in operation.Changes)
                            linked |= absorbed.Add(change.Before.Id);
                expanded |= linked;
            } while (linked);
            baseline = document.Baseline with
            {
                Slots = retainedSlots, Blocks = desired.Blocks,
                Lessons = document.Baseline.Lessons.Where(x => !absorbed.Contains(x.Id))
                    .Concat(desired.Lessons.Where(x => absorbed.Contains(x.Id))).ToImmutableArray()
            };
            var replay = baseline;
            AddConflicts(replay);
            foreach (var operation in history.Where(x => x.DayTemplate is null))
            {
                replay = ProjectSession.ApplyDeltas(replay, operation.Changes.Where(x => !absorbed.Contains(x.Before.Id)).ToImmutableArray(), true);
                AddConflicts(replay);
            }
            void AddConflicts(ScheduleState state)
            {
                IEnumerable<IGrouping<string, LessonInstance>> Groups(Func<LessonInstance, string> key, Func<LessonInstance, bool>? include = null) =>
                    state.Lessons.Where(include ?? (_ => true)).GroupBy(key).Where(g => g.Count() > 1);
                var collisions = Groups(x => x.ClassId + "\0" + x.SlotId)
                    .Concat(Groups(x => x.TeacherId + "\0" + x.SlotId, x => x.TeacherId is not null))
                    .Concat(Groups(x => x.Room + "\0" + x.SlotId, x => !string.IsNullOrWhiteSpace(x.Room)));
                foreach (var lesson in collisions.SelectMany(x => x)) expanded |= absorbed.Add(lesson.Id);
            }
        } while (expanded);
        var initial = Guid.NewGuid(); var currentId = initial;
        var rebuilt = ImmutableArray.CreateBuilder<Operation>(); var current = baseline;
        foreach (var original in history.Where(x => x.DayTemplate is null))
        {
            var changes = original.Changes.Where(x => !absorbed.Contains(x.Before.Id)).ToImmutableArray();
            if (changes.IsEmpty) continue;
            var validation = ProjectSession.ValidateChanges(current, original.Kind, changes);
            if (!validation.IsValid) throw new InvalidDataException("기초 배정 갱신 후 교체 이력을 유지할 수 없습니다: " + string.Join(" / ", validation.Issues.Select(x => x.Message)));
            var nextId = Guid.NewGuid();
            rebuilt.Add(original with { BeforeStateId = currentId, AfterStateId = nextId, Changes = changes });
            current = ProjectSession.ApplyDeltas(current, changes, true); currentId = nextId;
        }
        if (!current.Lessons.OrderBy(x => x.Id, StringComparer.Ordinal).SequenceEqual(desired.Lessons.OrderBy(x => x.Id, StringComparer.Ordinal)))
            throw new InvalidDataException("기초 배정 갱신 전후의 다른 날짜 수업이 일치하지 않습니다.");
        var result = document with
        {
            SchemaVersion = Math.Max(5, document.SchemaVersion), OriginalPattern = pattern, BaseDayPlans = planned.Values.OrderBy(x => x.Date).ToImmutableArray(),
            TemplateSlotIds = null, Baseline = baseline, Current = current, InitialStateId = initial, CurrentStateId = currentId,
            History = rebuilt.ToImmutable(), HistoryCursor = rebuilt.Count
        };
        DocumentIntegrity.EnsureValid(result);
        return result;
    }
}
