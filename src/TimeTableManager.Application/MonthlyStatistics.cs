using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record MonthlyTeacherTotal(string TeacherId, string Teacher, int Planned, int MoveOut, int MoveIn,
    int CoverOut, int CoverIn, int Assigned, int TemplateOut = 0, int TemplateIn = 0)
{
    public string TeacherCode => TeacherIdentifiers.Display(TeacherId);
    public int Difference => Assigned - Planned;
    public bool Balanced => Planned - MoveOut + MoveIn - CoverOut + CoverIn - TemplateOut + TemplateIn == Assigned;
}
public sealed record MonthlyChange(string LessonId, string Class, string Subject, string OriginalTeacher, string Teacher,
    string Before, string After, string Attribution, bool CrossMonth)
{
    public string Boundary => CrossMonth ? "월 경계" : "";
}
public sealed record MonthlyReport(DateOnly Month, IReadOnlyList<MonthlyTeacherTotal> Teachers, IReadOnlyList<MonthlyChange> Changes);

/// <summary>Final allocation, not operation counts. Move legs use their own dates; teacher replacement uses the final lesson date.</summary>
public static class MonthlyStatistics
{
    public static MonthlyReport Create(ProjectDocument document, DateOnly requested)
    {
        var calendar = document.Calendar ?? throw new InvalidOperationException("먼저 날짜를 설정하세요.");
        var month = MonthlyTimetable.MonthOf(requested);
        var original = document.Baseline.Lessons.ToDictionary(x => x.Id);
        var currentLessons = document.Current.Lessons.ToDictionary(x => x.Id);
        var slots = document.Current.Slots.ToDictionary(x => x.Id);
        var classes = document.Current.Classes.ToDictionary(x => x.Id, x => x.Name);
        var names = document.Current.Teachers.ToDictionary(x => x.Id, x => x.Name);
        names[""] = "미배정";
        var counts = names.ToDictionary(x => x.Key, _ => new int[8]);
        var details = new List<MonthlyChange>();
        var pattern = DayTemplatePlanner.OriginalPattern(document).Lessons.ToDictionary(x => x.Id);
        string TemplateTeacher(LessonInstance lesson) => DayTemplatePlanner.PatternId(lesson) is { } id && pattern.TryGetValue(id, out var source)
            ? source.TeacherId ?? "" : lesson.TeacherId ?? "";
        foreach (var lesson in original.Values)
            if (MonthlyTimetable.InMonth(calendar.DateOf(slots[lesson.SlotId]), month)) counts[lesson.TeacherId ?? ""][0]++;
        foreach (var lesson in currentLessons.Values)
            if (MonthlyTimetable.InMonth(calendar.DateOf(slots[lesson.SlotId]), month)) counts[lesson.TeacherId ?? ""][5]++;
        foreach (var id in original.Keys.Concat(currentLessons.Keys).Distinct())
        {
            original.TryGetValue(id, out var before); currentLessons.TryGetValue(id, out var current);
            if (before is null || current is null)
            {
                var lesson = current ?? before!; var date = calendar.DateOf(slots[lesson.SlotId]);
                if (!MonthlyTimetable.InMonth(date, month)) continue;
                var assignedTeacher = lesson.TeacherId ?? "";
                var templateTeacher = current is null ? assignedTeacher : TemplateTeacher(lesson);
                counts[templateTeacher][current is null ? 6 : 7]++;
                if (current is not null && templateTeacher != assignedTeacher)
                { counts[templateTeacher][3]++; counts[assignedTeacher][4]++; }
                var position = $"{date:yyyy-MM-dd} {slots[lesson.SlotId].Period}교시";
                details.Add(new(id, classes[lesson.ClassId], lesson.Subject, before is null ? "—" : names[lesson.TeacherId ?? ""],
                    current is null ? "—" : names[lesson.TeacherId ?? ""], before is null ? "추가 수업" : position,
                    current is null ? "요일 변경으로 제외" : position, "기초 요일 적용", false));
                continue;
            }
            var from = calendar.DateOf(slots[before.SlotId]); var to = calendar.DateOf(slots[current.SlotId]);
            bool fromMonth = MonthlyTimetable.InMonth(from, month), toMonth = MonthlyTimetable.InMonth(to, month);
            var oldTeacher = before.TeacherId ?? ""; var newTeacher = current.TeacherId ?? "";
            bool appliedTemplate = document.SchemaVersion < 5 && DayTemplatePlanner.PatternId(current) is not null;
            string assignedByTemplate = appliedTemplate ? TemplateTeacher(current) : oldTeacher;
            bool moved = before.SlotId != current.SlotId, covered = assignedByTemplate != newTeacher;
            bool template = appliedTemplate && (before.Subject != current.Subject || before.Room != current.Room || oldTeacher != assignedByTemplate);
            var attribution = new List<string>();
            if (moved)
            {
                if (fromMonth) counts[oldTeacher][1]++;
                if (toMonth) counts[oldTeacher][2]++;
                attribution.Add($"이동 출발 {from:yyyy-MM} / 도착 {to:yyyy-MM}");
            }
            if (template)
            {
                if (toMonth) { counts[oldTeacher][6]++; counts[assignedByTemplate][7]++; }
                attribution.Add("기초 요일 적용");
            }
            if (covered)
            {
                if (toMonth) { counts[assignedByTemplate][3]++; counts[newTeacher][4]++; }
                attribution.Add($"담당 교체 결강·보강 {to:yyyy-MM}");
            }
            if ((moved || covered || template) && (fromMonth || toMonth))
                details.Add(new(current.Id, classes[current.ClassId], current.Subject, names[oldTeacher], names[newTeacher],
                    $"{from:yyyy-MM-dd} {slots[before.SlotId].Period}교시", $"{to:yyyy-MM-dd} {slots[current.SlotId].Period}교시",
                    string.Join(" · ", attribution), MonthlyTimetable.MonthOf(from) != MonthlyTimetable.MonthOf(to)));
        }
        var totals = counts.Where(x => x.Key != "" || x.Value.Any(v => v != 0))
            .Select(x => new MonthlyTeacherTotal(x.Key, names[x.Key], x.Value[0], x.Value[1], x.Value[2], x.Value[3], x.Value[4], x.Value[5], x.Value[6], x.Value[7]))
            .OrderBy(x => x.Teacher).ThenBy(x => x.TeacherId).ToArray();
        if (totals.Any(x => !x.Balanced)) throw new InvalidDataException("월별 수업 수 대사가 맞지 않습니다.");
        return new(month, totals, details.OrderBy(x => x.After).ThenBy(x => x.Class).ToArray());
    }
}
