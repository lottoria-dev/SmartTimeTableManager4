using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class ProjectRepetition
{
    /// <summary>Keep week-one baseline/current/history; week two repeats the original baseline, with new lesson IDs.</summary>
    public static ProjectDocument CreateTwoWeekCopy(ProjectDocument source)
    {
        DocumentIntegrity.EnsureValid(source);
        if (source.Baseline.Slots.IsEmpty || source.Baseline.Slots.Any(x => x.Week != 1))
            throw new InvalidOperationException("1주 자료만 2주 반복본으로 만들 수 있습니다. 이미 2주 이상인 자료는 전체 주차 보기를 선택하세요.");
        var pattern = DayTemplatePlanner.OriginalPattern(source);
        var allocated = (source.TemplateSlotIds ?? []).ToHashSet();
        var slotIds = pattern.Slots.Where(x => !allocated.Contains(x.Id)).ToDictionary(x => x.Id, _ => Guid.NewGuid().ToString("N"));
        var extraSlots = pattern.Slots.Where(x => slotIds.ContainsKey(x.Id)).Select(x => x with { Id = slotIds[x.Id], Week = 2 }).ToImmutableArray();
        var extraLessons = pattern.Lessons.Select(x => x with
        {
            Id = Guid.NewGuid().ToString("N"), SlotId = slotIds[x.SlotId], Source = x.Source + " (기초 원본 2주 반복)"
        }).ToImmutableArray();
        var extraBlocks = pattern.Blocks.Select(x => x with { SlotId = slotIds[x.SlotId] }).ToImmutableArray();
        ScheduleState Extend(ScheduleState state) => state with
        { Slots = state.Slots.AddRange(extraSlots), Lessons = state.Lessons.AddRange(extraLessons), Blocks = state.Blocks.AddRange(extraBlocks) };
        var copy = source with
        {
            ProjectId = Guid.NewGuid().ToString("N"), Name = source.Name + " (2주)",
            Baseline = Extend(source.Baseline), Current = Extend(source.Current)
        };
        copy = ProjectSession.RebuildExtendedLedger(copy);
        DocumentIntegrity.EnsureValid(copy);
        return copy;
    }
}
