using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private bool lockSelectionMode;
    private readonly HashSet<(string RowId, string SlotId)> lockCells = [];
    private BoardCell[] SelectedLockCells() => boardRows.SelectMany(x => x.Cells).Where(x => lockCells.Contains((x.RowId, x.SlotId))).ToArray();
    private void ToggleLockSelectionClick(object sender, RoutedEventArgs e)
    {
        if (busy || workMode != WorkMode.Browse || manualDraft is not null || vm.Session is null) { UpdateWorkflowControls(); return; }
        if (lockSelectionMode) { ExitLockSelection(); PaintPlan(); UpdateWorkflowControls(); ActionHint.Text = "다중 잠금을 끝냈습니다."; return; }
        CancelInteraction();
        lockSelectionMode = true;
        TimetableGrid.SelectionMode = DataGridSelectionMode.Extended;
        TimetableGrid.SelectedCells.Clear();
        UpdateWorkflowControls();
        ActionHint.Text = "잠글 셀을 드래그하거나 Shift·Ctrl로 선택하세요. 선택 후 우클릭 → 선택 수업 잠금/해제. 이 모드에서는 수업을 이동하지 않습니다.";
    }
    private void ExitLockSelection()
    {
        if (!lockSelectionMode && lockCells.Count == 0) return;
        lockSelectionMode = false; lockCells.Clear();
        TimetableGrid.SelectedCells.Clear(); TimetableGrid.SelectionMode = DataGridSelectionMode.Single;
    }
    private void LockCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (!lockSelectionMode || refreshing) return;
        lockCells.Clear();
        foreach (var selected in TimetableGrid.SelectedCells)
        {
            int index = selected.Column.DisplayIndex - 1;
            if (selected.Item is BoardRow row && index >= 0 && index < row.Cells.Count)
                lockCells.Add((row.Cells[index].RowId, row.Cells[index].SlotId));
        }
        var lessons = SelectedLockCells().Where(x => x.LessonId is not null).ToArray();
        PaintPlan();
        ActionHint.Text = $"{lessons.Length}개 수업 선택 · 이미 잠김 {lessons.Count(x => x.Locked)}개. 우클릭 또는 상단 잠금 메뉴에서 일괄 잠금/해제를 선택하세요.";
    }
    private void ApplyBulkLocks(IEnumerable<BoardCell> cells, bool locked)
    {
        if (busy || workMode != WorkMode.Browse || manualDraft is not null || vm.Session is null) return;
        var ids = cells.Where(x => x.LessonId is not null).Select(x => x.LessonId!).Distinct().ToArray();
        if (ids.Length == 0) { ActionHint.Text = "선택한 수업이 없습니다. ‘다중 잠금’에서 셀을 선택하세요."; return; }
        var plan = ChangePlanner.SetLocks(vm.Session, ids, locked);
        ApplyLockPlan(plan, locked);
    }
    private void ApplyLockPlan(ChangePlan plan, bool locked)
    {
        if (busy || workMode != WorkMode.Browse || manualDraft is not null || vm.Session is null) return;
        if (plan.Changes.IsEmpty) { ActionHint.Text = locked ? "선택한 수업은 모두 잠겨 있습니다." : "선택한 수업은 모두 잠금이 해제되어 있습니다."; return; }
        var result = vm.Session.Apply(plan);
        if (!result.IsValid) { ActionHint.Text = string.Join(" ", result.Issues.Select(x => x.Message)); return; }
        ExitLockSelection(); vm.Refresh(); RefreshGrid();
        ActionHint.Text = $"{plan.Changes.Length}개 수업을 일괄 {(locked ? "잠갔습니다" : "해제했습니다")}. Ctrl+Z로 한 번에 되돌립니다.";
    }
    private bool OpenTeacherNameContext(object source)
    {
        if (ViewMode.SelectedIndex != 0 || vm.Session is not { } session) return false;
        var node = source as DependencyObject;
        while (node is not null && node is not DataGridCell)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is not DataGridCell { DataContext: BoardRow row } cell || cell.Column.DisplayIndex != 0 ||
            !session.Document.Current.Teachers.Any(x => x.Id == row.Id)) return false;
        var revision = session.Revision;
        var count = session.Document.Current.Lessons.Count(x => x.TeacherId == row.Id);
        var menu = new ContextMenu { PlacementTarget = cell };
        menu.Items.Add(new MenuItem { Header = $"{row.Name} · 전체 주차·요일 {count}개 수업", IsEnabled = false });
        foreach (bool locked in new[] { true, false })
        {
            var item = new MenuItem { Header = locked ? "이 교사 수업 전체 잠금" : "이 교사 수업 전체 잠금 해제",
                ToolTip = "현재 화면에 보이지 않는 날짜의 수업도 포함합니다.",
                IsEnabled = workMode == WorkMode.Browse && manualDraft is null && count > 0 };
            item.Click += (_, _) =>
            {
                if (!busy && ReferenceEquals(vm.Session, session) && session.Revision == revision)
                    ApplyLockPlan(ChangePlanner.SetTeacherLocks(session, row.Id, locked), locked);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true; return true;
    }
    private void LockSelectedClick(object sender, RoutedEventArgs e) => ApplyBulkLocks(SelectedLockCells(), true);
    private void UnlockSelectedClick(object sender, RoutedEventArgs e) => ApplyBulkLocks(SelectedLockCells(), false);
    private void LockVisibleClick(object sender, RoutedEventArgs e) => ApplyBulkLocks(boardRows.SelectMany(x => x.Cells).ToArray(), true);
    private void UnlockVisibleClick(object sender, RoutedEventArgs e) => ApplyBulkLocks(boardRows.SelectMany(x => x.Cells).ToArray(), false);
    private MenuItem BuildBulkLockMenu(BoardCell cell)
    {
        var root = new MenuItem { Header = "일괄 잠금 · 현재 조회 범위" };
        var session = vm.Session!; var revision = session.Revision;
        void Scope(string label, IEnumerable<BoardCell> candidates)
        {
            var cells = candidates.Where(x => x.LessonId is not null).ToArray();
            if (cells.Length == 0) return;
            var submenu = new MenuItem { Header = $"{label} · {cells.Length}개" };
            foreach (bool locked in new[] { true, false })
            {
                var item = new MenuItem { Header = locked ? "모두 잠금" : "모두 잠금 해제" };
                item.Click += (_, _) => { if (ReferenceEquals(vm.Session, session) && session.Revision == revision) ApplyBulkLocks(cells, locked); };
                submenu.Items.Add(item);
            }
            root.Items.Add(submenu);
        }
        var all = boardRows.SelectMany(x => x.Cells).ToArray();
        Scope(cell.TeacherRow ? "이 교사 행" : "이 학급 행", all.Where(x => x.RowId == cell.RowId));
        Scope("이 교시 전체", all.Where(x => x.SlotId == cell.SlotId));
        var slots = session.Document.Current.Slots.ToDictionary(x => x.Id); var at = slots[cell.SlotId];
        Scope("이 날짜 전체", all.Where(x => slots[x.SlotId].Week == at.Week && slots[x.SlotId].Day == at.Day));
        if (cell.LessonId is { } id)
        {
            var lessons = session.Document.Current.Lessons.ToDictionary(x => x.Id);
            Scope("같은 과목 전체 · " + lessons[id].Subject, all.Where(x => x.LessonId is { } other && lessons[other].Subject == lessons[id].Subject));
        }
        return root;
    }
    private void OpenLockContext(BoardCell cell)
    {
        var selected = SelectedLockCells(); var session = vm.Session!; var revision = session.Revision;
        var menu = new ContextMenu { PlacementTarget = TimetableGrid };
        foreach (bool locked in new[] { true, false })
        {
            var item = new MenuItem { Header = $"선택한 수업 {selected.Count(x => x.LessonId is not null)}개 {(locked ? "잠금" : "잠금 해제")}", IsEnabled = selected.Any(x => x.LessonId is not null) };
            item.Click += (_, _) => { if (ReferenceEquals(vm.Session, session) && session.Revision == revision) ApplyBulkLocks(selected, locked); };
            menu.Items.Add(item);
        }
        menu.Items.Add(BuildBulkLockMenu(cell));
        var done = new MenuItem { Header = "범위 선택 종료" };
        done.Click += (_, _) => { ExitLockSelection(); PaintPlan(); UpdateWorkflowControls(); };
        menu.Items.Add(done); menu.IsOpen = true;
    }
}
