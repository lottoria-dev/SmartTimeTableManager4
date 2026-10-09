namespace TimeTableManager.Application;

/// <summary>Consent belongs to one draft/preview and one session revision, never to the saved timetable.</summary>
public sealed class RechangeReview
{
    private ProjectSession? owner;
    private Guid revision;
    private IReadOnlySet<string> changed = new HashSet<string>();
    private readonly HashSet<string> approved = [];
    public void Reset() { owner = null; approved.Clear(); }
    private void Ensure(ProjectSession session)
    {
        if (ReferenceEquals(owner, session) && revision == session.Revision) return;
        owner = session; revision = session.Revision; approved.Clear();
        changed = ChangeHistory.PreviouslyChangedLessons(session.Document);
    }
    public string[] Required(ProjectSession session, IEnumerable<string> ids)
    {
        Ensure(session);
        return ids.Distinct().Where(id => changed.Contains(id) && !approved.Contains(id)).ToArray();
    }
    public void Approve(ProjectSession session, IEnumerable<string> ids)
    {
        Ensure(session); approved.UnionWith(ids.Where(changed.Contains));
    }
}
