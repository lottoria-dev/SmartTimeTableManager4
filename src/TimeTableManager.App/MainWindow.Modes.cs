using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private enum WorkMode { Browse, Swap, Cover, Manual, Auto }
    private WorkMode workMode = WorkMode.Browse;
    private bool changingMode;
    private bool ModeAllows(ChangePlan? plan) => plan is not null && (workMode switch
    {
        WorkMode.Swap => plan.Kind == ChangeKind.Move && plan.Changes.Length == 2,
        WorkMode.Cover => plan.Kind == ChangeKind.AssignTeacher,
        WorkMode.Manual => plan.Kind == ChangeKind.Move && manualDraft is not null && manualDraft.Held.IsEmpty,
        WorkMode.Auto => plan.Kind == ChangeKind.Move,
        _ => false
    });
    private string ModeGuide => workMode switch
    {
        WorkMode.Browse => "조회 · 클릭하면 해당 교사의 모든 수업을 강조합니다. 이 모드에서는 수업을 옮기지 않습니다.",
        WorkMode.Swap => "맞교환 · 첫 수업 클릭 → 초록색 상대 클릭 → 확인 후 두 수업 교환",
        WorkMode.Cover => "보강 · 수업 클릭 → 공강 교사 선택 → 보강 적용 (*는 3연강 이상)",
        WorkMode.Manual => ViewMode.SelectedIndex == 1
            ? "수동 교체 · 같은 학급 행의 추천 칸에 놓으세요. 밀려난 수업을 모두 배치한 뒤 적용합니다."
            : "수동 교체 · 참여 교사를 상단에 순서대로 모읍니다. 대기 교사의 초록 칸에 배치하고 밀려날 수업은 도움말에서 확인하세요.",
        _ => "AI 자동 · 당일 우선 · 3연강 회피, 필요 시 허용 확인 · 지정 목적 고정 · 우클릭: 하루 이동"
    };
    private void WorkModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || refreshing || changingMode || !ReferenceEquals(e.OriginalSource, WorkModes)) return;
        var next = (WorkMode)WorkModes.SelectedIndex;
        if (next == workMode) return;
        if (busy || manualDraft is not null)
        {
            changingMode = true; WorkModes.SelectedIndex = (int)workMode; changingMode = false;
            ActionHint.Text = "진행 중인 작업을 적용하거나 취소한 뒤 모드를 바꾸세요."; return;
        }
        CancelInteraction(); workMode = next;
        RefreshGrid(); UpdateWorkflowControls(); ActionHint.Text = ModeGuide;
    }
    private void ChangedRowsClick(object sender, RoutedEventArgs e)
    { if (!busy && workMode == WorkMode.Browse) RefreshGrid(); }
    private void ModeCellClick(BoardCell cell)
    {
        if (busy || vm.Session is null) return;
        if (cell.LessonId is { } selected && selected == vm.SelectedLessonId && manualDraft is null)
        { CancelInteraction(); return; }
        if (workMode == WorkMode.Browse)
        {
            if (cell.LessonId is { } id) Pick(id); else CancelInteraction();
            return;
        }
        if (workMode == WorkMode.Manual && manualDraft is not null)
        {
            if (manualDraft.ActiveId is { } active && manualDraft.ActiveOriginSlotId is { } origin &&
                Matches(cell, vm.Session.Document.Current.Lessons.Single(x => x.Id == active) with { SlotId = origin }))
            {
                KeepHeldSelection();
                return;
            }
            ManualCell(cell); return;
        }
        if (workMode == WorkMode.Auto && vm.SelectedLessonId is { } source)
        {
            if (cell.LessonId == source) { CancelInteraction(); return; }
            if (AllowedTarget(source, cell)) { PreviewTarget(cell); return; }
        }
        if (cell.LessonId is not { } lessonId) return;
        if (workMode == WorkMode.Swap && vm.SelectedLessonId is { } first && first != lessonId && swapCandidates.Contains(lessonId))
        { ConfirmSwap(lessonId); return; }
        Pick(lessonId);
        if (vm.SelectedLessonId != lessonId) return;
        switch (workMode)
        {
            case WorkMode.Swap: SwapStartClick(this, new RoutedEventArgs()); break;
            case WorkMode.Cover: CoverClick(this, new RoutedEventArgs()); break;
            case WorkMode.Manual: ManualStartClick(this, new RoutedEventArgs()); break;
        }
    }
    private void ConfirmSwap(string secondId)
    {
        if (workMode != WorkMode.Swap || vm.Session is not { } session || vm.SelectedLessonId is not { } firstId) return;
        var plan = ChangePlanner.SwapTwo(session, firstId, secondId);
        var validation = session.Preview(plan);
        if (!validation.IsValid) { ActionHint.Text = string.Join(" ", validation.Issues.Select(x => x.Message)); return; }
        var first = session.Document.Current.Lessons.Single(x => x.Id == firstId);
        var second = session.Document.Current.Lessons.Single(x => x.Id == secondId);
        var message = vm.Describe(first) + "\n\n↕ 1대1 맞교환\n\n" + vm.Describe(second) + "\n\n두 수업의 시간을 교환할까요?";
        if (MessageBox.Show(this, message, "맞교환 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(vm.Session, session) || plan.Revision != session.Revision) { ActionHint.Text = "시간표가 바뀌었습니다. 다시 선택하세요."; return; }
        vm.ShowPlan(plan); ApplyClick(this, new RoutedEventArgs());
    }
    private void ExclusionsClick(object sender, RoutedEventArgs e) => EditExclusions(null);
    private void EditExclusions(DateOnly? onlyDate)
    {
        if (busy || manualDraft is not null || vm.Session is not { } session || !CalendarReady) return;
        var revision = session.Revision;
        var window = new GradeExclusionsWindow(session.Document, onlyDate) { Owner = this };
        if (window.ShowDialog() != true || !ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
        CancelInteraction(); session.SetGradeDateExclusions(window.Result);
        vm.Refresh(); RefreshGrid();
        ActionHint.Text = "학년 행사 제외를 반영했습니다. 흐린 수업과 해당 학년의 빈 시간은 모든 교체에서 제외됩니다. 작업을 저장하세요.";
    }
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (busy || dragActive || vm.Session is not { } session) return;
        var revision = session.Revision;
        if (MessageBox.Show(this, "이 작업 파일의 모든 교체·보강 이력을 지우고 현재 기초 배정으로 돌아갈까요?\n학교 계획으로 적용한 기초 요일·날짜·학년 행사 설정은 유지합니다.\n초기화 후 Ctrl+Z로 복원할 수 없습니다.",
            "전체 초기화", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
        CancelInteraction(); session.ResetToBaseline(); vm.Attach(session);
        changingMode = true; workMode = WorkMode.Browse; WorkModes.SelectedIndex = 0; changingMode = false;
        ChangedRowsOnly.IsChecked = false; RefreshGrid();
        ActionHint.Text = "현재 기초 배정으로 초기화했습니다. 저장하면 현재 시각이 붙은 새 작업 파일을 만듭니다.";
    }
}
