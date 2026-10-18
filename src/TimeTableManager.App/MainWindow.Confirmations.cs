using System.Windows;
using System.Windows.Input;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private readonly RechangeReview rechangeReview = new();
    private bool ShowWorkDialog(Window dialog)
    {
        var cursor = Mouse.OverrideCursor;
        try { Mouse.OverrideCursor = null; dialog.Owner = this; return dialog.ShowDialog() == true; }
        finally { Mouse.OverrideCursor = cursor; }
    }
    private bool ConfirmRechanges(IEnumerable<string> lessonIds, ChangePlan? plan = null)
    {
        if (vm.Session is not { } session) return false;
        var ids = rechangeReview.Required(session, lessonIds).Where(id => session.Document.Current.Lessons.Any(x => x.Id == id)).ToArray();
        if (ids.Length == 0) return true;
        var revision = session.Revision;
        var current = session.Document.Current.Lessons.ToDictionary(x => x.Id);
        var baseline = session.Document.Baseline.Lessons.ToDictionary(x => x.Id);
        var after = plan?.Changes.ToDictionary(x => x.Before.Id);
        string Line(string id)
        {
            var text = "현재: " + vm.Describe(current[id]).Replace("\n", " · ") + "\n최초: " +
                (baseline.TryGetValue(id, out var initial) ? vm.Describe(initial).Replace("\n", " · ") : "기초 요일 적용으로 추가된 수업");
            return text + (after is not null && after.TryGetValue(id, out var next)
                ? next.Removed ? "\n이번 안: 선택 날짜에서 제외" : "\n이번 안: " + vm.Describe(next.After).Replace("\n", " · ") : "\n이번 작업에서 다시 이동하거나 대기 카드로 꺼낼 수업입니다.");
        }
        var window = new ReviewWindow("이미 변경한 수업을 다시 변경", $"이미 변경한 수업 {ids.Length}개를 다시 바꾸려 합니다. 재변경을 허용할까요?",
            string.Join("\n\n", ids.Select(Line)), "재변경 허용", "• 이번 작업에만 허용\n• 취소하면 이번 이동 중단\n• 잠금 변경만 한 수업은 재변경 확인에서 제외");
        if (!ShowWorkDialog(window) || !ReferenceEquals(vm.Session, session) || session.Revision != revision)
        { ActionHint.Text = "재변경을 취소했습니다. 다른 수업·목적 시간을 선택할 수 있습니다."; return false; }
        rechangeReview.Approve(session, ids); return true;
    }
    private void SetDirectionChecks(SwapDirection direction)
    {
        AllowEarlier.IsChecked = direction is SwapDirection.Earlier or SwapDirection.Both;
        AllowLater.IsChecked = direction is SwapDirection.Later or SwapDirection.Both;
    }
    private bool EnsureManualDates(IEnumerable<string> slotIds)
    {
        if (manualDraft is not { } draft || vm.Session?.Document.Calendar is not { } calendar) return false;
        var slots = vm.Session.Document.Current.Slots.ToDictionary(x => x.Id);
        var dates = slotIds.Distinct().Select(id => slots[id]).ToArray();
        if (dates.Any(x => !calendar.CanEdit(x))) { ActionHint.Text = "오늘 이전과 작업 기간 밖은 날짜 허용 옵션으로 해제할 수 없습니다."; return false; }
        if (draft.DateAnchorSlotId is not { } anchor || dates.All(x => draft.AllowsDate(x.Id))) return true;
        var date = calendar.DateOf(slots[anchor]);
        bool earlier = AllowEarlier.IsChecked == true || dates.Any(x => calendar.DateOf(x) < date);
        bool later = AllowLater.IsChecked == true || dates.Any(x => calendar.DateOf(x) > date);
        var next = earlier ? (later ? SwapDirection.Both : SwapDirection.Earlier) : SwapDirection.Later;
        var text = next == SwapDirection.Both ? "이전·이후 날짜" : next == SwapDirection.Earlier ? "이전 날짜" : "이후 날짜";
        var revision = vm.Session.Revision;
        if (!ShowWorkDialog(new ReviewWindow("다른 날짜 이동 허용", $"{text} 교체를 허용하고 수동 작업을 계속할까요?",
            $"처음 선택한 수업 날짜: {date:yyyy/M/d}\n선택한 시간: {string.Join(" / ", dates.Select(calendar.Label))}", "옵션 변경 후 계속",
            "• 날짜 허용은 연쇄 참여 수업 전체에 적용\n• 잠금·행사 제외·작업 기간 유지")))
        { ActionHint.Text = "날짜 옵션과 대기 카드를 유지했습니다. 다른 목적 시간을 선택하세요."; return false; }
        if (!ReferenceEquals(manualDraft, draft) || vm.Session.Revision != revision) return false;
        var error = draft.SetDirection(next);
        if (error is not null) { ActionHint.Text = error; return false; }
        SetDirectionChecks(next); RefreshManualBoard(); return true;
    }
    private bool TryTakeManual(string id)
    {
        if (manualDraft is not { } draft || vm.Session is null) return false;
        var lesson = draft.DisplayState.Lessons.FirstOrDefault(x => x.Id == id);
        if (lesson is not null)
        {
            if (lesson.Locked || lesson.TeacherId is null || new SchoolDayRules(vm.Session.Document).IsExcluded(lesson))
            { ActionHint.Text = "잠금·교사 미배정·행사 제외 수업은 꺼낼 수 없습니다."; return false; }
            if (!EnsureManualDates([lesson.SlotId])) return false;
        }
        if (!ConfirmRechanges([id])) return false;
        var error = draft.Take(id);
        if (error is not null) { ActionHint.Text = error; return false; }
        return true;
    }
    private bool ConfirmOwnLessonDisplacement(string slot, string active, ProjectSession session)
    {
        if (manualDraft is not { } draft) return false;
        var teacher = session.Document.Current.Lessons.Single(x => x.Id == active).TeacherId;
        var firstTeacher = draft.ParticipatingTeachers.FirstOrDefault();
        var affected = draft.DisplacedAt(slot).Where(x => x.TeacherId == teacher || x.TeacherId == firstTeacher).ToArray();
        if (affected.Length == 0) return true;
        var revision = session.Revision;
        bool accepted = ShowWorkDialog(new ReviewWindow("본인의 다른 수업도 이동", "본인의 다른 수업이 대기 카드로 밀려납니다. 계속할까요?",
            string.Join("\n", affected.Select(vm.Describe)), "확인하고 계속", "이 수업을 처음 출발한 시간에 넣으면 본인의 공강이 생기지 않습니다. 의도한 배치인지 확인하세요."));
        if (!accepted) ActionHint.Text = "본인 수업 밀어내기를 취소했습니다. 다른 시간을 선택하세요.";
        return accepted && ReferenceEquals(manualDraft, draft) && ReferenceEquals(vm.Session, session) && session.Revision == revision;
    }
    private SwapDirection? ChooseExpandedDates(ProjectSession session, string sourceId, string? targetId, IReadOnlySet<string> scope)
    {
        var calendar = session.Document.Calendar!; var state = session.Document.Current;
        var slots = state.Slots.ToDictionary(x => x.Id);
        var origin = slots[state.Lessons.Single(x => x.Id == sourceId).SlotId];
        var choices = new List<Option>();
        foreach (var direction in new[] { SwapDirection.Later, SwapDirection.Earlier, SwapDirection.Both })
        {
            if (targetId is not null && !CalendarSwapSearch.Allows(calendar, origin, slots[targetId], direction)) continue;
            if (!state.Slots.Any(x => scope.Contains(x.Id) && calendar.DateOf(x) != calendar.DateOf(origin) && CalendarSwapSearch.Allows(calendar, origin, x, direction))) continue;
            choices.Add(new(direction.ToString(), direction == SwapDirection.Later ? "이후 날짜 포함" : direction == SwapDirection.Earlier ? "이전 날짜 포함" : "이전·이후 모두 포함"));
        }
        if (choices.Count == 0) return null;
        var dialog = new ChoiceWindow("다른 날짜를 포함해 재탐색", $"선택 수업 날짜 {calendar.DateOf(origin):M/d} 안에서 교체안을 찾지 못했습니다. 이 날짜의 이전·이후를 포함할까요? 지정한 목적 시간은 유지하며 오늘 이전과 선택한 작업 기간 밖은 제외합니다.", choices, "옵션 적용 후 재탐색");
        return ShowWorkDialog(dialog) && dialog.Choice is { } choice ? Enum.Parse<SwapDirection>(choice.Id) : null;
    }
}
