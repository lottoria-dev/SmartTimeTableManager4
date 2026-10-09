using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using TimeTableManager.Application;
using TimeTableManager.Infrastructure;

namespace TimeTableManager.App;

public sealed class MonthlyStatisticsWindow : Window
{
    public MonthlyStatisticsWindow(MonthlyReport report)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = $"{report.Month:yyyy년 M월} 결보강·이동 통계"; Width = 1080; Height = 650; MinWidth = 760; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "확정된 최종 배정 기준 · 잠금·취소·원상복귀·미배치 초안은 변동 건수에서 제외", FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "담당 교체 결강·보강은 최종 수업 날짜의 월에 집계합니다.\n기초 요일 적용은 학교 계획으로 기초배정에 반영하며 변동 건수에서 제외합니다.\n실제 수업 실시 여부를 확인한 통계는 아닙니다. 행사 제외는 교체 제한이며 수업을 삭제하지 않습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (bool details in new[] { false, true })
        {
            var button = new Button { Content = details ? "변동 상세 내보내기" : "교사별 통계 내보내기" };
            button.Click += (_, _) =>
            {
                var picker = new SaveFileDialog { Filter = "Excel 통합 문서|*.xlsx|CSV 파일|*.csv", DefaultExt = ".xlsx", AddExtension = true, OverwritePrompt = false,
                    FileName = TimestampedFileNames.Stamp($"{report.Month:yyyy-MM}_{(details ? "변동상세" : "결보강통계")}.xlsx", DateTime.Now) };
                if (picker.ShowDialog(this) != true) return;
                try { MonthlyReportExporter.Write(TimestampedFileNames.Next(Path.ChangeExtension(picker.FileName, picker.FilterIndex == 2 ? ".csv" : ".xlsx"), DateTime.Now), report, details); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            buttons.Children.Add(button);
        }
        top.Children.Add(buttons); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var tabs = new TabControl();
        DataGrid Table(System.Collections.IEnumerable rows, params (string Header, string Field)[] columns)
        {
            var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, FrozenColumnCount = 1 };
            foreach (var col in columns) grid.Columns.Add(new DataGridTextColumn { Header = col.Header, Binding = new Binding(col.Field), Width = DataGridLength.Auto, MinWidth = 80 });
            return grid;
        }
        tabs.Items.Add(new TabItem { Header = "교사별 집계", Content = Table(report.Teachers,
            ("교사", "Teacher"), ("기초배정", "Planned"),
            ("담당교체 결강", "CoverOut"), ("담당교체 보강", "CoverIn"), ("최종배정", "Assigned"), ("증감", "Difference")) });
        tabs.Items.Add(new TabItem { Header = "월 경계·변동 상세", Content = Table(report.Changes,
            ("학급", "Class"), ("과목", "Subject"), ("원래교사", "OriginalTeacher"), ("현재교사", "Teacher"),
            ("기초일시", "Before"), ("현재일시", "After"), ("월 경계", "Boundary")) });
        panel.Children.Add(tabs); Content = panel;
    }
}
