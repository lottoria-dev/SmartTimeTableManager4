using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

public sealed class ReviewWindow : Window
{
    public ReviewWindow(string title, string instruction, string detail, string accept, string? explanation = null)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = title; Width = 630; MinWidth = 460;
        SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(360, Math.Min(650, SystemParameters.WorkArea.Height - 80));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "확인 이유", Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(new Border { Background = (Brush)FindResource("AquaBlue"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold } });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            panel.Children.Add(new TextBlock { Text = "관련 수업·배치", Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
            panel.Children.Add(new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(9, 6, 9, 6), MaxHeight = 240, FontSize = 12 });
        }
        if (!string.IsNullOrWhiteSpace(explanation)) panel.Children.Add(new Expander { Header = "참고 설명", Margin = new Thickness(0, 10, 0, 0),
            Content = new TextBlock { Text = explanation, Foreground = Brushes.SlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 5, 5, 0) } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        var yes = new Button { Content = accept }; var no = new Button { Content = "취소", IsCancel = true, IsDefault = true };
        yes.Click += (_, _) => DialogResult = true;
        actions.Children.Add(yes); actions.Children.Add(no); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
