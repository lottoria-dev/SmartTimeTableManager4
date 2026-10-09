using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

public sealed record ImportProfile(int HeaderRow, int ClassColumn, int DayColumn, int PeriodColumn, int SubjectColumn,
    int TeacherColumn = -1, int TeacherKeyColumn = -1, int WeekColumn = -1, int GradeColumn = -1,
    int RoomColumn = -1, int HomeroomColumn = -1, string HeaderFingerprint = "");
public sealed record ImportPreview(ProjectDocument? Document, ImmutableArray<Issue> Issues)
{
    public bool CanImport => Document is not null && !Issues.Any(x => x.Severity == Severity.Error);
}
public interface IImportAdapter
{
    string Name { get; }
    ImportPreview Parse(RawTable table, ImportProfile? profile = null, bool repeatSingleWeek = false);
}

internal sealed class ImportBuilder(RawTable table, string adapter)
{
    public List<Issue> Issues { get; } = [];
    private readonly Dictionary<string, Teacher> teachers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassGroup> classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSlot> slots = new(StringComparer.Ordinal);
    private readonly List<LessonInstance> lessons = [];
    public void Error(string code, string message, string location) => Issues.Add(new(code, Severity.Error, message, location));
    public string? AddTeacher(string name, string key = "")
    {
        if (name.Length == 0 && key.Length == 0) return null;
        var id = (key.Length > 0 ? "code:" + key : "name:" + name);
        var display = name.Length > 0 ? name : key;
        if (teachers.TryGetValue(id, out var existing) && existing.Name != display)
            Error("TEACHER_CODE", "같은 교사 코드에 서로 다른 이름이 있습니다.", key);
        else teachers[id] = new(id, display);
        return id;
    }
    public void AddClass(string name, string homeroom)
    {
        var teacher = AddTeacher(homeroom);
        if (classes.TryGetValue(name, out var existing))
        {
            if (existing.HomeroomTeacherId is not null && teacher is not null && existing.HomeroomTeacherId != teacher)
                Error("HOMEROOM", "같은 학급의 담임 정보가 다릅니다.", name);
            if (existing.HomeroomTeacherId is null && teacher is not null) classes[name] = existing with { HomeroomTeacherId = teacher };
        }
        else classes[name] = new(name, name, teacher);
    }
    public void AddSlot(int week, int day, int period)
    {
        var id = $"w{week}-d{day}-p{period}";
        slots.TryAdd(id, new(id, week, day, period));
    }
    public void Add(string className, int week, int day, int period, string subject, string teacher, string teacherKey, string room, string location)
    {
        if (subject.Length == 0)
        {
            if (teacher.Length > 0 || teacherKey.Length > 0) Error("MISSING_SUBJECT", "교사는 있으나 과목이 없습니다.", location);
            return;
        }
        AddSlot(week, day, period);
        var id = Guid.NewGuid().ToString("N");
        lessons.Add(new(id, className, subject, AddTeacher(teacher, teacherKey), $"w{week}-d{day}-p{period}", Room: room.Length > 0 ? room : null, Source: location));
    }
    public ImportPreview Finish(bool repeat)
    {
        if (lessons.Count == 0) Error("EMPTY_IMPORT", "가져올 수업이 없습니다. 표준 시간표의 학급·과목·교사 칸을 확인하세요.", "");
        if (repeat)
        {
            if (slots.Values.Any(x => x.Week != 1)) Error("REPEAT_SCOPE", "2주 반복 생성은 원본이 1주 자료일 때만 선택하세요.", "");
            else
            {
                foreach (var slot in slots.Values.ToArray()) AddSlot(2, slot.Day, slot.Period);
                foreach (var lesson in lessons.ToArray()) lessons.Add(lesson with { Id = Guid.NewGuid().ToString("N"), SlotId = lesson.SlotId.Replace("w1-", "w2-", StringComparison.Ordinal), Source = lesson.Source + " (명시적 2주 반복)" });
            }
        }
        var state = new ScheduleState(teachers.Values.ToImmutableArray(), classes.Values.ToImmutableArray(), slots.Values.OrderBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period).ToImmutableArray(), lessons.ToImmutableArray(), []);
        Issues.AddRange(ConstraintValidator.Validate(state).Issues);
        if (teachers.Keys.Any(x => x.StartsWith("name:", StringComparison.Ordinal)))
            Issues.Add(new("NAME_ID", Severity.Warning, "코드가 없는 교사는 같은 이름을 같은 교사로 연결했습니다. 동명이인이 있으면 교사 코드 열이 있는 행 목록을 사용하세요."));
        var source = new ImportSource(table.FileName, table.Sha256, adapter, DateTimeOffset.UtcNow, table.Encoding, table.Sheet);
        return new(Issues.Any(x => x.Severity == Severity.Error) ? null : ProjectDocument.Create(System.IO.Path.GetFileNameWithoutExtension(table.FileName), state, source), Issues.ToImmutableArray());
    }
}

public static class ImportHelpers
{
    public static int Day(string value)
    {
        value = value.Trim().Replace("요일", "", StringComparison.Ordinal);
        if (int.TryParse(value, out var number) && number is >= 1 and <= 7) return number;
        return value.Length == 1 ? "월화수목금토일".IndexOf(value[0]) + 1 : 0;
    }
    public static string Fingerprint(RawTable table, int row) => row < 0 || row >= table.Rows.Length ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", table.Rows[row]))));
}
