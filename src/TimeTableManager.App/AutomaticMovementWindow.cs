using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

/// <summary>Refusals request another complete plan; they never apply a subset of a chain.</summary>
public sealed class AutomaticMovementWindow : Window
{
    private readonly List<(TeacherMovementReview Group, CheckBox Move, CheckBox? Run, CheckBox? Requested)> choices = [];
    public IReadOnlyList<string> DeniedMovementTeachers => choices.Where(x => x.Move.IsEnabled && x.Move.IsChecked != true).Select(x => x.Group.TeacherId).ToArray();
    public IReadOnlyList<string> DeniedConsecutiveTeachers => choices.Where(x => x.Run is not null && x.Run.IsChecked != true).Select(x => x.Group.TeacherId).ToArray();

    public AutomaticMovementWindow(ProjectSession session, IReadOnlyList<TeacherMovementReview> groups, bool placementFirst = false)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "AI 교체안 · 교사별 이동 허용"; Width = 720; MinWidth = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(320, Math.Min(740, SystemParameters.WorkArea.Height - 80));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var state = session.Document.Current;
        var slots = state.Slots.ToDictionary(x => x.Id);
        var classes = state.Classes.ToDictionary(x => x.Id, x => x.Name);
        string Position(string id) => session.Document.Calendar?.Label(slots[id]) ?? slots[id].Label;
        string Day(TimeSlot slot) => session.Document.Calendar is { } calendar
            ? $"{calendar.DateOf(slot):M/d} ({"월화수목금토일"[slot.Day - 1]})" : $"{slot.Week}주 {"월화수목금토일"[slot.Day - 1]}요일";
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new Border { Background = (Brush)FindResource("AquaBlue"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10),
            Child = new TextBlock { Text = "교사별로 이동을 허용하세요. 체크를 끄면 해당 교사의 추가 수업을 유지하고 대체안을 찾습니다.",
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap } });
        panel.Children.Add(new TextBlock { Text = "직접 선택한 수업은 이동 대상으로 유지합니다. 취소하면 시간표는 바뀌지 않습니다.",
            FontSize = 12, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 8) });
        var list = new StackPanel();
        var accept = new Button();
        void UpdateActions()
        {
            accept.IsEnabled = choices.All(x => x.Requested is null || x.Requested.IsChecked == true);
            accept.Content = DeniedMovementTeachers.Count > 0 || DeniedConsecutiveTeachers.Count > 0
                ? "선택 반영 · 대체안 찾기" : "선택 허용 · 교체안 보기";
        }
        foreach (var group in groups)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = group.Name + (group.OwnTeacher ? " · 선택한 교사" : ""), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var move = new CheckBox { Content = group.Additional.IsEmpty ? "직접 선택한 수업 이동" : group.OwnTeacher
                ? $"본인의 다른 수업 {group.Additional.Length}개 이동 허용" : $"수업 {group.Additional.Length}개 이동 허용",
                IsChecked = true, IsEnabled = !group.Additional.IsEmpty, Margin = new Thickness(0, 5, 0, 3) };
            body.Children.Add(move);
            CheckBox? requested = null;
            int changedRoots = group.Requested.Count(x => group.PreviouslyChanged.Contains(x.Before.Id));
            if (changedRoots > 0)
            {
                requested = new CheckBox { Content = $"직접 선택한 수업 {changedRoots}개의 재변경 허용", IsChecked = false,
                    Foreground = Brushes.DarkRed, Margin = new Thickness(0, 3, 0, 3) };
                body.Children.Add(requested);
            }
            var deltas = group.Requested.Concat(group.Additional).OrderBy(x => slots[x.Before.SlotId].Week)
                .ThenBy(x => slots[x.Before.SlotId].Day).ThenBy(x => slots[x.Before.SlotId].Period);
            foreach (var delta in deltas)
            {
                var flags = (group.Requested.Any(x => x.Before.Id == delta.Before.Id) ? "선택 수업 · " : "") +
                    (group.PreviouslyChanged.Contains(delta.Before.Id) ? "재변경 · " : "");
                body.Children.Add(new TextBlock { Text = $"{flags}{classes[delta.Before.ClassId]} · {delta.Before.Subject}  {Position(delta.Before.SlotId)} → {Position(delta.After.SlotId)}",
                    FontSize = 12, FontWeight = group.PreviouslyChanged.Contains(delta.Before.Id) ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = group.PreviouslyChanged.Contains(delta.Before.Id) ? Brushes.DarkRed : (Brush)FindResource("AquaInk"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 0) });
            }
            if (group.OriginStillOccupied) body.Children.Add(new TextBlock { Text = "주의: 출발 교시에 본인의 다른 수업이 들어오므로 그 시간의 공강은 생기지 않습니다.",
                Foreground = Brushes.SaddleBrown, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 5, 2, 0) });
            CheckBox? run = null;
            if (!group.Risks.IsEmpty)
            {
                run = new CheckBox { Content = "이번 안의 3연강 이상 허용", IsChecked = false, Foreground = Brushes.DarkRed, Margin = new Thickness(0, 7, 0, 2) };
                body.Children.Add(run);
                foreach (var risk in group.Risks) body.Children.Add(new TextBlock {
                    Text = $"{Day(risk.Day)} · {risk.FirstPeriod}~{risk.LastPeriod}교시 · {risk.Length}연강",
                    FontSize = 12, Foreground = Brushes.DarkRed, Margin = new Thickness(2, 0, 2, 0) });
            }
            choices.Add((group, move, run, requested));
            move.Checked += (_, _) => UpdateActions(); move.Unchecked += (_, _) => UpdateActions();
            if (run is not null) { run.Checked += (_, _) => UpdateActions(); run.Unchecked += (_, _) => UpdateActions(); }
            if (requested is not null) { requested.Checked += (_, _) => UpdateActions(); requested.Unchecked += (_, _) => UpdateActions(); }
            list.Children.Add(new Border { Child = body, Background = Brushes.White, BorderBrush = (Brush)FindResource("AquaEdge"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7) });
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(140, Math.Min(410, SystemParameters.WorkArea.Height - 300)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(new Expander { Header = "허용·재탐색 안내", Margin = new Thickness(0, 4, 0, 2), Content = new TextBlock {
            Text = "• ‘재변경’: 이미 이동·보강한 수업을 다시 변경\n• 거부는 이번 탐색에만 적용. 영구 잠금 아님\n• 대체안은 다시 확인하며, 교체안 일부만 적용하지 않음\n• 지정 목적·잠금·행사·부재·날짜 허용 조건 유지" +
                (placementFirst ? "\n• 배치 우선: 연강을 허용하면 적용 후 필요한 수업만 수동 조정" : ""),
            FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        accept.Click += (_, _) => DialogResult = true;
        actions.Children.Add(accept); actions.Children.Add(new Button { Content = "취소", IsCancel = true, IsDefault = true });
        panel.Children.Add(actions); UpdateActions();
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
