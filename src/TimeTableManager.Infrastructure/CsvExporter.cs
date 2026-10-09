using System.Text.RegularExpressions;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

public static class CsvExporter
{
    private static readonly Regex ClassCode = new(@"\A[0-9]{1,3}-[0-9]{1,3}\z", RegexOptions.CultureInvariant);
    /// <summary>Exchange data only. Does not represent history, baseline, locks or saved-state status.</summary>
    public static void Write(string path, ScheduleState state)
    {
        var slots = state.Slots.ToDictionary(x => x.Id);
        var teachers = state.Teachers.ToDictionary(x => x.Id);
        var classes = state.Classes.ToDictionary(x => x.Id);
        var rows = state.Lessons.OrderBy(x => x.ClassId).ThenBy(x => slots[x.SlotId].Week).ThenBy(x => slots[x.SlotId].Day).ThenBy(x => slots[x.SlotId].Period)
            .Select(lesson =>
            {
                var slot = slots[lesson.SlotId];
                var teacher = lesson.TeacherId is null ? "" : teachers[lesson.TeacherId].Name;
                return new object[] { classes[lesson.ClassId].Name, slot.Week, "월화수목금토일"[slot.Day - 1].ToString(), slot.Period,
                    lesson.Subject, teacher, TeacherIdentifiers.Display(lesson.TeacherId), lesson.Room ?? "" };
            });
        TabularExport.Write(path, "배정 결과", ["학급", "주차", "요일", "교시", "과목", "교사", "교사코드", "교실"], rows);
    }
    // CSV has no types. Only a validated class code becomes a constant text formula.
    // Arbitrary input follows Escape's protection. XLSX uses literal text, with no formulas.
    public static string EscapeClass(string text) => ClassCode.IsMatch(text)
        ? "\"=\"\"" + text + "\"\"\"" : Escape(text);
    public static string ReadClass(string text) => text.StartsWith("=\"", StringComparison.Ordinal) && text.EndsWith('"') &&
        text.Length > 3 && ClassCode.IsMatch(text[2..^1]) ? text[2..^1] : text;
    public static string Escape(string text)
    {
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@') || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
