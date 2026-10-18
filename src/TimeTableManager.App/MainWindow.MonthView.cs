using System.Windows;
using TimeTableManager.Application;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private async void MonthClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is not { } session) return;
        var picker = new MonthWindow(viewedMonth) { Owner = this };
        if (picker.ShowDialog() != true) return;
        try
        {
            CancelInteraction(); SetBusy(true);
            await Task.Run(() => session.IncludeViewMonth(picker.Month));
            viewedMonth = picker.Month;
            refreshing = true; LoadCalendarControls(session, true); vm.Refresh(); refreshing = false;
            RefreshGrid();
            ActionHint.Text = $"{viewedMonth:yyyy년 M월} 전체 조회 · 작업 기간은 그대로입니다. 기간 밖의 수업은 조회만 가능합니다.";
        }
        catch (Exception ex) { Error(ex); }
        finally { refreshing = false; SetBusy(false); }
    }
}
