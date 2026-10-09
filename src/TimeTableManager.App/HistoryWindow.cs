using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public sealed class HistoryWindow : Window
{
    public HistoryWindow(ProjectDocument document)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "전체 변경 흐름 · " + document.Name; Width = 1240; Height = 720; MinWidth = 800; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var operations = ChangeHistory.Operations(document);
        var net = ChangeHistory.NetChanges(document);
        var panel = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var grouping = new ComboBox { ItemsSource = new[] { "학급별 흐름", "교사별 흐름" }, SelectedIndex = 0, Width = 135, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(grouping, Dock.Left); toolbar.Children.Add(grouping);
        toolbar.Children.Add(new TextBlock
        {
            Text = $"반영 중 {operations.Count(x => x.IsApplied)}건 · 되돌림 {operations.Count(x => !x.IsApplied)}건 · 원본 대비 {net.Length}개 수업 변경\n같은 번호의 변경 전·후 카드를 선으로 연결합니다. 카드를 가리키면 상세 정보를 표시합니다.",
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        var navigation = new ListBox { Width = 245, Margin = new Thickness(0, 0, 10, 0), DisplayMemberPath = "Label" };
        navigation.ItemsSource = new[] { new Option("all", "현재 전체 흐름\n기초 시간표 → 현재 배정") }.Concat(operations.Reverse().Select(x =>
            new Option(x.Number.ToString(), $"{x.Number}. {(x.IsApplied ? "반영 중" : "되돌림")} · {x.Operation.At.ToLocalTime():MM-dd HH:mm}\n{x.Operation.Description}"))).ToArray();
        DockPanel.SetDock(navigation, Dock.Left); panel.Children.Add(navigation);
        var main = new DockPanel();
        var caption = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(caption, Dock.Top); main.Children.Add(caption);
        var diagram = new ChangeDiagram(); main.Children.Add(diagram); panel.Children.Add(main); Content = panel;
        void Refresh()
        {
            if (navigation.SelectedItem is not Option selected) return;
            if (selected.Id == "all")
            {
                caption.Text = "현재 전체 흐름 · 기초 원본에서 현재 배정까지 (원위치로 돌아온 수업 제외)";
                diagram.Show(document, net, grouping.SelectedIndex == 1);
            }
            else
            {
                var entry = operations.Single(x => x.Number.ToString() == selected.Id);
                caption.Text = $"{entry.Number}. {entry.Operation.Description} · {(entry.IsApplied ? "현재 반영 중" : "되돌린 작업 — 현재 배정에 반영되지 않음")}";
                diagram.Show(document, entry.Operation.Changes, grouping.SelectedIndex == 1);
            }
        }
        navigation.SelectionChanged += (_, _) => Refresh(); grouping.SelectionChanged += (_, _) => Refresh();
        navigation.SelectedIndex = 0;
    }
}
