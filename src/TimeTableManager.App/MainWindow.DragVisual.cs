using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private Border? dragCard;
    private string? dragCardLessonId;
    private void ShowDragCard(string lessonId, Point point)
    {
        if (vm.Session is not { } session) return;
        ClearDragCard();
        dragCardLessonId = lessonId;
        var lesson = session.Document.Current.Lessons.Single(x => x.Id == lessonId);
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = lesson.Subject + " · " + session.Document.Current.Teachers.FirstOrDefault(x => x.Id == lesson.TeacherId)?.Name,
            FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        card.Children.Add(new TextBlock { Text = session.Document.Current.Classes.Single(x => x.Id == lesson.ClassId).Name + " · 이동 중",
            FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 0) });
        dragCard = new Border { Width = 170, Padding = new Thickness(9, 6, 9, 6), CornerRadius = new CornerRadius(7),
            Background = ThemeBrush("AquaGlass"), BorderBrush = ThemeBrush("AquaFocus"), BorderThickness = new Thickness(1.5),
            Opacity = 0.93, Child = card, Effect = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 4, Opacity = 0.3 } };
        DragCanvas.Children.Add(dragCard); MoveDragCard(point, true);
    }
    private void MoveDragCard(Point point, bool allowed)
    {
        if (dragCard is null) return;
        Canvas.SetLeft(dragCard, Math.Clamp(point.X + 16, 0, Math.Max(0, Workspace.ActualWidth - 176)));
        Canvas.SetTop(dragCard, Math.Clamp(point.Y + 18, 0, Math.Max(0, Workspace.ActualHeight - 64)));
        dragCard.BorderBrush = allowed ? ThemeBrush("AquaFocus") : Brushes.IndianRed;
        dragCard.Opacity = allowed ? 0.93 : 0.65;
    }
    private void ClearDragCard() { DragCanvas.Children.Clear(); dragCard = null; dragCardLessonId = null; }
    private void BoardPointerLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (!dragActive) ClearDragCard(); }
}
