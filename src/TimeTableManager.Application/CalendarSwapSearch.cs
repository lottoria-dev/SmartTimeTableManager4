using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public enum SwapDirection { Both, Earlier, Later, SameDay }

/// <summary>Finish a bounded same-day search before admitting other dates for any participant.</summary>
public static class CalendarSwapSearch
{
    public static bool Allows(ScheduleCalendar calendar, TimeSlot source, TimeSlot target, SwapDirection direction) =>
        calendar.CanEdit(source) && calendar.CanEdit(target) && (direction switch
        {
            SwapDirection.Earlier => calendar.DateOf(target) <= calendar.DateOf(source),
            SwapDirection.Later => calendar.DateOf(target) >= calendar.DateOf(source),
            SwapDirection.SameDay => calendar.DateOf(target) == calendar.DateOf(source),
            _ => true
        });

    public static SwapSearchResult Find(ProjectSession session, string sourceId, string? targetId,
        IReadOnlySet<string> visible, SwapDirection direction, CancellationToken token, bool otherDaysOnly = false,
        bool allowConsecutive = false, IReadOnlySet<string>? immovable = null,
        IReadOnlySet<string>? avoidConsecutiveTeachers = null)
    {
        token.ThrowIfCancellationRequested();
        if (session.Document.Calendar is not { } calendar) return new(null, "먼저 날짜를 설정하세요.", 0);
        if (otherDaysOnly && direction == SwapDirection.SameDay) return new(null, "이전 또는 이후 날짜 교체 허용을 켜세요.", 0);
        var state = session.Document.Current;
        var consecutive = new ConsecutiveTeaching(state);
        var source = state.Lessons.Single(x => x.Id == sourceId);
        if (source.TeacherId is null) return new(null, "담당 교사가 없는 수업은 자동 이동할 수 없습니다.", 0);
        if (immovable?.Contains(sourceId) == true) return new(null, "선택 수업은 이번 탐색에서 이동을 허용하지 않은 수업입니다.", 0);
        bool AvoidRun(ConsecutiveRisk risk) => !allowConsecutive || avoidConsecutiveTeachers?.Contains(risk.TeacherId) == true;
        var slots = state.Slots.ToDictionary(x => x.Id); var origin = slots[source.SlotId];
        var school = new SchoolDayRules(session.Document);
        if (source.Locked || school.IsExcluded(source)) return new(null, "선택 수업은 잠금·행사 제외 수업입니다.", 0);
        var scope = state.Slots.Where(x => visible.Contains(x.Id) && Allows(calendar, origin, x, direction)).Select(x => x.Id).ToHashSet();
        if (!scope.Contains(origin.Id)) return new(null, "선택 수업이 오늘 이전이거나 작업 기간 밖입니다.", 0);
        var originDate = calendar.DateOf(origin);
        var candidates = state.Slots.Where(x => scope.Contains(x.Id) && x.Id != origin.Id && !school.IsExcluded(source.ClassId, x.Id) &&
                (!otherDaysOnly || calendar.DateOf(x) != originDate))
            .OrderBy(x => calendar.DateOf(x) == originDate ? 0 : 1)
            .ThenBy(x => Math.Abs(calendar.DateOf(x).DayNumber - originDate.DayNumber))
            .ThenBy(x => Math.Abs(x.Period - origin.Period)).ThenBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period).ToArray();
        if (targetId is not null)
        {
            candidates = candidates.Where(x => x.Id == targetId).ToArray();
            if (candidates.Length == 0) return new(null, "지정 목적은 날짜 허용·작업 기간·행사 제외 조건에 맞지 않습니다. 목적을 유지합니다.", 0);
        }
        var stages = new List<(string Name, HashSet<string> Scope)>();
        void AddStage(string name, Func<TimeSlot, bool> filter)
        {
            var selected = scope.Where(id => filter(slots[id])).ToHashSet();
            if (!selected.Contains(origin.Id) || !candidates.Any(x => selected.Contains(x.Id)) || stages.Any(x => x.Scope.SetEquals(selected))) return;
            stages.Add((name, selected));
        }
        AddStage("당일 안에서", x => calendar.DateOf(x) == originDate);
        if (direction != SwapDirection.SameDay)
        {
            foreach (int radius in new[]{1,2,3})
                AddStage($"선택 날짜 앞뒤 {radius}일의 허용 날짜에서", x => Math.Abs(calendar.DateOf(x).DayNumber-originDate.DayNumber) <= radius);
            AddStage("선택 날짜 앞뒤 1주의 허용 날짜에서", x => Math.Abs(calendar.DateOf(x).DayNumber-originDate.DayNumber) <= 7);
            AddStage("허용한 이전·이후 날짜까지 범위를 넓혀", _ => true);
        }
        int examined = 0;
        var targetNotice = targetId is null ? "" : $"목적 고정: {calendar.Label(slots[targetId])}. ";
        foreach (var stage in stages)
        {
            token.ThrowIfCancellationRequested();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            // A phase cannot consume the next phase's budget. Limits are not proofs of infeasibility.
            limit.CancelAfter(TimeSpan.FromMilliseconds(stages.Count == 1 ? 2000 : Math.Max(250, 2400 / stages.Count)));
            int used = 0;
            bool Fits(ChangePlan plan) => plan.Changes.All(x => immovable?.Contains(x.Before.Id) != true && stage.Scope.Contains(x.Before.SlotId) && stage.Scope.Contains(x.After.SlotId)) &&
                (targetId is null || plan.Changes.Any(x => x.Before.Id == sourceId && x.After.SlotId == targetId));
            SwapSearchResult Found(ChangePlan plan) => new(plan, targetNotice + stage.Name + " 교체안을 찾았습니다. 전후를 확인하고 적용하세요.", examined);
            try
            {
                // Even after widening helper dates, try same-day destinations (including chains) first.
                foreach (var group in candidates.Where(x => stage.Scope.Contains(x.Id)).GroupBy(x => calendar.DateOf(x) == originDate ? 0 : 1).OrderBy(x => x.Key))
                {
                    foreach (var target in group)
                    {
                        limit.Token.ThrowIfCancellationRequested();
                        if (used >= 200) break;
                        var direct = ChangePlanner.Move(session, sourceId, target.Id); examined++; used++;
                        if (Fits(direct) && session.Preview(direct).IsValid && (allowConsecutive && avoidConsecutiveTeachers?.Count is not > 0 || !consecutive.Evaluate(direct).Any(AvoidRun))) return Found(direct);
                    }
                    foreach (var target in group.Take(24))
                    {
                        limit.Token.ThrowIfCancellationRequested();
                        if (used >= 200) break;
                        var result = SwapSearch.Find(session, sourceId, target.Id, stage.Scope, limit.Token, maxNodes: Math.Min(100, 200 - used),
                            immovable: immovable, allowConsecutive: allowConsecutive, avoidConsecutiveTeachers: avoidConsecutiveTeachers);
                        used += result.Examined; examined += result.Examined;
                        if (result.Plan is { } plan && Fits(plan)) return Found(plan);
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Continue to the next permitted phase. */ }
        }
        token.ThrowIfCancellationRequested();
        return new(null, targetNotice + (direction == SwapDirection.SameDay
            ? "당일 제한 탐색에서 안을 찾지 못했습니다. 필요하면 이전·이후 날짜 허용을 켜세요."
            : "당일 우선 후 허용 날짜까지 제한 탐색했으나 안을 찾지 못했습니다. 수동 교체·보강을 검토하세요.") +
            (targetId is null ? "" : " 지정 목적은 다른 시간으로 대체하지 않습니다."), examined);
    }
}
