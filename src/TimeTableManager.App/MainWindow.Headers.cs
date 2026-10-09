using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private ScrollViewer? boardScroll;
    private string? headerProject;
    private DateOnly? headerMonday;
    private List<string> headerSlots = [];
    private double[] headerWidths = [];
    private double headerOffset = double.NaN;
    private readonly List<(Border Border, double Left)> dateBands = [];
    private static readonly Brush DateDividerBrush = Tint("#66778A");
    private const double DateDividerWidth = 2;
    private double boundaryHeight = -1, boundaryWidth = -1;
    private static ScrollViewer? FindGridScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindGridScroll(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
    private void UpdateDateHeaders()
    {
        if (DateHeaderCanvas is null || TimetableGrid.Columns.Count != visibleSlots.Count + 1 || vm.Session?.Document.Calendar is not { } calendar) return;
        var columns = TimetableGrid.Columns;
        // LayoutUpdated covers user column resizing, DPI/zoom and scrollbar changes; unchanged geometry is a no-op.
        bool rebuild = headerProject != vm.Session.Document.ProjectId || headerMonday != calendar.FirstWeekMonday ||
            headerSlots.Count != visibleSlots.Count || headerWidths.Length != columns.Count;
        if (!rebuild)
            for (int i = 0; i < columns.Count; i++)
                if (Math.Abs(headerWidths[i] - columns[i].ActualWidth) > 0.1 || i > 0 && headerSlots[i - 1] != visibleSlots[i - 1].Id) { rebuild = true; break; }
        if (rebuild)
        {
            headerProject = vm.Session.Document.ProjectId; headerMonday = calendar.FirstWeekMonday;
            headerSlots = visibleSlots.Select(x => x.Id).ToList();
            headerWidths = columns.Select(x => x.ActualWidth).ToArray();
            DateNameColumn.Width = new GridLength(headerWidths[0]);
            DateHeaderCanvas.Children.Clear(); dateBands.Clear();
            double left = 0; int column = 1;
            foreach (var day in visibleSlots.GroupBy(calendar.DateOf))
            {
                double width = day.Sum(_ => headerWidths[column++]);
                var band = new Border
                {
                    Width = width, Height = 26, Background = Brushes.White,
                    BorderBrush = Tint("#DFE6EE"), BorderThickness = new Thickness(0, 0, 1, 1),
                    ToolTip = "우클릭: 이 날짜의 학년 행사 제외 · 기초 요일 적용",
                    Child = new TextBlock { Text = $"{day.Key:M/d} ({"월화수목금토일"[day.First().Day - 1]})", FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AquaInk"),
                        TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                };
                var date = day.Key;
                // A single continuous seam joins date and period headers; timetable weekday fills remain unchanged.
                if (left > 0)
                {
                    var seam = new Border { Background = DateDividerBrush, Width = DateDividerWidth, Height = 26, IsHitTestVisible = false };
                    Panel.SetZIndex(seam, 1);
                    dateBands.Add((seam, left)); DateHeaderCanvas.Children.Add(seam);
                }
                band.MouseRightButtonUp += (_, e) => { OpenDateContext(date, band); e.Handled = true; };
                dateBands.Add((band, left)); DateHeaderCanvas.Children.Add(band); left += width;
            }
        }
        // The name column is frozen. Only the merged date bands follow horizontal scrolling.
        double offset = boardScroll?.HorizontalOffset ?? 0;
        bool moved = rebuild || Math.Abs(headerOffset - offset) > 0.1 || double.IsNaN(headerOffset);
        if (moved)
        {
            headerOffset = offset;
            foreach (var band in dateBands) Canvas.SetLeft(band.Border, band.Left - offset);
        }
        double gutter = boardScroll?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
        if (Math.Abs(DateScrollGutter.Width.Value - gutter) > 0.1) DateScrollGutter.Width = new GridLength(gutter);
        double height = Math.Max(0, TimetableGrid.ActualHeight - (boardScroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? SystemParameters.HorizontalScrollBarHeight : 0));
        double viewportWidth = Math.Max(0, TimetableGrid.ActualWidth - gutter);
        if (moved || Math.Abs(boundaryHeight - height) > 0.1 || Math.Abs(boundaryWidth - viewportWidth) > 0.1)
        {
            boundaryHeight = height; boundaryWidth = viewportWidth;
            BoundaryCanvas.Children.Clear();
            foreach (var left in dateBands.Select(x => x.Left).Where(x => x > 0).Distinct())
            {
                double x = headerWidths[0] + left - offset;
                if (x < headerWidths[0] || x >= viewportWidth) continue;
                // Draw above horizontal row lines, rather than a border on each individual cell.
                var line = new Border { Background = DateDividerBrush, Width = DateDividerWidth, Height = height };
                Canvas.SetLeft(line, x); BoundaryCanvas.Children.Add(line);
            }
        }
    }
}
