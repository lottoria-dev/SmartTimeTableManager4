using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record CoverCandidate(string TeacherId, string Name, bool ThreeConsecutive, int Longest, int DailyLessons)
{
    public string Label => $"{(ThreeConsecutive ? "* " : "")}{Name} · 배치 후 {DailyLessons}시간 · 최대 {Longest}연강";
}
public static class TeachingAdvice
{
    public static int LongestRun(IEnumerable<LessonInstance> lessons, IReadOnlyDictionary<string,TimeSlot> slots, string teacher, TimeSlot day)
    {
        var periods = lessons.Where(x => x.TeacherId == teacher).Select(x => slots[x.SlotId])
            .Where(x => x.Week == day.Week && x.Day == day.Day).Select(x => x.Period);
        return LongestPeriods(periods);
    }
    public static int LongestPeriods(IEnumerable<int> periods)
    {
        int last = -2, run = 0, maximum = 0;
        foreach (var period in periods.Distinct().Order()) { run = period == last + 1 ? run + 1 : 1; maximum = Math.Max(maximum,run); last = period; }
        return maximum;
    }
    public static IReadOnlyList<CoverCandidate> CoverCandidates(ProjectSession session,string lessonId)
    {
        var s=session.Document.Current; var lesson=s.Lessons.Single(x=>x.Id==lessonId);
        var slots=s.Slots.ToDictionary(x=>x.Id);var slot=slots[lesson.SlotId];
        var duplicateNames=s.Teachers.GroupBy(x=>x.Name).Where(x=>x.Count()>1).Select(x=>x.Key).ToHashSet();
        var result=new List<CoverCandidate>();
        foreach(var teacher in s.Teachers.Where(x=>x.Id!=lesson.TeacherId))
        {
            var plan=ChangePlanner.AssignTeacher(session,lessonId,teacher.Id);
            if (!session.Preview(plan).IsValid) continue;
            var after=s.Lessons.Where(x=>x.Id!=lessonId).Append(lesson with{TeacherId=teacher.Id}).ToArray();
            var run=LongestRun(after,slots,teacher.Id,slot);
            var count=after.Count(x=>x.TeacherId==teacher.Id && slots[x.SlotId].Week==slot.Week && slots[x.SlotId].Day==slot.Day);
            result.Add(new(teacher.Id,duplicateNames.Contains(teacher.Name)?$"{teacher.Name} [{teacher.Id}]":teacher.Name,run>=3,run,count));
        }
        return result.OrderBy(x=>x.ThreeConsecutive).ThenBy(x=>x.DailyLessons).ThenBy(x=>x.Name).ToArray();
    }
}
