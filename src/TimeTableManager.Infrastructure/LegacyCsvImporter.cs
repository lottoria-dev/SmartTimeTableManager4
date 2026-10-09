using System.Text.RegularExpressions;
using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

/// <summary>Standard combined-cell and paired-row grids, shared by XLSX and legacy CSV inputs.</summary>
public sealed partial class LegacyCsvImporter : IImportAdapter
{
    public string Name => "표준 전체 시간표 (동일셀·분리셀)";
    [GeneratedRegex(@"^(?:(\d)주\s*)?([월화수목금토일])(?:요일)?(?:\s*(\d+)(?:교시)?)?$")]
    private static partial Regex DayHeader();
    [GeneratedRegex(@"^(\d+)[\-\.\s]+(\d+)$")]
    private static partial Regex ClassLabel();
    public ImportPreview Parse(RawTable table, ImportProfile? profile = null, bool repeatSingleWeek = false)
    {
        var builder = new ImportBuilder(table, Name);
        if (table.Rows.Any(r => r.Any(c => c.TrimStart().StartsWith("#METADATA_", StringComparison.Ordinal))))
            return new(null, [new("LEGACY_METADATA", Severity.Error, "변경 이력·잠금·제외 메타데이터가 있는 구버전 파일은 가져올 수 없습니다. 최초 기초 CSV를 사용하세요. 기존 파일은 변경되지 않습니다.")]);
        var map = new Dictionary<int, (int Week, int Day, int Period)>();
        int header = -1, dataStart = -1;
        for (int r = 0; r < Math.Min(10, table.Rows.Length); r++)
        {
            var direct = new Dictionary<int, (int, int, int)>();
            for (int c = 0; c < table.Rows[r].Length; c++)
            {
                var m = DayHeader().Match(table.Cell(r, c));
                if (m.Success && m.Groups[3].Success) direct[c] = (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1, ImportHelpers.Day(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            }
            if (direct.Count > 0) { map = direct; header = r; dataStart = r + 1; break; }
            if (r + 1 >= table.Rows.Length) continue;
            int day = 0, week = 1;
            for (int c = 0; c < Math.Max(table.Rows[r].Length, table.Rows[r + 1].Length); c++)
            {
                var m = DayHeader().Match(table.Cell(r, c));
                if (m.Success) { day = ImportHelpers.Day(m.Groups[2].Value); week = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1; }
                else if (table.Cell(r, c).Length > 0) day = 0;
                if (day > 0 && int.TryParse(table.Cell(r + 1, c), out var period)) direct[c] = (week, day, period);
            }
            if (direct.Count > 0) { map = direct; header = r; dataStart = r + 2; break; }
        }
        if (header < 0) return new(null, [new("HEADERS", Severity.Error, "요일·교시 헤더를 찾지 못했습니다. 표준 양식의 상단 요일·교시 행을 유지하세요.")]);
        var homeroom = Enumerable.Range(0, table.Rows[header].Length).FirstOrDefault(c => table.Cell(header, c).Contains("담임", StringComparison.Ordinal), -1);
        var splitClass = table.Rows[header].Take(2).Select(x => x.Trim()).SequenceEqual(new[] { "학년", "반" });
        string lastGrade = "";
        string? ClassAt(int row, bool remember)
        {
            var first = table.Cell(row, 0);
            var second = table.Cell(row, 1);
            var match = ClassLabel().Match(first);
            if (match.Success) return $"{int.Parse(match.Groups[1].Value)}-{int.Parse(match.Groups[2].Value)}";
            if (splitClass && int.TryParse(second, out var cls) && cls > 0)
            {
                if (int.TryParse(first, out var grade) && grade > 0) { if (remember) lastGrade = grade.ToString(); return $"{grade}-{cls}"; }
                if (first.Length == 0 && lastGrade.Length > 0) return $"{lastGrade}-{cls}";
            }
            return null;
        }
        foreach (var (_, time) in map) builder.AddSlot(time.Week, time.Day, time.Period);
        for (int row = dataStart; row < table.Rows.Length; row++)
        {
            if (table.Rows[row].All(string.IsNullOrWhiteSpace)) continue;
            var className = ClassAt(row, true);
            if (className is null)
            {
                builder.Error("CLASS_ROW", "학급을 인식하지 못했습니다. 수업 행을 건너뛰지 않습니다.", $"{table.Sheet}!행{row + 1}");
                continue;
            }
            builder.AddClass(className, table.Cell(row, homeroom));
            bool hasInline = map.Keys.Any(c => table.Cell(row, c).Contains('\n'));
            bool teacherRow = row + 1 < table.Rows.Length && ClassAt(row + 1, false) is null && table.Cell(row + 1, 0).Length == 0 &&
                (!splitClass || table.Cell(row + 1, 1).Length == 0) && map.Keys.Any(c => table.Cell(row + 1, c).Length > 0) && !hasInline;
            foreach (var (col, time) in map)
            {
                var cell = table.Cell(row, col);
                string subject, teacher;
                if (teacherRow) { subject = cell; teacher = table.Cell(row + 1, col); }
                else
                {
                    var pieces = cell.Replace("\r", "", StringComparison.Ordinal).Split('\n');
                    if (pieces.Length > 2 && pieces.Skip(2).Any(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        builder.Error("MULTI_LINE", "한 셀에 여러 담당·과목이 있어 자동 해석할 수 없습니다.", $"{table.Sheet}!행{row + 1}열{col + 1}");
                        continue;
                    }
                    subject = pieces[0].Trim();
                    teacher = pieces.Length > 1 ? pieces[1].Trim() : "";
                    // Ambiguous spaces are not used to invent a teacher name.
                }
                builder.Add(className, time.Week, time.Day, time.Period, subject, teacher, "", "", $"{table.Sheet}!행{row + 1}열{col + 1}");
            }
            if (teacherRow) row++;
        }
        return builder.Finish(repeatSingleWeek);
    }
}
