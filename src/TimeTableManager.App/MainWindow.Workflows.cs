using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private enum BoardAction { Normal, Swap }
    private BoardAction boardAction;
    private ManualChainDraft? manualDraft;
    private Dictionary<string,PlacementAdvice> placementAdvice = [];
    private bool updatingBasket;
    private string? fixedTargetSlotId;
    private HashSet<string> swapCandidates = [];
    private PlanPreviewWindow? previewWindow;
    private ChangePlan? drawnPlan;
    private ManualChainDraft? drawnDraft;
    private ImmutableArray<LessonDelta> drawnChanges = [];
    private ScheduleState? DisplayState => manualDraft?.DisplayState ?? vm.Session?.Document.Current;
    private SwapDirection Direction => AllowEarlier.IsChecked == true ? (AllowLater.IsChecked == true ? SwapDirection.Both : SwapDirection.Earlier)
        : AllowLater.IsChecked == true ? SwapDirection.Later : SwapDirection.SameDay;
    private void UpdateWorkflowControls()
    {
        if (!IsInitialized || ModeActions is null) return;
        bool idle = !busy && manualDraft is null && !lockSelectionMode;
        bool browsing = workMode == WorkMode.Browse, automatic = workMode == WorkMode.Auto;
        bool drafting = workMode is WorkMode.Manual or WorkMode.Auto;
        FileTools.IsEnabled = !busy && manualDraft is null;
        WorkModes.IsEnabled = !busy && manualDraft is null;
        LocksMenu.IsEnabled = browsing && !busy && vm.Session is not null;
        LockRangeButton.Visibility = ChangedRowsOnly.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        LockRangeButton.IsEnabled = !busy && vm.Session is not null;
        ChangedRowsOnly.IsEnabled = idle;
        LockSelectionOption.IsChecked = lockSelectionMode;
        LockRangeButton.Content = lockSelectionMode ? "다중 잠금 종료" : "다중 잠금";
        ExclusionsMenu.IsEnabled = ExclusionsButton.IsEnabled = idle && vm.Session is not null;
        ViewMode.IsEnabled = WeekFilter.IsEnabled = DayFilter.IsEnabled = !busy && !lockSelectionMode;
        CalendarTools.IsEnabled = idle;
        AutoOptionsPanel.Visibility = drafting ? Visibility.Visible : Visibility.Collapsed;
        AutoButton.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        AutoOptionsPanel.IsEnabled = !busy && !lockSelectionMode;
        DateDirectionHint.Text = workMode == WorkMode.Manual ? "선택 수업 날짜 기준: " : "선택일 우선 · 추가 허용: ";
        AutoPreview.IsEnabled = !busy && drafting;
        ShowHints.IsEnabled = !busy && workMode == WorkMode.Manual;
        AllowEarlier.IsEnabled = AllowLater.IsEnabled = !busy && !lockSelectionMode;
        AutoButton.IsEnabled = idle && vm.SelectedLessonId is not null;
        ApplyButton.Visibility = PreviewButton.Visibility = drafting ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !busy && ModeAllows(vm.Pending) && vm.CanApply;
        DraftUndoButton.Visibility = manualDraft is null ? Visibility.Collapsed : Visibility.Visible;
        BasketPanel.Visibility = workMode == WorkMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        if (manualDraft is null) { BasketList.ItemsSource = null; BasketHint.Text = "표에서 수업을 선택하면 배치할 카드가 여기에 표시됩니다."; }
        if (manualDraft?.ActiveId is null && !dragActive) ClearDragCard();
        DraftUndoButton.IsEnabled = !busy && manualDraft?.CanUndo == true;
        UpdateLegend();
        RefreshPendingDiagram();
    }
    private void RefreshPendingDiagram(bool force = false)
    {
        if (!IsInitialized || PreviewButton is null) return;
        var changes = vm.Pending?.Changes ?? manualDraft?.PlacedChanges ?? [];
        PreviewButton.IsEnabled = !busy && workMode is (WorkMode.Manual or WorkMode.Auto) && vm.Session is not null && !changes.IsEmpty;
        var selection = vm.SelectedLessonId is null ? (manualDraft?.Held.IsEmpty == false ? "대기 카드에서 배치할 수업을 선택하세요." : "수업을 선택하세요.")
            : (manualDraft is null ? "선택: " : "배치할 카드: ") + vm.SelectedSummary.Replace("\n", " · ");
        if (manualDraft?.ActiveOriginSlotId is { } origin && vm.SelectedLessonId is { } active && vm.Session is { } owner)
            selection = "배치할 카드: " + vm.Describe(owner.Document.Current.Lessons.Single(x => x.Id == active) with { SlotId = origin }).Replace("\n", " · ");
        else if (manualDraft is not null && manualDraft.Held.IsEmpty && vm.Pending is { } completed)
            selection = $"수동 교체안 · {completed.Changes.Length}개 수업 배치 완료\n교체안 보기에서 전후를 확인하고 적용하세요.";
        TargetHint.Text = selection;
        if (workMode == WorkMode.Auto && vm.SelectedLessonId is not null)
            TargetHint.Text += fixedTargetSlotId is { } target && vm.Session is { } session
                ? "\n목적 고정: " + (session.Document.Calendar?.Label(session.Document.Current.Slots.Single(x => x.Id == target)) ?? target)
                : "\n목적 미지정 · 가능한 시간 탐색";
        bool readyToSearch = workMode == WorkMode.Auto && !busy && vm.SelectedLessonId is not null && fixedTargetSlotId is not null && vm.Pending is null;
        UpdateActionColors(readyToSearch);
        AutoButton.ToolTip = readyToSearch ? "지정한 목적 시간으로, 3연강 이상을 피하는 교체안을 먼저 찾습니다." : "당일 우선으로 탐색하며 3연강 이상은 별도 허용을 확인합니다.";
        if (teacherDayPlan is not null && ReferenceEquals(vm.Pending, teacherDayPlan)) TargetHint.Text = teacherDayPlan.Description +
            (teacherDayReviewDetail is not null ? "\n배치 우선 · 연강 확인 후 필요한 수업만 수작업 조정" : "\n하루 수업 전체 배치 · 교체안 확인 후 적용");
        ClearTargetButton.Visibility = workMode == WorkMode.Auto ? Visibility.Visible : Visibility.Collapsed;
        ClearTargetButton.IsEnabled = !busy && fixedTargetSlotId is not null;
        ApplyButton.IsEnabled = !busy && ModeAllows(vm.Pending) && vm.CanApply;
        previewWindow?.SetCanApply(!busy && ModeAllows(vm.Pending) && vm.CanApply);
        if (vm.Session is null || changes.IsEmpty)
        {
            previewWindow?.Close(); drawnPlan = null; drawnDraft = null; drawnChanges = []; return;
        }
        if (previewWindow is null) return;
        if (!force && ReferenceEquals(drawnPlan, vm.Pending) && ReferenceEquals(drawnDraft, manualDraft) && drawnChanges == changes) return;
        drawnPlan = vm.Pending; drawnDraft = manualDraft; drawnChanges = changes;
        var caption = vm.Pending is not null ? vm.PreviewText : $"미확정 연쇄 · 배치한 변경 {changes.Length}개 · 대기 {manualDraft!.Held.Length}개";
        var document = vm.Pending?.DayTemplate is { } template ? vm.Session.Document with
            { Current = DayTemplatePlanner.Extend(vm.Session.Document.Current, template) } : vm.Session.Document;
        previewWindow.UpdatePlan(document, changes, caption, !busy && ModeAllows(vm.Pending) && vm.CanApply);
    }
    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (busy || workMode is not (WorkMode.Manual or WorkMode.Auto) || vm.Session is null || vm.Pending is null && manualDraft is null) return;
        if (previewWindow is null)
        {
            var window = new PlanPreviewWindow(() => ApplyClick(this, new RoutedEventArgs()), () => CancelClick(this, new RoutedEventArgs())) { Owner = this };
            previewWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(previewWindow, window)) previewWindow = null; };
            RefreshPendingDiagram(true);
            if (ReferenceEquals(previewWindow, window)) window.Show();
        }
        else { RefreshPendingDiagram(true); previewWindow?.Activate(); }
    }
    private void QueuePlanPreview()
    {
        if (workMode is not (WorkMode.Manual or WorkMode.Auto) || AutoPreview.IsChecked != true || !vm.CanApply || vm.Pending is not { } plan) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!busy && !dragActive && ReferenceEquals(vm.Pending, plan)) PreviewClick(this, new RoutedEventArgs());
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void ClearTargetClick(object sender, RoutedEventArgs e)
    {
        if (busy || workMode != WorkMode.Auto || manualDraft is not null) return;
        fixedTargetSlotId = null; targetCell = null; vm.CancelPreview(); PaintPlan();
        ActionHint.Text = "목적을 해제했습니다. 이제 자동 탐색에서 가능한 시간을 찾거나 새 목적 칸을 지정할 수 있습니다.";
    }
    private bool RequireLesson()
    {
        if (busy || workMode == WorkMode.Browse || manualDraft is not null || lockSelectionMode) return false;
        if (vm.SelectedLessonId is not null) return true;
        ActionHint.Text="현재 모드에서 작업할 수업을 클릭하세요.";return false;
    }
    private async void SwapStartClick(object sender, RoutedEventArgs e)
    {
        if (workMode != WorkMode.Swap || !RequireLesson() || vm.Session is null) return;
        var session = vm.Session; var sourceId = vm.SelectedLessonId!; var revision = session.Revision;
        var visible = visibleSlots.Select(x => x.Id).ToHashSet();
        boardAction = BoardAction.Swap; targetCell = null; fixedTargetSlotId = null; swapCandidates.Clear();
        vm.CancelPreview(); PaintPlan();
        using var cancellation = new CancellationTokenSource(); searchCancellation = cancellation;
        try
        {
            SetBusy(true); ActionHint.Text = "현재 조회 범위에서 두 수업만 맞교환할 수 있는 상대를 확인합니다. Esc로 취소할 수 있습니다.";
            var matches = await Task.Run(() => SwapRecommendations.Find(session, sourceId, visible, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(vm.Session, session) || session.Revision != revision || vm.SelectedLessonId != sourceId) return;
            swapCandidates = matches.ToHashSet(); RefreshGrid(true); FocusPriorityTeacher();
            ActionHint.Text = matches.Count == 0
                ? "현재 조회 범위에 1대1 맞교환 가능한 수업이 없습니다. 조회 범위를 넓히거나 자동 교체·수동 연쇄·보강을 사용하세요."
                : $"맞교환 가능한 수업 {matches.Count}개를 초록색으로 표시했습니다. 상대 수업 클릭 후 확인창에서 ‘예’를 누르면 교환합니다.";
        }
        catch (OperationCanceledException) { CancelInteraction(); }
        catch (Exception ex) { ActionHint.Text = "맞교환 후보 확인 실패: " + ex.Message; }
        finally { searchCancellation = null; SetBusy(false); }
    }
    private void ManualStartClick(object sender,RoutedEventArgs e)
    {
        if(workMode != WorkMode.Manual || !RequireLesson() || vm.Session is null || !CalendarReady)return;
        rechangeReview.Reset();
        if (!ConfirmRechanges([vm.SelectedLessonId!])) return;
        var draft=new ManualChainDraft(vm.Session, Direction);var error=draft.Take(vm.SelectedLessonId!);
        if(error is not null){ActionHint.Text=error;return;}
        manualDraft=draft;fixedTargetSlotId=null;targetCell=null;
        RefreshManualBoard();
    }
    private void RefreshManualBoard()
    {
        if(manualDraft is null || vm.Session is null)return;
        vm.Select(manualDraft.ActiveId);targetCell=null;fixedTargetSlotId=null;
        var plan=manualDraft.Plan();if(plan is not null)vm.ShowPlan(plan);
        placementAdvice=manualDraft.ActiveId is null?[]:MonthlyTimetable.WorkingSlots(vm.Session.Document).ToDictionary(x=>x.Id,x=>manualDraft.Advice(x.Id));
        updatingBasket=true;
        var held=manualDraft.Held;
        BasketList.ItemsSource=held.Select(x=>new Option(x.Id,$"{vm.Session.Document.Current.Classes.Single(c=>c.Id==x.ClassId).Name}\n{x.Subject} · {vm.Teachers.Single(t=>t.Id==x.TeacherId).Label}")).ToArray();
        BasketList.SelectedItem=((IEnumerable<Option>)BasketList.ItemsSource).FirstOrDefault(x=>x.Id==manualDraft.ActiveId);
        updatingBasket=false;
        BasketHint.Text=$"대기 수업 {held.Length}개 · 카드를 선택하고 표의 목적 칸에 놓으세요";
        RefreshGrid(true);
        FocusPriorityTeacher();
        if(!held.IsEmpty) ActionHint.Text=manualDraft.ActiveId is null ? "대기 카드를 클릭하고 목적 교시에 배치하세요."
            : !visibleSlots.Any(x => placementAdvice.TryGetValue(x.Id, out var advice) && advice.Recommended)
                ? "현재 범위에 추천 시간이 없습니다. 연한 초록은 배치 가능하며 추가 밀어내기·복귀·연강을 확인하세요."
                : "진한 초록: 배치 추천 · 연한 초록: 배치 가능. 셀에 마우스를 올려 밀려날 수업을 확인하세요. 직전 자리는 추천하지 않습니다.";
        else if(vm.CanApply){QueuePlanPreview();ActionHint.Text="모든 수업을 배치했습니다. 전후를 확인하고 ‘변경 적용’을 누르면 한 작업으로 확정합니다.";}
        else ActionHint.Text=plan is null?"모두 원래 위치입니다. 다른 카드를 꺼내거나 작업을 취소하세요.":vm.PreviewText;
    }
    private void ManualCell(BoardCell cell)
    {
        if(manualDraft is null)return;
        if(manualDraft.ActiveId is not { } active){if(cell.LessonId is { } next)Pick(next);return;}
        if(!AllowedTarget(active,cell)){if(cell.LessonId is { } next)Pick(next);return;}
        var advice = manualDraft.AdviceBeforeDatePermission(cell.SlotId);
        if (!advice.Allowed) { ActionHint.Text = advice.Message; return; }
        if (!EnsureManualDates([cell.SlotId])) return;
        if (vm.Session is not { } session || !ConfirmOwnLessonDisplacement(cell.SlotId, active, session)) return;
        if (!ConfirmRechanges(manualDraft.DisplacedAt(cell.SlotId).Select(x => x.Id).Prepend(active))) return;
        var draft = manualDraft;
        if (!ConfirmConsecutive(session, draft.PlacementRisks(cell.SlotId)) || !ReferenceEquals(manualDraft, draft) || draft.ActiveId != active) return;
        var error=manualDraft.Put(cell.SlotId);
        if(error is not null){ActionHint.Text=error;return;}
        RefreshManualBoard();
    }
    private void BasketSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updatingBasket || busy || manualDraft is null || BasketList.SelectedItem is not Option selected)return;
        if (TryTakeManual(selected.Id)) RefreshManualBoard();
    }
    private void BasketMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (busy || manualDraft is null) return;
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && node is not ListBoxItem)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is ListBoxItem { DataContext: Option option } && option.Id == manualDraft.ActiveId)
        { e.Handled = true; KeepHeldSelection(); }
    }
    private void KeepHeldSelection()
    {
        ActionHint.Text = "대기 수업은 선택을 유지합니다. 다른 교시에 배치하거나 ‘연쇄 한 단계 뒤로’ 또는 ‘선택·작업 취소’를 사용하세요.";
    }
    private void DraftUndoClick(object sender,RoutedEventArgs e)
    { if(!busy && !dragActive && manualDraft?.Undo()==true)RefreshManualBoard(); }
    private void HintsChanged(object sender,RoutedEventArgs e) { UpdateLegend(); PaintPlan(); }
    private void CalendarToggleClick(object sender,RoutedEventArgs e) => CalendarPanel.IsExpanded=!CalendarPanel.IsExpanded;
    private void HistoryClick(object sender,RoutedEventArgs e)
    {
        if(vm.Session is null)return;
        new HistoryWindow(vm.Session.Document){Owner=this}.ShowDialog();
    }
    private void SummaryClick(object sender, RoutedEventArgs e)
    {
        if (vm.Session is not null) new SummaryDiagramWindow(vm.Session.Document) { Owner = this }.ShowDialog();
    }
    private void CoverClick(object sender,RoutedEventArgs e)
    {
        if(workMode != WorkMode.Cover || !RequireLesson() || vm.Session is null)return;
        var session=vm.Session;var id=vm.SelectedLessonId!;var revision=session.Revision;
        var choices=TeachingAdvice.CoverCandidates(session,id).Where(x => !SpecialCover.IsSpecial(x.TeacherId))
            .Concat(SpecialCover.Candidates(session,id)).OrderBy(x => x.ThreeConsecutive).ThenBy(x => SpecialCover.IsSpecial(x.TeacherId)).ToArray();
        if(choices.Length==0){ActionHint.Text="배치 가능한 공강 교사·특별 보강 담당이 없습니다. 잠금·기준일·배정 제외 시간을 확인하세요.";return;}
        var window=new ChoiceWindow("공강 교사·특별 보강", "공강 교사와 교장·교감 특별 보강을 선택할 수 있습니다. *는 배치 후 3연강 이상입니다. 특별 보강도 같은 시간에 중복 배정할 수 없습니다.",
            choices.Select(x=>new Option(x.TeacherId,(SpecialCover.IsSpecial(x.TeacherId) ? "[특별 보강] " : "")+x.Label)),"보강 적용"){Owner=this};
        if(window.ShowDialog()!=true || window.Choice is null || !ReferenceEquals(vm.Session,session) || session.Revision!=revision)return;
        if (SpecialCover.IsSpecial(window.Choice.Id))
        {
            if (!CalendarReady) { ActionHint.Text = "날짜 설정을 확인하세요."; return; }
            if (!ConfirmConsecutivePlan(ChangePlanner.AssignTeacher(session, id, window.Choice.Id))) return;
            var result = session.ApplySpecialCover(id, window.Choice.Id, revision);
            if (!result.IsValid) { ActionHint.Text = string.Join(" ", result.Issues.Select(x => x.Message)); return; }
            vm.Attach(session); RefreshGrid(); ActionHint.Text = "특별 보강을 적용했습니다. 셀 정보에서 원래 교사와 보강 담당을 확인할 수 있습니다."; return;
        }
        boardAction=BoardAction.Normal;targetCell=null;fixedTargetSlotId=null;
        var plan=ChangePlanner.AssignTeacher(session,id,window.Choice.Id) with { Description="공강 교사 보강 · "+window.Choice.Label };
        vm.ShowPlan(plan); ApplyClick(this, new RoutedEventArgs());
    }
}
