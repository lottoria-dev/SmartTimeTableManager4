using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

/// <summary>Time columns, before/after cards and connecting lines; no document mutations.</summary>
public sealed class ChangeDiagram : UserControl
{
    private readonly StackPanel groups = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer scroll;
    private static readonly string[] Colors = ["#C2410C", "#2563EB", "#7C3AED", "#15803D", "#BE123C", "#A16207"];
    public ChangeDiagram()
    {
        Content = scroll = new ScrollViewer
        {
            Content = groups, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false
        };
    }
    public void Show(ProjectDocument document, IEnumerable<LessonDelta> changes, bool byTeacher = false)
    {
        groups.Children.Clear(); scroll.ScrollToLeftEnd();
        Append(document, changes, byTeacher);
    }
    public void ShowAppliedOperations(ProjectDocument document, bool byTeacher = false)
    {
        groups.Children.Clear(); scroll.ScrollToLeftEnd();
        var applied = ChangeHistory.Operations(document).Where(x => x.IsApplied).ToArray();
        if (applied.Length == 0) { Append(document, [], byTeacher); return; }
        foreach (var entry in applied)
        {
            groups.Children.Add(new TextBlock
            {
                Text = $"{entry.Number}. {entry.Operation.At.ToLocalTime():M/d HH:mm} · {entry.Operation.Description}",
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 12, 0, 6), Foreground = Brushes.DarkSlateBlue
            });
            Append(document, entry.Operation.Changes, byTeacher);
        }
    }
    private void Append(ProjectDocument document, IEnumerable<LessonDelta> changes, bool byTeacher)
    {
        var deltas = changes.Where(ChangeHistory.IsLessonChange).ToArray();
        if (deltas.Length == 0)
        {
            groups.Children.Add(new TextBlock { Text = "표시할 수업 이동·교사 변경이 없습니다.", Margin = new Thickness(8), Foreground = Brushes.SlateGray });
            return;
        }
        var state = document.Current;
        var slots = state.Slots.ToDictionary(x => x.Id);
        var classes = state.Classes.ToDictionary(x => x.Id, x => x.Name);
        var duplicates = state.Teachers.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        var teachers = state.Teachers.ToDictionary(x => x.Id, x => duplicates.Contains(x.Name) ? $"{x.Name} [{x.Id}]" : x.Name);
        string Teacher(string? id) => id is null ? "미배정" : teachers[id];
        string Slot(string id) => document.Calendar?.Label(slots[id]) ?? slots[id].Label;
        foreach (var group in deltas.GroupBy(d => byTeacher ? d.Before.TeacherId : d.Before.ClassId))
        {
            var ordered = group.OrderBy(d => slots[d.Before.SlotId].Week).ThenBy(d => slots[d.Before.SlotId].Day)
                .ThenBy(d => slots[d.Before.SlotId].Period).ThenBy(d => d.Before.Id).ToArray();
            var columns = ordered.SelectMany(d => new[] { d.Before.SlotId, d.After.SlotId }).Distinct()
                .OrderBy(id => slots[id].Week).ThenBy(id => slots[id].Day).ThenBy(id => slots[id].Period).ToArray();
            var indices = columns.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            const double left = 56, step = 230, cardWidth = 218, cardHeight = 46, beforeY = 38;
            int beforeDepth = ordered.GroupBy(d => d.Before.SlotId).Max(g => g.Count());
            int afterDepth = ordered.GroupBy(d => d.After.SlotId).Max(g => g.Count());
            double afterY = beforeY + beforeDepth * 50 + 32;
            var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, Width = left + (columns.Length - 1) * step + cardWidth, Height = afterY + afterDepth * 50 + 4, Background = Brushes.White };
            void Place(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
            void Label(string text, double x, double y, double width, bool bold = false) => Place(new TextBlock
            {
                Text = text, Width = width, FontSize = 11, Foreground = Brushes.SlateGray,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis
            }, x, y);
            Label((byTeacher ? Teacher(group.Key) : classes[group.Key!]) + $" · {ordered.Length}개 수업 · 같은 번호의 변경 전·후 연결선", 0, 0, canvas.Width, true);
            Label("변경 전", 2, beforeY + 8, 52); Label("변경 후", 2, afterY + 8, 52);
            for (int c = 0; c < columns.Length; c++) Label(Slot(columns[c]), left + c * step, 19, cardWidth, true);
            var beforeCounts = new Dictionary<string, int>(); var afterCounts = new Dictionary<string, int>();
            for (int i = 0; i < ordered.Length; i++)
            {
                var delta = ordered[i];
                var cover = !delta.Added && !delta.Removed && delta.Before.Source == delta.After.Source && delta.Before.TeacherId != delta.After.TeacherId;
                var color = (Brush)new BrushConverter().ConvertFromString(cover ? "#0891B2" : Colors[i % Colors.Length])!;
                color.Freeze();
                double x1 = left + indices[delta.Before.SlotId] * step, x2 = left + indices[delta.After.SlotId] * step;
                double y1 = beforeY + beforeCounts.GetValueOrDefault(delta.Before.SlotId) * 50;
                double y2 = afterY + afterCounts.GetValueOrDefault(delta.After.SlotId) * 50;
                beforeCounts[delta.Before.SlotId] = beforeCounts.GetValueOrDefault(delta.Before.SlotId) + 1;
                afterCounts[delta.After.SlotId] = afterCounts.GetValueOrDefault(delta.After.SlotId) + 1;
                var beforeDetail = delta.Added ? "배정 없음" : $"{Slot(delta.Before.SlotId)} · {delta.Before.Subject} · {Teacher(delta.Before.TeacherId)}";
                var afterDetail = delta.Removed ? "요일 변경으로 제외" : $"{Slot(delta.After.SlotId)} · {delta.After.Subject} · {Teacher(delta.After.TeacherId)}";
                var detail = $"{classes[delta.Before.ClassId]}\n변경 전: {beforeDetail}\n변경 후: {afterDetail}";
                double offset = (i % 5 - 2) * 5;
                var start = new Point(x1 + cardWidth / 2 + offset, y1 + cardHeight);
                var end = new Point(x2 + cardWidth / 2 + offset, y2);
                double bend = (start.Y + end.Y) / 2;
                var geometry = new PathGeometry([new PathFigure(start,
                    [new BezierSegment(new Point(start.X, bend), new Point(end.X, bend), end, true)], false)]);
                Place(new System.Windows.Shapes.Path { Data = geometry, Stroke = color, StrokeThickness = 2.5, ToolTip = detail }, 0, 0);
                Border Card(LessonInstance lesson, bool after)
                {
                    var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    lines.Children.Add(new TextBlock
                    {
                        Text = !after && delta.Added ? $"{i + 1}. 추가 전 · 빈칸" : after && delta.Removed ? $"{i + 1}. 요일 변경으로 제외" : $"{i + 1}. {lesson.Subject} · {Teacher(lesson.TeacherId)}", FontSize = 12,
                        FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    lines.Children.Add(new TextBlock
                    {
                        Text = classes[lesson.ClassId] + (cover ? (after ? " · 보강 교사" : " · 변경 전 교사") : delta.Before.Source != delta.After.Source ? " · 기초 요일 적용" : ""),
                        FontSize = 11, Foreground = Brushes.SlateGray
                    });
                    return new Border
                    {
                        Width = cardWidth, Height = cardHeight, BorderBrush = color, BorderThickness = new Thickness(2, 1, 1, 1),
                        CornerRadius = new CornerRadius(3), Background = after ? (cover ? Brushes.LightCyan : Brushes.AliceBlue) : Brushes.White,
                        Padding = new Thickness(6, 3, 4, 3), ToolTip = detail, Child = lines
                    };
                }
                var beforeCard = Card(delta.Before, false); var afterCard = Card(delta.After, true);
                Panel.SetZIndex(beforeCard, 1); Panel.SetZIndex(afterCard, 1);
                Place(beforeCard, x1, y1); Place(afterCard, x2, y2);
            }
            groups.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = canvas, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 4), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }
}
