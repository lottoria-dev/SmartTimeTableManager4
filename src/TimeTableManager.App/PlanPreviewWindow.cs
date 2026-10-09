using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

/// <summary>Modeless preview: opening or closing it never changes board layout or the schedule.</summary>
public sealed class PlanPreviewWindow : Window
{
    private readonly ChangeDiagram diagram = new();
    private readonly ComboBox grouping = new() { ItemsSource = new[] { "학급별 흐름", "교사별 흐름" }, SelectedIndex = 0, Width = 125 };
    private readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 0, 2) };
    private readonly Button apply = new() { Content = "변경 적용", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(4) };
    private ProjectDocument? document;
    private ImmutableArray<LessonDelta> changes = [];
    public PlanPreviewWindow(Action onApply, Action onCancel)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "반영할 교체안 · 시간표 위치는 유지됩니다";
        Width = 1000; Height = 470; MinWidth = 650; MinHeight = 300;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(grouping, Dock.Left); header.Children.Add(grouping); header.Children.Add(caption);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "작업 취소", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6) };
        var close = new Button { Content = "창 닫기", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6) };
        apply.Background = (System.Windows.Media.Brush)FindResource("AquaMintStrong");
        apply.Click += (_, _) => onApply(); cancel.Click += (_, _) => onCancel(); close.Click += (_, _) => Close();
        buttons.Children.Add(apply); buttons.Children.Add(cancel); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(diagram); Content = panel;
        grouping.SelectionChanged += (_, _) => Draw();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    public void UpdatePlan(ProjectDocument current, ImmutableArray<LessonDelta> deltas, string text, bool canApply)
    {
        document = current; changes = deltas; caption.Text = text; apply.IsEnabled = canApply; Draw();
    }
    public void SetCanApply(bool value) => apply.IsEnabled = value;
    private void Draw() { if (document is not null) diagram.Show(document, changes, grouping.SelectedIndex == 1); }
}
