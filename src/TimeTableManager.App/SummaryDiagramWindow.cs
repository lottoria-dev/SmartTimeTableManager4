using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public sealed class SummaryDiagramWindow : Window
{
    public SummaryDiagramWindow(ProjectDocument document)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "변경 내역 종합 다이어그램 · " + document.Name;
        Width = 1320; Height = 760; MinWidth = 800; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel();
        var scope = new ComboBox { ItemsSource = new[] { "최초 시간표 → 현재 배정", "적용된 전체 과정 · 순서대로" }, SelectedIndex = 0, Width = 235, Margin = new Thickness(0, 0, 10, 8) };
        var grouping = new ComboBox { ItemsSource = new[] { "학급별", "교사별" }, SelectedIndex = 0, Width = 100, Margin = new Thickness(0, 0, 10, 8) };
        toolbar.Children.Add(scope); toolbar.Children.Add(grouping);
        toolbar.Children.Add(new TextBlock { Text = "모든 변경을 한 화면에서 스크롤하여 확인 · 잠금 이력 제외", VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        var diagram = new ChangeDiagram(); panel.Children.Add(diagram); Content = panel;
        void Refresh()
        {
            if (scope.SelectedIndex == 0) diagram.Show(document, TimeTableManager.Application.ChangeHistory.NetChanges(document), grouping.SelectedIndex == 1);
            else diagram.ShowAppliedOperations(document, grouping.SelectedIndex == 1);
        }
        scope.SelectionChanged += (_, _) => Refresh(); grouping.SelectionChanged += (_, _) => Refresh(); Refresh();
    }
}
