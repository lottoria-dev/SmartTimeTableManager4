using System.Windows;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private async void MonthClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is not { } session) return;
        var picker = new MonthWindow(session.Document.Monthly?.Month ?? session.Document.Calendar?.EditableFrom ?? DateOnly.FromDateTime(DateTime.Today)) { Owner = this };
        if (picker.ShowDialog() != true) return;
        try
        {
            CancelInteraction(); SetBusy(true);
            var next = await Task.Run(() => new ProjectSession(MonthlyTimetable.EnsureMonth(session.Document, picker.Month)));
            Attach(next);
            ActionHint.Text = $"{picker.Month:yyyy년 M월} 관리 · 기존 날짜의 변경을 이어받았습니다. 다른 달 이동은 해당 월 통계에도 반영됩니다. 편집 허용 시작일을 확인하세요.";
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    private async void MonthlyStatisticsClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is not { } session) return;
        if (session.Document.Monthly is not { } monthly) { ActionHint.Text = "파일 → 관리 월 선택에서 월 단위 관리를 시작하세요."; return; }
        try
        {
            SetBusy(true);
            var report = await Task.Run(() => MonthlyStatistics.Create(session.Document, monthly.Month));
            SetBusy(false);
            new MonthlyStatisticsWindow(report) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    private string MonthWarning(ChangePlan plan) => vm.Session is { } session && MonthlyTimetable.CrossesMonth(session.Document, plan)
        ? "\n\n월 경계를 넘는 이동입니다. 가급적 같은 달 안에서 교체하세요. 출발·도착 월의 통계가 함께 바뀝니다." : "";
}
