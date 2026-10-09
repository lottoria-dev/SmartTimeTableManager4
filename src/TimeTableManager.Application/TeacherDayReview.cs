using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record TeacherDayReviewItem(ConsecutiveRisk Risk, IReadOnlyList<LessonInstance> Lessons);

/// <summary>Review the final plan against the original state, including unchanged lessons in each new run.</summary>
public static class TeacherDayReview
{
    public static IReadOnlyList<TeacherDayReviewItem> Create(ScheduleState before, ChangePlan plan)
    {
        var slots = before.Slots.ToDictionary(x => x.Id);
        var replacements = plan.Changes.ToDictionary(x => x.Before.Id, x => x.After);
        var after = before.Lessons.Select(x => replacements.GetValueOrDefault(x.Id, x)).ToArray();
        return new ConsecutiveTeaching(before).Evaluate(plan)
            .OrderByDescending(x => x.Length).ThenBy(x => x.Day.Week).ThenBy(x => x.Day.Day).ThenBy(x => x.TeacherId)
            .Select(risk => new TeacherDayReviewItem(risk, after.Where(x => x.TeacherId == risk.TeacherId &&
                slots[x.SlotId].Week == risk.Day.Week && slots[x.SlotId].Day == risk.Day.Day &&
                slots[x.SlotId].Period >= risk.FirstPeriod && slots[x.SlotId].Period <= risk.LastPeriod)
                .OrderBy(x => slots[x.SlotId].Period).ToArray())).ToArray();
    }
}
