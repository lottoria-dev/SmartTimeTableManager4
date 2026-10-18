using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private DateOnly viewedMonth = MonthlyTimetable.MonthOf(DateOnly.FromDateTime(DateTime.Today));

    private void LoadCalendarControls(ProjectSession session, bool showMonth = false)
    {
        var calendar = session.Document.Calendar!;
        WorkHorizon.ItemsSource = Enumerable.Range(0, 4).Select(weeks => new Option(weeks.ToString(),
            $"{(weeks == 0 ? "이번 주" : weeks + "주 후")} 금요일까지 ({BoardDateRange.Friday(Today, weeks):M/d})")).ToArray();
        WorkHorizon.SelectedIndex = calendar.WeeksAhead;
        WeekFilter.ItemsSource = new[] { new Option("-1", "작업 기간 보기"), new Option("-2", $"{viewedMonth:yyyy년 M월} 전체") };
        WeekFilter.SelectedIndex = showMonth ? 1 : 0;
        vm.DisplayMonth = viewedMonth;
        UpdateCalendarHint();
    }
    private void UpdateCalendarHint()
    {
        if (vm.Session?.Document.Calendar is not { } calendar) return;
        CalendarHint.Text = calendar.EditableUntil < calendar.EditableFrom
            ? "이번 주 작업일이 지났습니다. 1주 후 이상을 선택하세요."
            : $"{calendar.EditableFrom:yyyy/M/d}~{calendar.EditableUntil:M/d} 편집 · 기간 밖은 조회만";
    }
    private bool CalendarReady => vm.Session?.Document.Calendar is { } calendar && calendar.EditableFrom == Today &&
        calendar.EditableUntil == BoardDateRange.Friday(Today, calendar.WeeksAhead) &&
        WorkHorizon.SelectedItem is Option selected && selected.Id == calendar.WeeksAhead.ToString();
    private bool EditableSlot(string slotId) => CalendarReady && vm.Session?.Document.Calendar is { } calendar &&
        vm.Session.Document.Current.Slots.FirstOrDefault(x => x.Id == slotId) is { } slot && calendar.CanEdit(slot);

    private async void WorkHorizonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || busy || !IsInitialized || vm.Session is null || WorkHorizon.SelectedItem is not Option option) return;
        await RefreshWorkPeriod(int.Parse(option.Id), true);
    }
    private async Task RefreshWorkPeriod(int? requested = null, bool showWorkingPeriod = false)
    {
        if (busy || manualDraft is not null || lockSelectionMode || vm.Session is not { } session) return;
        var weeks = requested ?? session.Document.Calendar?.WeeksAhead ?? WorkPeriod.DefaultWeeksAhead;
        var showMonth = !showWorkingPeriod && WeekFilter.SelectedItem is Option { Id: "-2" };
        try
        {
            CancelInteraction(); SetBusy(true);
            await Task.Run(() => session.SetWorkPeriod(Today, weeks));
            if (!ReferenceEquals(vm.Session, session)) return;
            if (viewedMonth != MonthlyTimetable.MonthOf(Today) && showWorkingPeriod) viewedMonth = MonthlyTimetable.MonthOf(Today);
            refreshing = true;
            LoadCalendarControls(session, showMonth); vm.Refresh();
            refreshing = false;
            RefreshGrid();
            ActionHint.Text = WorkPeriod.EditableSlots(session.Document).Any()
                ? $"작업 기간을 {session.Document.Calendar!.EditableUntil:M/d}까지로 설정했습니다. 다음 달 날짜도 이어서 편집합니다."
                : "이번 주 금요일이 지났습니다. 작업 기간에서 1주 후 이상을 선택하세요.";
        }
        catch (Exception ex)
        {
            refreshing = true;
            WorkHorizon.SelectedIndex = session.Document.Calendar?.WeeksAhead ?? WorkPeriod.DefaultWeeksAhead;
            refreshing = false; Error(ex);
        }
        finally { refreshing = false; SetBusy(false); }
    }
    private async void WorkPeriodViewClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is null) return;
        if (vm.Session.Document.Calendar?.EditableFrom != Today) await RefreshWorkPeriod(showWorkingPeriod: true);
        else WeekFilter.SelectedIndex = 0;
    }
    private async void CurrentMonthViewClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is null) return;
        if (vm.Session.Document.Calendar?.EditableFrom != Today) await RefreshWorkPeriod();
        viewedMonth = MonthlyTimetable.MonthOf(Today);
        refreshing = true; LoadCalendarControls(vm.Session, true); vm.Refresh(); refreshing = false;
        RefreshGrid(); ActionHint.Text = "이번 달 전체 조회 · 오늘 이전과 작업 기간 밖의 수업은 조회만 가능합니다.";
    }
    private void DirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || refreshing || busy) return;
        if (manualDraft is { } draft)
        {
            var error = draft.SetDirection(Direction);
            if (error is not null) { SetDirectionChecks(draft.Direction); ActionHint.Text = error; return; }
            RefreshManualBoard(); return;
        }
        vm.CancelPreview(); UpdateWorkflowControls(); PaintPlan();
        ActionHint.Text = Direction == SwapDirection.SameDay ? "당일 안에서만 교체안을 찾습니다. 지정 목적은 유지합니다."
            : "선택한 작업 기간 안에서 당일 우선 → 가까운 이전·이후 날짜로 넓힙니다. 밀려나는 수업도 같은 제한을 따릅니다.";
    }
}
