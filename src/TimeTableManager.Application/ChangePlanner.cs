using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record Candidate(TimeSlot Slot, ChangePlan Plan, ValidationResult Validation);

public static class ChangePlanner
{
    public static ChangePlan Move(ProjectSession session, string lessonId, string targetSlotId)
    {
        var state = session.Document.Current;
        var lesson = state.Lessons.Single(x => x.Id == lessonId);
        var target = state.Slots.Single(x => x.Id == targetSlotId);
        var other = state.Lessons.SingleOrDefault(x => x.ClassId == lesson.ClassId && x.SlotId == targetSlotId);
        ImmutableArray<LessonDelta> deltas = other is null
            ? [new(lesson, lesson with { SlotId = targetSlotId })]
            : [new(lesson, lesson with { SlotId = targetSlotId }), new(other, other with { SlotId = lesson.SlotId })];
        if (lesson.SlotId == targetSlotId) deltas = [];
        return new(session.Revision, ChangeKind.Move, $"{lesson.Subject} → {target.Label}{(other is null ? " 이동" : " 맞교환")}", deltas);
    }
    public static ChangePlan SwapTwo(ProjectSession session, string firstId, string secondId)
    {
        var a = session.Document.Current.Lessons.Single(x => x.Id == firstId);
        var b = session.Document.Current.Lessons.Single(x => x.Id == secondId);
        return new(session.Revision, ChangeKind.Move, "1대1 맞교환", firstId == secondId || a.SlotId == b.SlotId || a.TeacherId == b.TeacherId ? [] :
            [new(a, a with { SlotId = b.SlotId }), new(b, b with { SlotId = a.SlotId })]);
    }
    public static ChangePlan AssignTeacher(ProjectSession session, string lessonId, string teacherId)
    {
        var lesson = session.Document.Current.Lessons.Single(x => x.Id == lessonId);
        return new(session.Revision, ChangeKind.AssignTeacher, $"{lesson.Subject} 담당 교사 변경", [new(lesson, lesson with { TeacherId = teacherId })]);
    }
    public static ChangePlan ToggleLock(ProjectSession session, string lessonId)
    {
        var lesson = session.Document.Current.Lessons.Single(x => x.Id == lessonId);
        return new(session.Revision, ChangeKind.ToggleLock, lesson.Locked ? "수업 잠금 해제" : "수업 잠금", [new(lesson, lesson with { Locked = !lesson.Locked })]);
    }
    public static ChangePlan SetLocks(ProjectSession session, IEnumerable<string> lessonIds, bool locked)
    {
        var lessons = session.Document.Current.Lessons.ToDictionary(x => x.Id);
        // Resolve every ID first; an unknown ID must not silently produce a partial batch.
        var changes = lessonIds.Distinct().Select(id => lessons[id]).Where(x => x.Locked != locked)
            .Select(x => new LessonDelta(x, x with { Locked = locked })).ToImmutableArray();
        return new(session.Revision, ChangeKind.ToggleLock, $"수업 {changes.Length}개 일괄 {(locked ? "잠금" : "잠금 해제")}", changes);
    }
    public static ChangePlan SetTeacherLocks(ProjectSession session, string teacherId, bool locked)
    {
        _ = session.Document.Current.Teachers.Single(x => x.Id == teacherId);
        return SetLocks(session, session.Document.Current.Lessons.Where(x => x.TeacherId == teacherId).Select(x => x.Id), locked);
    }
    public static ImmutableArray<Candidate> DirectCandidates(ProjectSession session, string lessonId, CancellationToken cancellationToken = default)
    {
        var result = ImmutableArray.CreateBuilder<Candidate>();
        foreach (var slot in session.Document.Current.Slots.OrderBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = Move(session, lessonId, slot.Id);
            var valid = session.Preview(plan);
            if (valid.IsValid) result.Add(new(slot, plan, valid));
        }
        return result.ToImmutable();
    }
}
