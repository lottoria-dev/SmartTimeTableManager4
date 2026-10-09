using System.Collections.Immutable;

namespace TimeTableManager.Domain;

public static class ConstraintValidator
{
    public static ValidationResult Validate(ScheduleState state)
    {
        var issues = ImmutableArray.CreateBuilder<Issue>();
        void Error(string code, string message, string location = "") => issues.Add(new(code, Severity.Error, message, location));
        if (state.Teachers.IsDefault || state.Classes.IsDefault || state.Slots.IsDefault || state.Lessons.IsDefault || state.Blocks.IsDefault)
            return ValidationResult.Error("SCHEMA", "필수 목록이 없습니다.");
        void Unique(IEnumerable<string> keys, string type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) Error("DUPLICATE_ID", $"{type} ID가 비었거나 중복되었습니다.");
        }
        Unique(state.Teachers.Select(x => x.Id), "교사");
        Unique(state.Classes.Select(x => x.Id), "학급");
        Unique(state.Slots.Select(x => x.Id), "시간");
        Unique(state.Lessons.Select(x => x.Id), "수업");
        var teachers = state.Teachers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var classes = state.Classes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var slots = state.Slots.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (state.Teachers.Any(x => string.IsNullOrWhiteSpace(x.Name)) || state.Classes.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            Error("NAME", "교사·학급 표시 이름을 확인하세요.");
        foreach (var group in state.Classes)
            if (group.HomeroomTeacherId is not null && !teachers.Contains(group.HomeroomTeacherId))
                Error("HOMEROOM", "담임 교사 참조가 없습니다.", group.Name);
        var slotKeys = new HashSet<(int, int, int)>();
        foreach (var slot in state.Slots)
            if (slot.Week is < 1 or > 520 || slot.Day is < 1 or > 7 || slot.Period is < 1 or > 20 || !slotKeys.Add((slot.Week, slot.Day, slot.Period)))
                Error("SLOT", "주차(1~520)·요일(1~7)·교시(1~20) 또는 중복 시간을 확인하세요.", slot.Id);
        var occupiedClasses = new HashSet<(string, string)>();
        var occupiedTeachers = new HashSet<(string, string)>();
        var occupiedRooms = new HashSet<(string, string)>();
        var blocks = state.Blocks.Select(x => (x.TeacherId, x.SlotId)).ToHashSet();
        foreach (var block in state.Blocks)
            if (!teachers.Contains(block.TeacherId) || !slots.Contains(block.SlotId)) Error("BLOCK_REF", "부재의 교사·시간 참조가 없습니다.");
        foreach (var lesson in state.Lessons)
        {
            if (!classes.Contains(lesson.ClassId) || !slots.Contains(lesson.SlotId) || string.IsNullOrWhiteSpace(lesson.Subject))
                Error("LESSON_REF", "수업의 학급·시간·과목을 확인하세요.", lesson.Source);
            if (!occupiedClasses.Add((lesson.ClassId, lesson.SlotId))) Error("CLASS_COLLISION", "같은 학급·시간에 수업이 중복되었습니다.", lesson.Source);
            if (lesson.TeacherId is { } teacher)
            {
                if (!teachers.Contains(teacher)) Error("TEACHER_REF", "존재하지 않는 교사입니다.", lesson.Source);
                if (!occupiedTeachers.Add((teacher, lesson.SlotId))) Error("TEACHER_COLLISION", "같은 교사가 같은 시간에 중복 배정되었습니다.", lesson.Source);
                if (blocks.Contains((teacher, lesson.SlotId))) Error("TEACHER_UNAVAILABLE", "부재 시간에 교사가 배정되었습니다.", lesson.Source);
            }
            else issues.Add(new("UNASSIGNED", Severity.Warning, "교사 미배정 수업입니다. 담당을 연결하세요.", lesson.Source));
            if (!string.IsNullOrWhiteSpace(lesson.Room) && !occupiedRooms.Add((lesson.Room, lesson.SlotId)))
                Error("ROOM_COLLISION", "같은 교실·시간에 수업이 중복되었습니다.", lesson.Source);
        }
        return new(issues.ToImmutable());
    }
}
