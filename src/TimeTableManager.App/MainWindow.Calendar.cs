using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private void LoadCalendarControls(ProjectSession session)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var calendar = session.Document.Calendar ?? new ScheduleCalendar(ScheduleCalendar.MondayOf(today), today);
        session.SetCalendar(calendar);
        FirstWeekDate.SelectedDate = calendar.FirstWeekMonday.ToDateTime(TimeOnly.MinValue);
        WorkDate.SelectedDate = calendar.EditableFrom.ToDateTime(TimeOnly.MinValue);
        var weeks = session.Document.Current.Slots.Select(x => x.Week).Distinct().Order().ToArray();
        var monthly = session.Document.Monthly is not null;
        WeekFilter.ItemsSource = new[] { new Option("-1", "오늘~다음 주 금") }
            .Concat(monthly ? new[] { new Option("-2", "관리 월 전체"), new Option("-3", "관리 월＋인접 주"), new Option("0", "저장된 전체 기간") }
                : new[] { new Option("0", "전체 주차") })
            .Concat(weeks.Where(w => !monthly || MonthlyTimetable.WorkingSlots(session.Document).Any(x => x.Week == w))
                .Select(w => new Option(w.ToString(), $"{calendar.FirstWeekMonday.AddDays((w - 1) * 7):M/d} 시작 주"))).ToArray();
        WeekFilter.SelectedIndex = 0;
        FirstWeekDate.IsEnabled = !monthly;
        RepeatTwoWeeks.Visibility = monthly ? Visibility.Collapsed : Visibility.Visible;
        RepeatTwoWeeks.IsEnabled = !monthly && weeks.Length == 1 && weeks[0] == 1;
        UpdateCalendarHint();
    }
    private void UpdateCalendarHint()
    {
        if (vm.Session?.Document.Calendar is not { } calendar) return;
        var last = vm.Session.Document.Current.Slots.Max(x => calendar.DateOf(x));
        CalendarHint.Text = vm.Session.Document.Monthly is { } monthly
            ? $"관리 {monthly.Month:yyyy년 M월} · 편집 허용 시작일 {calendar.EditableFrom:M/d} · 이전 날짜는 교체 제외"
            : $"1주 월요일 {calendar.FirstWeekMonday:yyyy/M/d} · 자료 마지막 날 {last:M/d} · 편집 허용 시작일 이전은 교체 제외";
    }
    private bool CalendarReady => FirstWeekDate.SelectedDate is { } first && WorkDate.SelectedDate is { } cutoff &&
        vm.Session?.Document.Calendar is { } calendar && calendar.FirstWeekMonday == ScheduleCalendar.MondayOf(DateOnly.FromDateTime(first)) &&
        calendar.EditableFrom == DateOnly.FromDateTime(cutoff);
    private bool EditableSlot(string slotId) => CalendarReady && vm.Session?.Document.Calendar is { } calendar &&
        vm.Session.Document.Current.Slots.FirstOrDefault(x => x.Id == slotId) is { } slot && calendar.CanEdit(slot);
    private void CalendarChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshing || busy || !IsInitialized || vm.Session is null) return;
        if (FirstWeekDate.SelectedDate is not { } first || WorkDate.SelectedDate is not { } cutoff)
        {
            vm.CancelPreview(); PaintPlan();
            ActionHint.Text = "1주 시작일과 편집 허용 시작일을 모두 선택하세요.";
            return;
        }
        try
        {
            var calendar = new ScheduleCalendar(ScheduleCalendar.MondayOf(DateOnly.FromDateTime(first)), DateOnly.FromDateTime(cutoff));
            vm.Session.SetCalendar(calendar);
            refreshing = true;
            FirstWeekDate.SelectedDate = calendar.FirstWeekMonday.ToDateTime(TimeOnly.MinValue);
            refreshing = false;
            vm.Refresh(); RefreshGrid(); UpdateCalendarHint();
        }
        catch (Exception ex)
        {
            refreshing = true;
            if (vm.Session.Document.Calendar is { } restored)
            { FirstWeekDate.SelectedDate = restored.FirstWeekMonday.ToDateTime(TimeOnly.MinValue); WorkDate.SelectedDate = restored.EditableFrom.ToDateTime(TimeOnly.MinValue); }
            refreshing = false; Error(ex);
        }
    }
    private void TodayClick(object sender, RoutedEventArgs e) => WorkDate.SelectedDate = DateTime.Today;
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
        ActionHint.Text = Direction == SwapDirection.SameDay ? "당일 안에서만 교체안을 찾습니다. 지정 목적은 유지합니다." : "당일 우선 → 같은 달 → 인접 주 순서로 허용 날짜를 넓힙니다. 밀려나는 수업도 같은 제한을 따릅니다.";
    }
    private async void RepeatTwoWeeksClick(object sender, RoutedEventArgs e)
    {
        if (busy || vm.Session is null) return;
        try
        {
            var copy = ProjectRepetition.CreateTwoWeekCopy(vm.Session.Document);
            if (!await MayAbandon()) return;
            projectFile?.Dispose(); projectFile = null;
            Attach(new ProjectSession(copy));
            ActionHint.Text = "2주 반복본을 만들었습니다. 1주 변경 이력은 유지하며 2주는 기초 원본을 반복합니다. 새 파일로 저장하세요.";
        }
        catch (Exception ex) { Error(ex); }
    }
}
