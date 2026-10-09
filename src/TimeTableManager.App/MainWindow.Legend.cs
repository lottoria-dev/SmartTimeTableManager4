using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private (WorkMode Mode, bool Locks, bool Hints, bool ClassView)? drawnLegend;
    private void UpdateLegend()
    {
        if (!IsInitialized || ColorLegend is null) return;
        var state = (workMode, lockSelectionMode, ShowHints.IsChecked, ViewMode.SelectedIndex == 1);
        if (drawnLegend == state) return;
        drawnLegend = state; ColorLegend.Children.Clear();
        void Swatch(string label, Brush color)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
            item.Children.Add(new Border { Width = 17, Height = 13, Background = color, BorderBrush = Brushes.SlateGray,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = label, Foreground = Ink, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            ColorLegend.Children.Add(item);
        }
        Swatch(lockSelectionMode ? "다중 잠금" : "선택 수업", SelectedFill);
        if (workMode == WorkMode.Browse || ViewMode.SelectedIndex == 1) Swatch("같은 교사", TeacherFill);
        if (workMode == WorkMode.Swap) Swatch("맞교환 가능", SwapFill);
        if (workMode == WorkMode.Manual && ShowHints.IsChecked)
        {
            Swatch("배치 추천", RecommendationFill); Swatch("배치 가능", PossibleFill); Swatch("배치 불가", UnavailableFill);
        }
        if (workMode is WorkMode.Manual or WorkMode.Auto) Swatch("교체안", PlanFill);
        if (workMode == WorkMode.Auto) { Swatch("목적 교시", TargetFill); Swatch("검증 불가", InvalidFill); }
        Swatch("변경된 수업 *", ChangedFill); Swatch("보강 · 보", CoverFill); Swatch("검색", SearchFill);
        ColorLegend.Children.Add(new TextBlock
        {
            Text = workMode == WorkMode.Manual ? (ShowHints.IsChecked ? "! 3연강 주의 · 직전 자리 추천 제외 · 자물쇠: 잠금 · 흐린 글씨: 행사 제외" : "추천 음영 꺼짐 · 자물쇠: 잠금 · 흐린 글씨: 행사 제외")
                : "자물쇠: 잠금 · 흐린 ‘변경’: 이동된 자리 · 흐린 수업: 행사 제외",
            FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 2)
        });
    }
}
