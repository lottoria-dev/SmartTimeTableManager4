using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

/// <summary>Column mapping for cycle timetables; not a claimed NEIS-specific schema.</summary>
public sealed class LongTableImporter : IImportAdapter
{
    public string Name => "주기 시간표 행 목록";
    public ImportPreview Parse(RawTable table, ImportProfile? profile = null, bool repeatSingleWeek = false)
    {
        var builder = new ImportBuilder(table, Name);
        if (profile is null) return new(null, [new("PROFILE", Severity.Error, "열 연결이 필요합니다.")]);
        var p = profile;
        var required = new[] { p.ClassColumn, p.DayColumn, p.PeriodColumn, p.SubjectColumn };
        var selected = new[] { p.ClassColumn, p.DayColumn, p.PeriodColumn, p.SubjectColumn, p.TeacherColumn, p.TeacherKeyColumn, p.WeekColumn, p.GradeColumn, p.RoomColumn, p.HomeroomColumn }.Where(x => x >= 0).ToArray();
        if (p.HeaderRow < 0 || p.HeaderRow >= table.Rows.Length || required.Any(x => x < 0) || selected.Any(x => x >= table.Rows[p.HeaderRow].Length) || selected.Distinct().Count() != selected.Length)
            return new(null, [new("PROFILE", Severity.Error, "헤더 행과 필수 열을 서로 다른 열로 연결하세요.")]);
        if (p.HeaderFingerprint.Length > 0 && ImportHelpers.Fingerprint(table, p.HeaderRow) != p.HeaderFingerprint)
            return new(null, [new("PROFILE_CHANGED", Severity.Error, "저장한 양식과 현재 열 제목이 다릅니다. 열을 다시 연결하세요.")]);
        for (int row = p.HeaderRow + 1; row < table.Rows.Length; row++)
        {
            if (table.Rows[row].All(string.IsNullOrWhiteSpace)) continue;
            string Get(int col) => table.Cell(row, col);
            var location = $"{table.Sheet}!행{row + 1}";
            var className = CsvExporter.ReadClass(Get(p.ClassColumn));
            if (p.GradeColumn >= 0) className = Get(p.GradeColumn) + "-" + className;
            var day = ImportHelpers.Day(Get(p.DayColumn));
            var weekText = Get(p.WeekColumn).Replace("주", "", StringComparison.Ordinal);
            var week = p.WeekColumn < 0 ? 1 : int.TryParse(weekText, out var w) ? w : 0;
            var periodText = Get(p.PeriodColumn).Replace("교시", "", StringComparison.Ordinal);
            var valid = int.TryParse(periodText, out var period);
            if (className.Length == 0 || (p.GradeColumn >= 0 && (Get(p.GradeColumn).Length == 0 || Get(p.ClassColumn).Length == 0)) || day == 0 || !valid || period is < 1 or > 20 || week is < 1 or > 4)
            {
                builder.Error("ROW", "학급·주차·요일·교시가 올바르지 않습니다. 날짜를 요일로 자동 변환하지 않습니다.", location);
                continue;
            }
            builder.AddClass(className, Get(p.HomeroomColumn));
            builder.AddSlot(week, day, period);
            builder.Add(className, week, day, period, Get(p.SubjectColumn), Get(p.TeacherColumn), Get(p.TeacherKeyColumn), Get(p.RoomColumn), location);
        }
        return builder.Finish(repeatSingleWeek);
    }
    public static ImportProfile Suggest(RawTable table, int header = 0)
    {
        int Find(params string[] aliases) => header >= table.Rows.Length ? -1 :
            Enumerable.Range(0, table.Rows[header].Length).FirstOrDefault(c => aliases.Contains(table.Cell(header, c).Replace(" ", "", StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase), -1);
        return new(header, Find("학급", "학반", "반", "CLASS_NM"), Find("요일", "Day"), Find("교시", "PERIO", "Period"), Find("과목", "교과", "수업내용", "ITRT_CNTNT", "Subject"),
            Find("교사", "교사명", "담당교사", "Teacher"), Find("교사코드", "TeacherId"), Find("주차", "Week"), Find("학년", "GRADE"), Find("교실", "Room"), Find("담임", "담임교사"), ImportHelpers.Fingerprint(table, header));
    }
}
