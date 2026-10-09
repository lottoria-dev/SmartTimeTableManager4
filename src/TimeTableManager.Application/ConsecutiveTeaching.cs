using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record ConsecutiveRisk(string TeacherId, TimeSlot Day, int FirstPeriod, int LastPeriod)
{
    public int Length => LastPeriod - FirstPeriod + 1;
}

/// <summary>New or extended runs only. Unchanged existing runs do not block unrelated work.</summary>
public sealed class ConsecutiveTeaching
{
    private readonly Dictionary<string, TimeSlot> slots;
    private readonly ScheduleState state;
    private readonly Dictionary<(string Teacher, int Week, int Day), HashSet<int>> original;
    private readonly Dictionary<(int Week, int Day), TimeSlot> days;
    public ConsecutiveTeaching(ScheduleState state)
    {
        this.state = state;
        slots = state.Slots.ToDictionary(x => x.Id);
        days = state.Slots.GroupBy(x => (x.Week, x.Day)).ToDictionary(x => x.Key, x => x.First());
        original = Index(state.Lessons);
    }
    private Dictionary<(string Teacher, int Week, int Day), HashSet<int>> Index(IEnumerable<LessonInstance> lessons) =>
        lessons.Where(x => x.TeacherId is not null).GroupBy(x => Key(x))
            .ToDictionary(g => g.Key, g => g.Select(x => slots[x.SlotId].Period).ToHashSet());
    private (string Teacher, int Week, int Day) Key(LessonInstance lesson)
    {
        var slot = slots[lesson.SlotId]; return (lesson.TeacherId!, slot.Week, slot.Day);
    }
    public IReadOnlyList<ConsecutiveRisk> Evaluate(ChangePlan plan)
    {
        if (plan.DayTemplate is { } day)
        {
            var extended = DayTemplatePlanner.Extend(state, day);
            return new ConsecutiveTeaching(extended).Evaluate(DayTemplatePlanner.Apply(extended, day, true).Lessons);
        }
        var changed = plan.Changes.Where(x => x.Before.SlotId != x.After.SlotId || x.Before.TeacherId != x.After.TeacherId).ToArray();
        var after = changed.SelectMany(x => new[] { x.Before, x.After }).Where(x => x.TeacherId is not null)
            .Select(Key).Distinct().ToDictionary(k => k, k => new HashSet<int>(original.GetValueOrDefault(k) ?? []));
        // Remove every departure before adding arrivals, so pair swaps do not erase a retained period.
        foreach (var change in changed)
            if (change.Before.TeacherId is not null) after[Key(change.Before)].Remove(slots[change.Before.SlotId].Period);
        foreach (var change in changed)
            if (change.After.TeacherId is not null) after[Key(change.After)].Add(slots[change.After.SlotId].Period);
        return Compare(after);
    }
    public IReadOnlyList<ConsecutiveRisk> Evaluate(IEnumerable<LessonInstance> after) => Compare(Index(after));
    private IReadOnlyList<ConsecutiveRisk> Compare(Dictionary<(string Teacher, int Week, int Day), HashSet<int>> after)
    {
        var result = new List<ConsecutiveRisk>();
        foreach (var (key, periods) in after)
            foreach (var (first, last) in NewRuns(original.GetValueOrDefault(key) ?? [], periods))
                result.Add(new(key.Teacher, days[(key.Week, key.Day)], first, last));
        return result.OrderBy(x => x.Day.Week).ThenBy(x => x.Day.Day).ThenBy(x => x.TeacherId).ThenBy(x => x.FirstPeriod).ToArray();
    }
    public static IReadOnlyList<(int First, int Last)> NewRuns(IEnumerable<int> before, IEnumerable<int> after)
    {
        var previous = before.ToHashSet(); var periods = after.Distinct().Order().ToArray();
        var result = new List<(int First, int Last)>();
        for (int i = 0; i < periods.Length;)
        {
            int start = i++;
            while (i < periods.Length && periods[i] == periods[i - 1] + 1) i++;
            if (i - start >= 3 && periods[start..i].Any(p => !previous.Contains(p)))
                result.Add((periods[start], periods[i - 1]));
        }
        return result;
    }
}
