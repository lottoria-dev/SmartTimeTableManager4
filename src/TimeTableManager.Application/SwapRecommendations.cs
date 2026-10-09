using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class SwapRecommendations
{
    public static ImmutableHashSet<string> Find(ProjectSession session, string sourceId,
        IReadOnlySet<string> visibleSlots, CancellationToken token)
    {
        var state = session.Document.Current;
        var source = state.Lessons.Single(x => x.Id == sourceId);
        var school = new SchoolDayRules(session.Document);
        var slots = state.Slots.ToDictionary(x => x.Id);
        bool Editable(string id) => session.Document.Calendar?.CanEdit(slots[id]) != false;
        var result = ImmutableHashSet.CreateBuilder<string>();
        token.ThrowIfCancellationRequested();
        if (source.Locked || school.IsExcluded(source) || !visibleSlots.Contains(source.SlotId) || !Editable(source.SlotId)) return result.ToImmutable();
        foreach (var other in state.Lessons)
        {
            token.ThrowIfCancellationRequested();
            if (other.Id == sourceId || other.TeacherId == source.TeacherId || other.SlotId == source.SlotId || other.Locked || school.IsExcluded(other) || school.IsExcluded(source.ClassId, other.SlotId) || school.IsExcluded(other.ClassId, source.SlotId) || !visibleSlots.Contains(other.SlotId) || !Editable(other.SlotId)) continue;
            // Reuse the same validator as Apply; recommendations never imply extra chain moves.
            if (session.Preview(ChangePlanner.SwapTwo(session, sourceId, other.Id)).IsValid) result.Add(other.Id);
        }
        return result.ToImmutable();
    }
}
