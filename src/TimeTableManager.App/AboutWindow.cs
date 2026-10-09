using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeTableManager.App;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "프로그램 정보"; Width = 540; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(28) };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/TimeTableManager4.png")),
            Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,0,0,12) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        content.Children.Add(logo);
        content.Children.Add(new TextBlock { Text = "Smart Timetable Manager 4", FontSize = 23, FontWeight = FontWeights.Bold });
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "4";
        content.Children.Add(new TextBlock { Text = "버전 " + version, Margin = new Thickness(0,6,0,18), Foreground = (Brush)FindResource("AquaMuted") });
        content.Children.Add(new TextBlock { Text = "MathTime에서 개발한 한국 학교용 수업 교체·보강 프로그램입니다.\n\n조회 · 맞교환 · 보강 · 수동 교체 · AI 자동\n\n자동 교체는 PC 안에서 탐색합니다. 외부 AI API에 시간표를 전송하지 않습니다.", TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
        content.Children.Add(new Border { Background = (Brush)FindResource("AquaGlass"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0,18,0,18),
            Child = new TextBlock { Text = "개발: MathTime · mathtime.kr\nCopyright (c) 2026 lottoria.\n교육기관 교직원의 학급 운영·업무 목적 무료 사용.\n상업적 영리 목적 사용 금지.\n외부 구성요소의 권리·라이선스는 각 원저작자에게 있습니다. 자세한 고지는 소스의 THIRD_PARTY_NOTICES.md에 있습니다.", TextWrapping = TextWrapping.Wrap, FontSize = 11, LineHeight = 19 } });
        var close = new Button { Content = "닫기", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Background = (Brush)FindResource("AquaBlueStrong"), IsDefault = true };
        close.Click += (_, _) => Close(); content.Children.Add(close); Content = content;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }
}
