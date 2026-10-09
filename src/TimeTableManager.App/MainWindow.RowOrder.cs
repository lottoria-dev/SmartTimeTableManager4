using System.Windows.Input;
using TimeTableManager.Application;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private bool DeferRowPromotion => dragActive || pressedCell is not null && Mouse.LeftButton == MouseButtonState.Pressed;
    private List<string> PriorityTeachers()
    {
        if (manualDraft is not null) return manualDraft.ParticipatingTeachers.ToList();
        if (workMode != WorkMode.Swap || vm.Session is null || vm.SelectedLessonId is not { } id) return [];
        var state = vm.Session.Document.Current;
        var selected = state.Lessons.Single(x => x.Id == id).TeacherId;
        if (selected is null) return [];
        var candidates = state.Lessons.Where(x => swapCandidates.Contains(x.Id) && x.TeacherId is not null)
            .Select(x => x.TeacherId!).ToHashSet();
        return new[] { selected }.Concat(vm.Teachers.Where(x => candidates.Contains(x.Id)).Select(x => x.Id)).Distinct().ToList();
    }
    private List<Option> OrderTeacherRows(List<Option> rows)
    {
        // During drag the cell under the pointer must stay in place. Promote once the drag ends.
        var order = DeferRowPromotion ? boardRows.Select(x => x.Id).ToList() : PriorityTeachers();
        var positions = order.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return rows.OrderBy(x => positions.GetValueOrDefault(x.Id, int.MaxValue)).ToList();
    }
    private void UpdateTeacherRowMarkers(bool teachers)
    {
        var priorities = teachers ? PriorityTeachers() : [];
        for (int i = 0; i < boardRows.Count; i++)
        {
            var row = boardRows[i]; var rank = priorities.IndexOf(row.Id);
            row.Marker = rank < 0 ? "" : manualDraft is not null ? (rank + 1).ToString()
                : rank == 0 ? "선택" : "후보";
        }
    }
    private void FocusPriorityTeacher()
    {
        if (ViewMode.SelectedIndex != 0 || DeferRowPromotion || vm.Session is null) return;
        var selectedId = vm.SelectedLessonId;
        var teacher = vm.Session.Document.Current.Lessons.FirstOrDefault(x => x.Id == selectedId)?.TeacherId;
        var row = boardRows.FirstOrDefault(x => x.Id == teacher);
        if (row is null) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!DeferRowPromotion && vm.SelectedLessonId == selectedId && ViewMode.SelectedIndex == 0 && boardRows.Contains(row)) TimetableGrid.ScrollIntoView(row);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
