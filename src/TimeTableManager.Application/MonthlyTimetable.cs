using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

/// <summary>Continuous dated ledger. MonthlySchedule is retained as a legacy pattern anchor, not an edit boundary.</summary>
public static class MonthlyTimetable
{
    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);
    public static bool InMonth(DateOnly date, DateOnly month) => date.Year == month.Year && date.Month == month.Month;
    public static (DateOnly Start, DateOnly End) Window(DateOnly month)
    {
        month = MonthOf(month);
        var start = ScheduleCalendar.MondayOf(month);
        if (start == month) start = start.AddDays(-7);
        var next = month.AddMonths(1);
        // If next month opens on a weekend, include its first school week as well.
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return (start, ScheduleCalendar.MondayOf(next).AddDays(6));
    }
    public static IEnumerable<TimeSlot> ActiveSlots(ProjectDocument doc)
    {
        var periods = DayTemplatePlanner.PeriodCounts(doc);
        return doc.Current.Slots.Where(x => x.Day <= 5 && (doc.Calendar is not { } dates || x.Period <= periods.GetValueOrDefault(dates.DateOf(x))));
    }
    public static IEnumerable<TimeSlot> WorkingSlots(ProjectDocument doc, bool boundary = true)
    {
        var active = ActiveSlots(doc);
        if (doc.SchemaVersion >= 6) return active;
        if (doc.Monthly is not { } monthly || doc.Calendar is not { } calendar) return active;
        var range = Window(monthly.Month);
        return active.Where(x => boundary
            ? calendar.DateOf(x) >= range.Start && calendar.DateOf(x) <= range.End
            : InMonth(calendar.DateOf(x), monthly.Month));
    }
    public static bool CrossesMonth(ProjectDocument doc, ChangePlan plan)
    {
        if (doc.Calendar is not { } calendar) return false;
        var slots = doc.Current.Slots.ToDictionary(x => x.Id);
        return plan.DayTemplate is null && plan.Changes.Any(x => MonthOf(calendar.DateOf(slots[x.Before.SlotId])) != MonthOf(calendar.DateOf(slots[x.After.SlotId])));
    }
    public static ProjectDocument EnsureMonth(ProjectDocument source, DateOnly requested)
    {
        var month = MonthOf(requested);
        return EnsureCoverage(source, Window(month), month, rolling: false);
    }
    public static ProjectDocument EnsureDates(ProjectDocument source, DateOnly start, DateOnly end, DateOnly? month = null)
    {
        if (start > end || start.Year is < 1901 or > 9998 || end.Year is < 1901 or > 9998)
            throw new InvalidOperationException("조회 날짜는 1901~9998년 범위에서 선택하세요.");
        if (source.History.Any(x => x.DayTemplate is not null) || source.TemplateSlotIds is not null)
            source = SchoolPlanRebaser.Apply(source);
        return EnsureCoverage(source, (ScheduleCalendar.MondayOf(start), ScheduleCalendar.MondayOf(end).AddDays(6)),
            month ?? source.Monthly?.Month ?? MonthOf(start), rolling: true);
    }
    private static ProjectDocument EnsureCoverage(ProjectDocument source, (DateOnly Start, DateOnly End) range, DateOnly month, bool rolling)
    {
        DocumentIntegrity.EnsureValid(source);
        if (month.Year is < 1901 or > 9998) throw new InvalidOperationException("조회 월은 1901~9998년 범위에서 선택하세요.");
        if (source.Baseline.Slots.IsEmpty) throw new InvalidOperationException("기초 시간표의 수업 시간이 비어 있습니다.");
        var calendar = source.Calendar ?? new ScheduleCalendar(ScheduleCalendar.MondayOf(month), range.Start);
        var allocated = (source.TemplateSlotIds ?? []).ToHashSet();
        var originalPattern = DayTemplatePlanner.OriginalPattern(source);
        var monthly = source.Monthly ?? new MonthlySchedule(month, calendar.FirstWeekMonday, originalPattern, Guid.NewGuid());
        var pattern = monthly.Pattern;
        if (pattern.Slots.IsEmpty || pattern.Slots.Any(x => x.Week is < 1 or > 4))
            throw new InvalidOperationException("반복 배치에는 1~4주의 기초 시간표가 필요합니다.");
        int cycle = pattern.Slots.Max(x => x.Week);
        if (Enumerable.Range(1, cycle).Any(w => !pattern.Slots.Any(x => x.Week == w)))
            throw new InvalidOperationException("기초 시간표의 주차가 연속적이지 않습니다.");
        var first = calendar.FirstWeekMonday < range.Start ? calendar.FirstWeekMonday : range.Start;
        int shift = (calendar.FirstWeekMonday.DayNumber - first.DayNumber) / 7;
        var lastExisting = source.Baseline.Slots.Max(x => calendar.DateOf(x));
        var last = lastExisting > range.End ? lastExisting : range.End;
        int weeks = (last.DayNumber - first.DayNumber) / 7 + 1;
        if (weeks > 520) throw new InvalidOperationException("한 작업 파일의 날짜 범위는 최대 520주입니다. 새 기초 시간표로 시작하세요.");
        var slots = source.Baseline.Slots.Select(x => x with { Week = x.Week + shift }).ToList();
        var keys = slots.Select(x => (x.Week, x.Day, x.Period)).ToHashSet();
        var lessons = new List<LessonInstance>(); var blocks = new List<TeacherBlock>();
        var bySlot = pattern.Lessons.ToLookup(x => x.SlotId);
        var blocksBySlot = pattern.Blocks.ToLookup(x => x.SlotId);
        for (int week = 1; week <= weeks; week++)
        {
            int offset = (first.AddDays((week - 1) * 7).DayNumber - monthly.PatternMonday.DayNumber) / 7;
            int patternWeek = (offset % cycle + cycle) % cycle + 1;
            foreach (var slot in pattern.Slots.Where(x => x.Week == patternWeek))
            {
                var actualDate = first.AddDays((week - 1) * 7 + slot.Day - 1);
                if (source.BaseDayPlans?.Any(x => x.Date == actualDate) == true || !keys.Add((week, slot.Day, slot.Period))) continue;
                string id = Guid.NewGuid().ToString("N");
                slots.Add(slot with { Id = id, Week = week });
                lessons.AddRange(bySlot[slot.Id].Select(x => x with
                { Id = Guid.NewGuid().ToString("N"), SlotId = id, Source = x.Source + " (주간 반복)" }));
                blocks.AddRange(blocksBySlot[slot.Id].Select(x => x with { SlotId = id }));
            }
        }
        var allSlots = slots.OrderBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period).ToImmutableArray();
        ScheduleState Extend(ScheduleState state) => state with
        { Slots = allSlots, Lessons = state.Lessons.AddRange(lessons), Blocks = state.Blocks.AddRange(blocks) };
        var next = source with
        {
            SchemaVersion = Math.Max(rolling ? 6 : 3, source.SchemaVersion), Calendar = calendar with { FirstWeekMonday = first },
            OriginalPattern = rolling ? source.OriginalPattern ?? originalPattern : source.OriginalPattern,
            BaseDayPlans = rolling ? source.BaseDayPlans ?? [] : source.BaseDayPlans,
            Monthly = monthly with { Month = month, CoverageId = source.Monthly is null || slots.Count != source.Baseline.Slots.Length || shift != 0 ? Guid.NewGuid() : monthly.CoverageId },
            Baseline = Extend(source.Baseline), Current = Extend(source.Current),
            History = source.History.Select(x => x.DayTemplate is { } day ? x with { DayTemplate = day with
                { AdditionalSlots = day.AdditionalSlots.Select(slot => slot with { Week = slot.Week + shift }).ToImmutableArray() } } : x).ToImmutableArray()
        };
        // New month lessons must follow the same ordering as history replay, including inserted template lessons.
        next = ProjectSession.RebuildExtendedLedger(next);
        DocumentIntegrity.EnsureValid(next);
        return next;
    }
}
