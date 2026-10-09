using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record TeacherMovementReview(string TeacherId, string Name,
    ImmutableArray<LessonDelta> Requested, ImmutableArray<LessonDelta> Additional,
    IReadOnlySet<string> PreviouslyChanged, ImmutableArray<ConsecutiveRisk> Risks,
    bool OwnTeacher, bool OriginStillOccupied);

/// <summary>Per-teacher review of one complete proposal, without changing the timetable.</summary>
public static class AutomaticPlanReview
{
    public static ImmutableArray<TeacherMovementReview> Groups(ProjectSession session, ChangePlan plan,
        IReadOnlySet<string> requested, string? consecutiveExemptTeacher = null)
    {
        var state = session.Document.Current;
        var ownTeachers = state.Lessons.Where(x => requested.Contains(x.Id)).Select(x => x.TeacherId).ToHashSet();
        var changed = ChangeHistory.PreviouslyChangedLessons(session.Document);
        var duplicates = state.Teachers.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        var names = state.Teachers.ToDictionary(x => x.Id, x => duplicates.Contains(x.Name) ? $"{x.Name} [{x.Id}]" : x.Name);
        var risks = new ConsecutiveTeaching(state).Evaluate(plan).Where(x => x.TeacherId != consecutiveExemptTeacher).ToArray();
        var after = plan.Changes.ToDictionary(x => x.Before.Id, x => x.After);
        var final = state.Lessons.Select(x => after.GetValueOrDefault(x.Id, x)).ToArray();
        return plan.Changes.GroupBy(x => x.Before.TeacherId ?? "").Select(group =>
        {
            var roots = group.Where(x => requested.Contains(x.Before.Id)).ToImmutableArray();
            return new TeacherMovementReview(group.Key, group.Key.Length == 0 ? "교사 미배정" : names.GetValueOrDefault(group.Key, group.Key), roots,
                group.Where(x => !requested.Contains(x.Before.Id)).ToImmutableArray(),
                group.Where(x => changed.Contains(x.Before.Id)).Select(x => x.Before.Id).ToHashSet(),
                risks.Where(x => x.TeacherId == group.Key).ToImmutableArray(), ownTeachers.Contains(group.Key),
                requested.Count == 1 && roots.Any(root => final.Any(x => x.Id != root.Before.Id && x.TeacherId == group.Key && x.SlotId == root.Before.SlotId)));
        }).OrderByDescending(x => !x.Requested.IsEmpty).ThenBy(x => x.Name).ToImmutableArray();
    }
}

/// <summary>Temporary search constraints. Refusal never sets a persistent lesson lock.</summary>
public sealed class AutomaticSearchRestrictions
{
    private readonly HashSet<string> deniedTeachers = [];
    private readonly HashSet<string> deniedConsecutive = [];
    public IReadOnlySet<string> AvoidConsecutiveTeachers => deniedConsecutive;
    public bool HasRefusals => deniedTeachers.Count > 0 || deniedConsecutive.Count > 0;
    public void Refuse(IEnumerable<string> movement, IEnumerable<string> consecutive)
    { deniedTeachers.UnionWith(movement); deniedConsecutive.UnionWith(consecutive); }
    public IReadOnlySet<string> Immovable(ProjectSession session, IReadOnlySet<string> requested) =>
        session.Document.Current.Lessons.Where(x => deniedTeachers.Contains(x.TeacherId ?? "") && !requested.Contains(x.Id))
            .Select(x => x.Id).ToHashSet();
}

/// <summary>Approval is valid only for this exact plan, session and revision.</summary>
public sealed class AutomaticPlanApproval
{
    private ProjectSession? owner;
    private ChangePlan? approved;
    private Guid revision;
    public void Reset() { owner = null; approved = null; }
    public void Approve(ProjectSession session, ChangePlan plan)
    {
        if (plan.Revision != session.Revision) throw new InvalidOperationException("검토 중 시간표가 바뀌었습니다. 다시 탐색하세요.");
        owner = session; revision = session.Revision; approved = plan;
    }
    public bool IsApproved(ProjectSession session, ChangePlan plan) => ReferenceEquals(owner, session) &&
        ReferenceEquals(approved, plan) && revision == session.Revision && plan.Revision == revision;
}
