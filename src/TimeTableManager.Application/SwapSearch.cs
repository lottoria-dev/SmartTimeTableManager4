using System.Collections.Immutable;
using System.Diagnostics;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record SwapSearchResult(ChangePlan? Plan, string Message, int Examined);

/// <summary>Local bounded search, preserving each lesson's class and teacher. Never edits the session.</summary>
public static class SwapSearch
{
    private sealed record Node(ImmutableArray<LessonInstance> Lessons, int Depth);

    public static SwapSearchResult Find(ProjectSession session, string sourceId, string targetSlot,
        IReadOnlySet<string> scope, CancellationToken token, int maxNodes = 600, int maxChanged = 6,
        IReadOnlySet<string>? immovable = null, bool allowConsecutive = false, IReadOnlySet<string>? consecutiveExemptTeachers = null,
        IReadOnlySet<string>? avoidConsecutiveTeachers = null)
    {
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var original = session.Document.Current;
        var consecutive = new ConsecutiveTeaching(original);
        var school = new SchoolDayRules(session.Document);
        var revision = session.Revision;
        var source = original.Lessons.Single(x => x.Id == sourceId);
        bool AvoidRun(ConsecutiveRisk risk) => avoidConsecutiveTeachers?.Contains(risk.TeacherId) == true ||
            !allowConsecutive && consecutiveExemptTeachers?.Contains(risk.TeacherId) != true;
        if (!scope.Contains(source.SlotId) || !scope.Contains(targetSlot))
            return new(null, "출발과 목적 교시가 현재 조회 범위 안에 있어야 합니다.", 0);
        var first = ChangePlanner.Move(session, sourceId, targetSlot);
        if (first.Changes.Any(x => immovable?.Contains(x.Before.Id) == true))
            return new(null, "이미 확정한 탐색 위치를 유지해야 하는 수업과 겹칩니다.", 0);
        if (session.Preview(first).IsValid && (allowConsecutive && avoidConsecutiveTeachers?.Count is not > 0 || !consecutive.Evaluate(first).Any(AvoidRun)))
            return new(first, "직접 교체가 가능합니다.", 0);
        if (first.Changes.IsEmpty || source.Locked || first.Changes.Any(x => x.Before.Locked || !school.Allows(x)))
            return new(null, "같은 칸이거나 잠금·학년 행사 제외 수업입니다.", 0);
        if (original.Lessons.Any(x => x.TeacherId is null && (x.SlotId == source.SlotId || x.SlotId == targetSlot)))
            return new(null, "출발·목적 시간의 담당 교사가 모두 연결되어야 합니다.", 0);
        var baseById = original.Lessons.ToDictionary(x => x.Id);
        var slots = original.Slots.ToDictionary(x => x.Id);
        var blocks = original.Blocks.Select(x => (x.TeacherId, x.SlotId)).ToHashSet();
        var firstMap = first.Changes.ToDictionary(x => x.Before.Id, x => x.After);
        var initial = original.Lessons.Select(x => firstMap.GetValueOrDefault(x.Id, x)).ToImmutableArray();
        var queue = new PriorityQueue<Node, (int Changed, int Depth, int Order)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var destinations = original.Slots.Where(x => scope.Contains(x.Id)).Select(x => x.Id).ToArray();
        int order = 0, examined = 0, generated = 0;
        bool limited = false;
        bool BudgetEnded() => watch.Elapsed >= TimeSpan.FromSeconds(2) || generated >= 1000;
        void Enqueue(ImmutableArray<LessonInstance> lessons, int depth)
        {
            if (BudgetEnded()) { limited = true; return; }
            var changes = lessons.Count(x => x.SlotId != baseById[x.Id].SlotId);
            if (changes > maxChanged) { limited = true; return; }
            var key = string.Join("|", lessons.Select(x => x.SlotId.Length + ":" + x.SlotId));
            if (!visited.Add(key)) return;
            generated++;
            queue.Enqueue(new(lessons, depth), (changes, depth, order++));
        }
        Enqueue(initial, 1);
        while (queue.Count > 0 && examined < maxNodes && !BudgetEnded())
        {
            token.ThrowIfCancellationRequested();
            var node = queue.Dequeue(); examined++;
            var deltas = node.Lessons.Where(x => x != baseById[x.Id]).Select(x => new LessonDelta(baseById[x.Id], x)).ToImmutableArray();
            var plan = new ChangePlan(revision, ChangeKind.Move, $"자동 교체안 · {deltas.Length}개 수업", deltas);
            var valid = session.Preview(plan).IsValid;
            IReadOnlyList<ConsecutiveRisk> risks = valid && (!allowConsecutive || avoidConsecutiveTeachers?.Count > 0) ? consecutive.Evaluate(plan).Where(AvoidRun).ToArray() : [];
            if (valid && risks.Count == 0)
                return new(plan, $"{deltas.Length}개 수업을 바꾸는 안을 찾았습니다. 적용 전 표시된 셀을 확인하세요.", examined);
            if (node.Depth >= 5) { limited = true; continue; }
            var classOccupancy = node.Lessons.ToDictionary(x => (x.ClassId, x.SlotId));
            // Resolve one resource conflict at a time; class occupancy is preserved by pair swaps.
            var conflict = node.Lessons.Where(x => x.TeacherId is not null)
                .GroupBy(x => (x.TeacherId, x.SlotId)).FirstOrDefault(x => x.Count() > 1)?.ToArray()
                ?? node.Lessons.Where(x => x.Room is not null).GroupBy(x => (x.Room, x.SlotId))
                    .FirstOrDefault(x => x.Count() > 1)?.ToArray()
                ?? node.Lessons.Where(x => x.TeacherId is { } teacher && blocks.Contains((teacher, x.SlotId))).Take(1).ToArray();
            // A valid but consecutive-heavy placement is not a dead end: move a helper in that run.
            if (valid && risks.Count > 0)
            {
                var risk = risks[0];
                conflict = node.Lessons.Where(x => x.TeacherId == risk.TeacherId &&
                    slots[x.SlotId].Week == risk.Day.Week && slots[x.SlotId].Day == risk.Day.Day &&
                    slots[x.SlotId].Period >= risk.FirstPeriod && slots[x.SlotId].Period <= risk.LastPeriod).ToArray();
            }
            foreach (var lesson in conflict)
            {
                if (lesson.Id == sourceId || immovable?.Contains(lesson.Id) == true || lesson.Locked || school.IsExcluded(lesson) || lesson.TeacherId is null || !scope.Contains(lesson.SlotId)) continue;
                foreach (var destination in destinations)
                {
                    token.ThrowIfCancellationRequested();
                    if (BudgetEnded()) { limited = true; break; }
                    if (destination == lesson.SlotId || school.IsExcluded(lesson.ClassId, destination)) continue;
                    var other = classOccupancy.GetValueOrDefault((lesson.ClassId, destination));
                    if (other is not null && (other.Id == sourceId || immovable?.Contains(other.Id) == true || other.Locked || school.IsExcluded(other) || school.IsExcluded(other.ClassId, lesson.SlotId) || other.TeacherId is null)) continue;
                    var next = node.Lessons.Select(x => x.Id == lesson.Id ? x with { SlotId = destination }
                        : other is not null && x.Id == other.Id ? x with { SlotId = lesson.SlotId } : x).ToImmutableArray();
                    Enqueue(next, node.Depth + 1);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(null, limited || queue.Count > 0
            ? "탐색 한도에서 교체안을 찾지 못했습니다. 불가능하다는 뜻은 아닙니다. 범위나 목적 교시를 바꿔 보세요."
            : "현재 제한 탐색에서 교체안을 찾지 못했습니다. 다른 목적 교시를 선택해 보세요.", examined);
    }
}
