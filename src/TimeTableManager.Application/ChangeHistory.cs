using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record VisibleOperation(int Number, Operation Operation, bool IsApplied);

/// <summary>Lesson changes for display. Lock operations remain intact for persistence and undo.</summary>
public static class ChangeHistory
{
    public static IReadOnlySet<string> PreviouslyChangedLessons(ProjectDocument document) => document.History
        .Take(document.HistoryCursor).SelectMany(x => x.Changes).Where(IsLessonChange).Select(x => x.Before.Id)
        .Concat(NetChanges(document).Select(x => x.Before.Id)).ToHashSet();

    public static bool IsLessonChange(LessonDelta delta) =>
        delta.Added || delta.Removed || delta.Before.SlotId != delta.After.SlotId || delta.Before.TeacherId != delta.After.TeacherId ||
        delta.Before.Subject != delta.After.Subject || delta.Before.Room != delta.After.Room;

    public static IReadOnlyList<VisibleOperation> Operations(ProjectDocument document) => document.History
        .Select((operation, index) => (operation, index))
        .Where(x => (x.operation.Kind is ChangeKind.Move or ChangeKind.AssignTeacher) && x.operation.Changes.Any(IsLessonChange))
        .Select((x, number) => new VisibleOperation(number + 1, x.operation, x.index < document.HistoryCursor)).ToArray();

    public static ImmutableArray<LessonDelta> NetChanges(ProjectDocument document)
    {
        return DayTemplatePlanner.Difference(document.Baseline.Lessons, document.Current.Lessons).Where(IsLessonChange).ToImmutableArray();
    }
    public static IReadOnlySet<string> ChangedRows(ProjectDocument document, IReadOnlySet<string> visibleSlots, bool teachers) =>
        NetChanges(document).SelectMany(d => new[] { d.Before, d.After }).Where(x => visibleSlots.Contains(x.SlotId))
            .Select(x => teachers ? x.TeacherId ?? "unassigned:" + x.ClassId : x.ClassId).ToHashSet();

}
