using System.Collections.Immutable;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public sealed record PlacementAdvice(bool Allowed, bool Recommended, bool ThreeConsecutive, int Displaced, string Message);

/// <summary>Incomplete placements stay only in this draft. The session receives a complete validated plan.</summary>
public sealed class ManualChainDraft
{
    private readonly ProjectSession session;
    private readonly Guid revision;
    private readonly ScheduleState original;
    private readonly SchoolDayRules school;
    private readonly Dictionary<string, LessonInstance> lessons;
    private readonly Dictionary<string, TimeSlot> slots;
    private ImmutableDictionary<string, string> placements;
    private ImmutableDictionary<string, string> heldFrom = ImmutableDictionary<string, string>.Empty;
    private readonly Stack<(ImmutableDictionary<string, string> Placements, ImmutableDictionary<string, string> HeldFrom, string? Active, string? Anchor)> undo = new();
    public string? ActiveId { get; private set; }
    public string? DateAnchorSlotId { get; private set; }
    public SwapDirection Direction { get; private set; }
    private bool AllowsDate(string slot, SwapDirection direction) => DateAnchorSlotId is not { } anchor ||
        session.Document.Calendar is not { } calendar || CalendarSwapSearch.Allows(calendar, slots[anchor], slots[slot], direction);
    public bool AllowsDate(string slot) => AllowsDate(slot, Direction);
    public string? SetDirection(SwapDirection direction)
    {
        if (session.Revision != revision) return "확정 시간표가 바뀌었습니다. 연쇄 작업을 취소하세요.";
        var used = PlacedChanges.SelectMany(x => new[] { x.Before.SlotId, x.After.SlotId })
            .Concat(Held.Select(x => x.SlotId)).Concat(heldFrom.Values);
        if (used.Any(x => !AllowsDate(x, direction))) return "이미 배치하거나 꺼낸 수업이 이 날짜 범위를 사용하고 있습니다. 한 단계 뒤로 가거나 연쇄 작업을 취소한 뒤 옵션을 끄세요.";
        Direction = direction; adviceCache.Clear(); return null;
    }
    public string? ActiveOriginSlotId => ActiveId is { } id ? heldFrom.GetValueOrDefault(id) : null;
    public void Deselect() { if (Held.IsEmpty) ActiveId = null; }
    private readonly List<string> participatingTeachers = [];
    public IReadOnlyList<string> ParticipatingTeachers => participatingTeachers.AsReadOnly();
    private void RecordTeacher(LessonInstance lesson)
    {
        if (lesson.TeacherId is { } id && !participatingTeachers.Contains(id)) participatingTeachers.Add(id);
    }
    private ImmutableDictionary<string, string>? indexedPlacements;
    private ScheduleState cachedDisplay = ScheduleState.Empty;
    private ImmutableArray<LessonInstance> cachedHeld = [];
    private ImmutableArray<LessonDelta> cachedChanges = [];
    private ILookup<string, LessonInstance> bySlot = null!;
    private ILookup<(string Teacher, int Week, int Day), int> teacherPeriods = null!;
    private HashSet<string> unknownSlots = [];
    private readonly HashSet<(string Teacher, string Slot)> blocks;
    private readonly Dictionary<string, PlacementAdvice> adviceCache = [];
    private string? adviceActive;
    public bool CanUndo => undo.Count > 0;
    public ImmutableArray<LessonInstance> Held { get { EnsureIndex(); return cachedHeld; } }
    public ScheduleState DisplayState { get { EnsureIndex(); return cachedDisplay; } }
    public ImmutableArray<LessonDelta> PlacedChanges { get { EnsureIndex(); return cachedChanges; } }
    private void EnsureIndex()
    {
        if (ReferenceEquals(indexedPlacements, placements)) return;
        cachedDisplay = original with { Lessons = original.Lessons.Where(x => placements.ContainsKey(x.Id))
            .Select(x => x.SlotId == placements[x.Id] ? x : x with { SlotId = placements[x.Id] }).ToImmutableArray() };
        cachedHeld = original.Lessons.Where(x => !placements.ContainsKey(x.Id)).ToImmutableArray();
        cachedChanges = cachedDisplay.Lessons.Where(x => x.SlotId != lessons[x.Id].SlotId)
            .Select(x => new LessonDelta(lessons[x.Id], x)).ToImmutableArray();
        bySlot = cachedDisplay.Lessons.ToLookup(x => x.SlotId);
        teacherPeriods = cachedDisplay.Lessons.Where(x => x.TeacherId is not null)
            .ToLookup(x => (x.TeacherId!, slots[x.SlotId].Week, slots[x.SlotId].Day), x => slots[x.SlotId].Period);
        unknownSlots = cachedDisplay.Lessons.Where(x => x.TeacherId is null).Select(x => x.SlotId).ToHashSet();
        indexedPlacements = placements; adviceCache.Clear(); adviceActive = null;
    }
    public ManualChainDraft(ProjectSession session, SwapDirection direction = SwapDirection.Both)
    {
        Direction = direction; this.session = session; revision = session.Revision; original = session.Document.Current; school = new SchoolDayRules(session.Document);
        lessons = original.Lessons.ToDictionary(x => x.Id); slots = original.Slots.ToDictionary(x => x.Id);
        placements = original.Lessons.ToImmutableDictionary(x => x.Id, x => x.SlotId);
        blocks = original.Blocks.Select(x => (x.TeacherId, x.SlotId)).ToHashSet();
    }
    private bool Editable(string slot) => session.Document.Calendar?.CanEdit(slots[slot]) != false;
    public string? Take(string id)
    {
        if (session.Revision != revision) return "확정 시간표가 바뀌었습니다. 연쇄 작업을 취소하고 다시 시작하세요.";
        if (!lessons.TryGetValue(id, out var lesson)) return "수업을 찾을 수 없습니다.";
        if (school.IsExcluded(lesson)) return "행사 제외 날짜·학년의 수업은 꺼낼 수 없습니다.";
        if (lesson.Locked || lesson.TeacherId is null) return "잠금 또는 교사 미배정 수업은 꺼낼 수 없습니다.";
        if (placements.TryGetValue(id, out var at))
        {
            if (school.IsExcluded(lesson.ClassId, at)) return "행사 제외 날짜·학년의 수업입니다.";
            if (!Editable(at)) return "오늘 이전이거나 선택한 작업 기간 밖의 수업입니다.";
            if (!AllowsDate(at)) return "이 날짜의 수업을 꺼내려면 이전·이후 날짜 허용 옵션을 켜세요.";
            EnsureIndex();
            if (unknownSlots.Contains(at)) return "같은 시간에 교사 미배정 수업이 있어 가용성을 확인할 수 없습니다.";
            undo.Push((placements, heldFrom, ActiveId, DateAnchorSlotId)); DateAnchorSlotId ??= at; heldFrom = heldFrom.SetItem(id, at); placements = placements.Remove(id);
        }
        ActiveId = id; RecordTeacher(lesson);
        return null;
    }
    private LessonInstance[] Conflicts(LessonInstance picked, string slot)
    {
        EnsureIndex();
        return bySlot[slot].Where(x => x.ClassId == picked.ClassId || x.TeacherId == picked.TeacherId ||
            picked.Room is not null && x.Room == picked.Room).ToArray();
    }
    public IReadOnlyList<LessonInstance> DisplacedAt(string slot) => ActiveId is { } id ? Conflicts(lessons[id], slot) : [];
    public IReadOnlyList<ConsecutiveRisk> PlacementRisks(string target)
    {
        if (ActiveId is not { } id || !Advice(target).Allowed) return [];
        var displaced = Conflicts(lessons[id], target).Select(x => x.Id).ToHashSet();
        var after = DisplayState.Lessons.Where(x => !displaced.Contains(x.Id)).Append(lessons[id] with { SlotId = target }).ToArray();
        var newlyPlaced = new ConsecutiveTeaching(DisplayState).Evaluate(after).ToHashSet();
        return new ConsecutiveTeaching(original).Evaluate(after).Where(newlyPlaced.Contains).ToArray();
    }
    public PlacementAdvice Advice(string target)
    {
        if (session.Revision != revision) return new(false,false,false,0,"확정 시간표가 바뀌었습니다. 다시 시작하세요.");
        EnsureIndex();
        if (adviceActive != ActiveId) { adviceCache.Clear(); adviceActive = ActiveId; }
        if (!adviceCache.TryGetValue(target, out var advice)) adviceCache[target] = advice = Evaluate(target);
        return advice;
    }
    public PlacementAdvice AdviceBeforeDatePermission(string target) => Evaluate(target, false);
    private PlacementAdvice Evaluate(string target, bool checkDirection = true)
    {
        if (session.Revision != revision || ActiveId is not { } id || !slots.TryGetValue(target, out var slot))
            return new(false,false,false,0,"먼저 대기 카드를 선택하세요.");
        if (!Editable(target)) return new(false,false,false,0,"오늘 이전이거나 선택한 작업 기간 밖입니다.");
        if (checkDirection && !AllowsDate(target)) return new(false,false,false,0,"다른 날짜입니다. 클릭하면 날짜 허용 옵션 변경을 확인합니다.");
        var picked = lessons[id];
        if (school.IsExcluded(picked.ClassId, target)) return new(false,false,false,0,"선택 수업의 학년이 이 날짜에 행사 제외되어 있습니다.");
        if (blocks.Contains((picked.TeacherId!, target)))
            return new(false,false,false,0,"선택 교사의 배정 제외 시간입니다.");
        var conflicts = Conflicts(picked,target);
        if (conflicts.Any(school.IsExcluded)) return new(false,false,false,conflicts.Length,"행사 제외 수업은 밀어낼 수 없습니다.");
        if (conflicts.Any(x => x.Locked)) return new(false,false,false,conflicts.Length,"잠긴 수업과 겹칩니다.");
        if (unknownSlots.Contains(target))
            return new(false,false,false,conflicts.Length,"교사 미배정 수업이 있어 가용성을 확인할 수 없습니다.");
        // Replacing a conflicting lesson of this teacher at the target keeps that same period occupied.
        bool triple = TeachingAdvice.LongestPeriods(teacherPeriods[(picked.TeacherId!, slot.Week, slot.Day)].Append(slot.Period)) >= 3;
        bool returning = target == ActiveOriginSlotId;
        var teachers = original.Teachers;
        var message = conflicts.Length > 0 ? "밀려날 수업: " + string.Join(" / ", conflicts.Select(x =>
            x.Subject + " · " + teachers.Single(t => t.Id == x.TeacherId).Name))
            : returning ? "방금 꺼낸 자리 · 제자리 복귀이며 추천 시간이 아닙니다."
            : "추가로 밀려나는 수업 없음";
        if (returning && conflicts.Length > 0) message = "직전 자리 · 다른 교시를 권장합니다. " + message;
        return new(true,conflicts.Length == 0 && !triple && !returning,triple,conflicts.Length,
            message + (triple ? " · ! 3연강 이상" : " · 3연강 없음"));
    }
    public string? Put(string target)
    {
        var advice = Advice(target);
        if (!advice.Allowed) return advice.Message;
        var id = ActiveId!;
        var displaced = Conflicts(lessons[id],target);
        undo.Push((placements,heldFrom,ActiveId,DateAnchorSlotId));
        foreach (var other in displaced)
        { RecordTeacher(other); heldFrom = heldFrom.SetItem(other.Id, placements[other.Id]); placements = placements.Remove(other.Id); }
        placements = placements.SetItem(id,target);
        heldFrom = heldFrom.Remove(id);
        ActiveId = displaced.FirstOrDefault()?.Id ?? Held.FirstOrDefault()?.Id;
        return null;
    }
    public bool Undo()
    {
        if (!undo.TryPop(out var previous)) return false;
        placements = previous.Placements; heldFrom = previous.HeldFrom; ActiveId = previous.Active; DateAnchorSlotId = previous.Anchor; return true;
    }
    public ChangePlan? Plan()
    {
        if (session.Revision != revision || !Held.IsEmpty) return null;
        var deltas = PlacedChanges;
        return deltas.IsEmpty ? null : new(revision,ChangeKind.Move,$"수동 연쇄 교체 · {deltas.Length}개 수업",deltas);
    }
}
