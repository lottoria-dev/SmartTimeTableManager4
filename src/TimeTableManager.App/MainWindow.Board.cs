using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private ObservableCollection<BoardRow> boardRows = [];
    private List<TimeSlot> visibleSlots = [];
    private string? renderedProjectId;
    private DateOnly? renderedMonday;
    private bool renderedTeachers;
    private readonly Dictionary<(int Index, int Edge), DataTemplate> cellTemplates = [];
    private BoardCell? targetCell, pressedCell;
    private Point pressedPoint;
    private bool dragActive;
    private CancellationTokenSource? searchCancellation;
    private const string DragFormat = "TimeTableManager.InternalLesson";
    private sealed record LessonDrag(Guid Revision, string LessonId, string Ticket);
    private LessonDrag? activeDrag;
    private readonly DispatcherTimer searchTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private string appliedQuery = "";
    private static Brush Tint(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    private static readonly Brush RecommendationFill = Tint("#15803D"), PossibleFill = Tint("#D1FAE5"), UnavailableFill = Tint("#E2E8F0");
    private static readonly Brush SelectedFill = Tint("#2563EB"), TeacherFill = Tint("#C4B5FD"),
        TargetFill = Tint("#15803D"), InvalidFill = Tint("#B91C1C"), PlanFill = Tint("#FB923C"),
        ChangedFill = Tint("#FED7AA"), CoverFill = Tint("#67E8F9"), SearchFill = Tint("#FDE047"), SwapFill = Tint("#34D399"), Ink = Tint("#0F172A");

    private static readonly Brush[] WeekdayFills = [Tint("#DBEAFE"), Tint("#DCFCE7"), Tint("#FEF3C7"), Tint("#FCE7F3"), Tint("#EDE9FE"), Tint("#FFEDD5"), Tint("#CFFAFE")];
    private static Brush WeekdayFill(int day) => WeekdayFills[Math.Clamp(day - 1, 0, 6)];

    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!refreshing && IsInitialized) RefreshGrid(manualDraft is not null); }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (refreshing || !IsInitialized) return;
        searchTimer.Stop();
        searchTimer.Start();
    }
    private void ApplySearch()
    {
        appliedQuery = SearchBox.Text.Trim();
        foreach (var row in boardRows)
        foreach (var cell in row.Cells)
            cell.SearchMatch = appliedQuery.Length == 0 || cell.SearchText.Contains(appliedQuery, StringComparison.OrdinalIgnoreCase);
        PaintPlan(); // No rebuilding columns, templates, selection, or scroll position while typing.
    }

    private void RefreshGrid(bool keepInteraction = false)
    {
        if (vm.Session is null || refreshing) return;
        refreshing = true;
        try
        {
            if (!keepInteraction)
            {
                vm.Select(null); targetCell = null; fixedTargetSlotId = null; pressedCell = null;
                boardAction = BoardAction.Normal; swapCandidates.Clear();
            }
            var s = DisplayState!;
            var week = WeekFilter.SelectedItem is Option w ? int.Parse(w.Id) : -1;
            var calendar = vm.Session.Document.Calendar!;
            var day = DayFilter.SelectedItem is Option d ? int.Parse(d.Id) : 0;
            bool teachers = ViewMode.SelectedIndex == 0;
            var periods = DayTemplatePlanner.PeriodCounts(vm.Session.Document);
            var nextSlots = s.Slots.Where(x => (week == -2 ? MonthlyTimetable.InMonth(calendar.DateOf(x), viewedMonth) : calendar.CanEdit(x))
                    && x.Day <= 5 && x.Period <= periods.GetValueOrDefault(calendar.DateOf(x)) && (day == 0 || x.Day == day))
                .OrderBy(x => x.Week).ThenBy(x => x.Day).ThenBy(x => x.Period).ToList();
            bool sameColumns = renderedProjectId == vm.Session.Document.ProjectId && renderedTeachers == teachers &&
                renderedMonday == calendar.FirstWeekMonday && visibleSlots.SequenceEqual(nextSlots);
            visibleSlots = nextSlots;
            var names = vm.Teachers.ToDictionary(x => x.Id, x => x.Label);
            var classNames = s.Classes.ToDictionary(x => x.Id, x => x.Name);
            var baseline = vm.Session.Document.Baseline.Lessons.ToDictionary(x => x.Id);
            var pattern = DayTemplatePlanner.OriginalPattern(vm.Session.Document).Lessons.ToDictionary(x => x.Id);
            var school = new SchoolDayRules(vm.Session.Document);
            var netChanges = ChangeHistory.NetChanges(vm.Session.Document);
            string RowKey(LessonInstance lesson) => teachers ? lesson.TeacherId ?? "unassigned:" + lesson.ClassId : lesson.ClassId;
            var changedPositions = netChanges.SelectMany(x => new[] { x.Before, x.After }).Select(x => (RowKey(x), x.SlotId)).ToHashSet();
            var departed = netChanges.Where(x => !x.Added).GroupBy(x => (RowKey(x.Before), x.Before.SlotId)).ToDictionary(g => g.Key, g => g.First());
            var rowOptions = teachers ? vm.Teachers.ToList() : s.Classes.Select(x => new Option(x.Id, x.Name)).ToList();
            if (teachers) rowOptions.AddRange(s.Lessons.Concat(workMode == WorkMode.Browse ? netChanges.Select(x => x.Before) : Enumerable.Empty<LessonInstance>()).Where(x => x.TeacherId is null).Select(x => x.ClassId).Distinct()
                .Select(id => new Option("unassigned:" + id, classNames[id] + " 미배정")));
            if (workMode == WorkMode.Browse && ChangedRowsOnly.IsChecked == true)
            {
                var changedRows = ChangeHistory.ChangedRows(vm.Session.Document, visibleSlots.Select(x => x.Id).ToHashSet(), teachers);
                rowOptions = rowOptions.Where(x => changedRows.Contains(x.Id)).ToList();
            }
            if (teachers) rowOptions = OrderTeacherRows(rowOptions);
            var occupancy = s.Lessons.ToDictionary(x => (RowKey(x), x.SlotId));
            var activeCard = manualDraft?.ActiveId is { } activeId ? vm.Session.Document.Current.Lessons.Single(x => x.Id == activeId) : null;

            var displacements = activeCard is null ? new Dictionary<string, IReadOnlyList<LessonInstance>>() :
                visibleSlots.ToDictionary(x => x.Id, x => manualDraft!.DisplacedAt(x.Id));
            var slotLabels = s.Slots.ToDictionary(x => x.Id, calendar.Label);
            var nextRows = rowOptions.Select(row => new BoardRow(row.Id, row.Label, visibleSlots.Select(slot =>
            {
                occupancy.TryGetValue((row.Id, slot.Id), out var lesson);
                var teacher = lesson?.TeacherId is { } tid ? names[tid] : "미배정";
                var cls = lesson is null ? "" : classNames[lesson.ClassId];
                var past = !calendar.CanEdit(slot);
                var old = lesson is null ? null : baseline.GetValueOrDefault(lesson.Id);
                var changed = changedPositions.Contains((row.Id, slot.Id)) || lesson is not null && old is not null && (old.SlotId != lesson.SlotId || old.TeacherId != lesson.TeacherId);
                var excluded = lesson is not null ? school.IsExcluded(lesson) : !teachers && school.IsExcluded(row.Id, slot.Id);
                var departure = departed.GetValueOrDefault((row.Id, slot.Id));
                var departureHint = lesson is null && departure is not null ? $"\n변경 전: {departure.Before.Subject} · {(departure.Before.TeacherId is { } beforeId ? names[beforeId] : "미배정")} → {slotLabels[departure.After.SlotId]} · {(departure.After.TeacherId is { } afterId ? names[afterId] : "미배정")}" : "";
                var appliedPattern = lesson is not null && DayTemplatePlanner.PatternId(lesson) is { } patternId ? pattern.GetValueOrDefault(patternId) : null;
                var coverOriginal = vm.Session.Document.SchemaVersion >= 5 ? old : appliedPattern ?? old;
                var covered = lesson is not null && coverOriginal is not null && coverOriginal.TeacherId != lesson.TeacherId;
                var coverHint = covered ? $"\n원래 교사: {(coverOriginal!.TeacherId is { } originalId ? names[originalId] : "미배정")}\n보강 교사: {teacher}" : "";
                bool activeRow = activeCard is not null && row.Id == (teachers ? activeCard.TeacherId : activeCard.ClassId);
                bool manualTarget = activeCard is not null && (activeRow || teachers && lesson?.ClassId == activeCard.ClassId);
                bool heldOrigin = activeRow && lesson is null && slot.Id == manualDraft!.ActiveOriginSlotId;
                var shown = heldOrigin ? activeCard : lesson;
                var displayText = shown is null ? (changed ? (teachers ? "변경" : "변경\n빈칸") : "")
                    : $"{(heldOrigin ? "" : changed ? "*" : "")}{(teachers ? classNames[shown.ClassId] : shown.Subject)}\n{(teachers ? shown.Subject : shown.TeacherId is { } shownTeacher ? names[shownTeacher] : "미배정")}";
                if (heldOrigin) displayText += " · 대기";
                var impact = manualTarget ? (displacements[slot.Id].Count == 0 ? "\n배치 시 추가로 밀려나는 수업 없음"
                    : "\n배치 시 밀려날 수업: " + string.Join(" / ", displacements[slot.Id].Select(x => classNames[x.ClassId] + " " + x.Subject + " · " + (x.TeacherId is { } t ? names[t] : "미배정")))) : "";
                if (activeCard is not null && row.Id == (teachers ? activeCard.TeacherId : activeCard.ClassId) &&
                    placementAdvice.TryGetValue(slot.Id, out var risk) && risk.ThreeConsecutive) displayText = "! " + displayText;
                return new BoardCell
                {
                    RowId = row.Id, SlotId = slot.Id, LessonId = lesson?.Id, TeacherRow = teachers,
                    TeacherId = lesson?.TeacherId, IsDeparture = teachers && lesson is null && changed && !heldOrigin, HeldOrigin = heldOrigin, Locked = lesson?.Locked == true, Covered = covered, IsPast = past, Excluded = excluded, DayBrush = WeekdayFill(slot.Day),
                    SearchText = $"{row.Label} {cls} {teacher} {lesson?.Subject}",
                    Text = displayText,
                    Hint = (lesson is null ? $"{row.Label} · {calendar.Label(slot)} · {(heldOrigin ? "대기 수업의 원래 자리 · 선택 유지 · 다른 교시에 배치하세요" : changed ? "원래 수업이 이동된 자리" : "빈 칸")}{departureHint}"
                        : $"{cls} · {calendar.Label(slot)}\n{lesson.Subject} / {teacher}{coverHint}{(lesson.Locked ? "\n잠긴 수업" : "")}{(changed ? "\n기초 시간표에서 변경됨" : "")}{(past ? "\n작업 기간 밖 · 조회만 가능" : "")}")
                        + (excluded ? "\n학년 행사 제외 · 이동/보강/새 배치 불가" : "") + impact
                        + (manualTarget && placementAdvice.TryGetValue(slot.Id, out var advice) ? "\n선택한 대기 카드 기준: " + advice.Message : ""),
                    BaseFill = covered ? CoverFill : changed ? ChangedFill : WeekdayFill(slot.Day)
                };
            }).ToList())).ToList();
            if (sameColumns)
            {
                // Move existing rows instead of clearing the whole ItemsSource when teachers are promoted.
                var desiredIds = nextRows.Select(x => x.Id).ToHashSet();
                for (int i = boardRows.Count - 1; i >= 0; i--)
                    if (!desiredIds.Contains(boardRows[i].Id)) boardRows.RemoveAt(i);
                var existing = boardRows.ToDictionary(x => x.Id);
                for (int i = 0; i < nextRows.Count; i++)
                {
                    var next = nextRows[i];
                    if (existing.TryGetValue(next.Id, out var row) && row.Name == next.Name)
                    {
                        var oldIndex = boardRows.IndexOf(row);
                        if (oldIndex != i) boardRows.Move(oldIndex, i);
                        for (int col = 0; col < visibleSlots.Count; col++) row.Cells[col].UpdateFrom(next.Cells[col]);
                    }
                    else
                    {
                        if (row is not null) boardRows.Remove(row);
                        boardRows.Insert(i, next);
                    }
                }
            }
            else
            {
                boardRows = new(nextRows);
                BuildBoardColumns(teachers, calendar);
                TimetableGrid.ItemsSource = boardRows;
                SizeBoard();
            }
            UpdateTeacherRowMarkers(teachers);
            renderedProjectId = vm.Session.Document.ProjectId; renderedTeachers = teachers; renderedMonday = calendar.FirstWeekMonday;
            searchTimer.Stop();
            ApplySearch();
            ActionHint.Text = visibleSlots.Count == 0 ? "현재 기간에 표시할 평일 수업이 없습니다. 작업 기간을 늘리거나 월 전체 조회를 선택하세요." : workMode == WorkMode.Browse && ChangedRowsOnly.IsChecked == true && boardRows.Count == 0 ? "현재 조회 범위에 변경된 행이 없습니다." : ModeGuide;
        }
        finally { refreshing = false; UpdateWorkflowControls(); }
    }
    private void BuildBoardColumns(bool teachers, ScheduleCalendar calendar)
    {
            TimetableGrid.Columns.Clear();
            var nameText = new Style(typeof(TextBlock));
            nameText.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            nameText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            nameText.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            nameText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            nameText.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            nameText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            nameText.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("DisplayName")));
            var nameCell = new Style(typeof(DataGridCell), TimetableGrid.CellStyle);
            nameCell.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            nameCell.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(223, 230, 238))));
            nameCell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
            nameCell.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("AquaInk")));
            var boardHeader = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
                (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
            boardHeader.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            boardHeader.Setters.Add(new Setter(Control.BorderBrushProperty, Tint("#DFE6EE")));
            boardHeader.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            boardHeader.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 4, 3, 4)));
            TimetableGrid.Columns.Add(new DataGridTextColumn { Header = teachers ? "교사" : "학급", Binding = new Binding("DisplayName"),
                Width = teachers ? 80 : 62, MinWidth = teachers ? 68 : 54, ElementStyle = nameText, CellStyle = nameCell, HeaderStyle = boardHeader });
            for (int i = 0; i < visibleSlots.Count; i++)
            {
                var dayEdge = i > 0 && calendar.DateOf(visibleSlots[i]) != calendar.DateOf(visibleSlots[i - 1]) ? 2 : 0;
                if (!cellTemplates.TryGetValue((i, dayEdge), out var template))
                {
                    template = (DataTemplate)XamlReader.Parse($$"""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                     <Border BorderBrush="#DFE6EE" BorderThickness="0,0,1,0">
                      <Border DataContext="{Binding Cells[{{i}}]}" Background="{Binding Fill}" ToolTip="{Binding Hint}" Padding="2">
                        <Grid>
                          <Grid Opacity="{Binding Opacity}">
                            <Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                            <TextBlock Text="{Binding PrimaryText}" Foreground="{Binding Foreground}" TextAlignment="Center" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                            <Viewbox Grid.Row="1" Stretch="Uniform" StretchDirection="DownOnly" HorizontalAlignment="Stretch">
                              <TextBlock Text="{Binding SecondaryText}" Foreground="{Binding Foreground}" TextAlignment="Center"/>
                            </Viewbox>
                          </Grid>
                          <Border HorizontalAlignment="Right" VerticalAlignment="Bottom" Background="#0891B2" CornerRadius="2" Padding="1,0">
                            <Border.Style><Style TargetType="Border"><Setter Property="Visibility" Value="Collapsed"/>
                              <Style.Triggers><DataTrigger Binding="{Binding Covered}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger></Style.Triggers>
                            </Style></Border.Style>
                            <TextBlock Text="보" Foreground="White" FontSize="9" FontWeight="Bold"/>
                          </Border>
                          <Border HorizontalAlignment="Right" VerticalAlignment="Top" Background="#F1F5F9" CornerRadius="2" Padding="1">
                            <Border.Style><Style TargetType="Border"><Setter Property="Visibility" Value="Collapsed"/>
                              <Style.Triggers><DataTrigger Binding="{Binding Locked}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger></Style.Triggers>
                            </Style></Border.Style>
                            <Path Width="10" Height="12" Stretch="Uniform" Stroke="#0F172A" StrokeThickness="1.6" Fill="#CBD5E1"
                              Data="M 3,5 L 3,3 C 3,0 9,0 9,3 L 9,5 M 1,5 L 11,5 L 11,13 L 1,13 Z M 6,8 L 6,10"/>
                          </Border>
                        </Grid>
                      </Border>
                     </Border>
                    </DataTemplate>
                    """);
                    cellTemplates[(i, dayEdge)] = template;
                }
                var slot = visibleSlots[i];
                var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), boardHeader);
                headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
                headerStyle.Setters.Add(new Setter(FrameworkElement.TagProperty, slot));
                headerStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, $"{calendar.Label(slot)} · 우클릭: 학년별 이 교시 잠금"));
                headerStyle.Setters.Add(new EventSetter(UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler(PeriodHeaderRightClick)));
                TimetableGrid.Columns.Add(new DataGridTemplateColumn { HeaderStyle = headerStyle, Header = $"{slot.Period}교시", CellTemplate = template });
            }
    }
    private void SizeBoard()
    {
        if (TimetableGrid is null || Zoom is null || visibleSlots.Count == 0) return;
        var nameColumn = TimetableGrid.Columns[0];
        var nameWidth = nameColumn.ActualWidth > 0 ? nameColumn.ActualWidth : nameColumn.Width.Value;
        var width = Math.Clamp((TimetableGrid.ActualWidth - nameWidth - SystemParameters.VerticalScrollBarWidth - 2) / visibleSlots.Count, 34, 170) * Zoom.Value;
        foreach (var column in TimetableGrid.Columns.Skip(1)) column.Width = new DataGridLength(width);
        TimetableGrid.FontSize = 11 * Zoom.Value;
        TimetableGrid.MinRowHeight = 24 * Zoom.Value;
        TimetableGrid.RowHeight = 36 * Zoom.Value;
    }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SizeBoard();
    private void BoardSizeChanged(object sender, SizeChangedEventArgs e) => SizeBoard();
    private void FitClick(object sender, RoutedEventArgs e) { Zoom.Value = 1; SizeBoard(); }

    private static BoardCell? HitCell(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is BoardCell cell) return cell;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
    private void BoardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (busy || lockSelectionMode) return;
        pressedCell = HitCell(e.OriginalSource);
        pressedPoint = e.GetPosition(TimetableGrid);
        // Picking is deferred until mouse-up or drag threshold; no schedule mutation on press.
    }
    private void BoardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (busy || lockSelectionMode || dragActive) return;
        var cell = HitCell(e.OriginalSource);
        var pressed = pressedCell;
        pressedCell = null;
        if (cell is null || !ReferenceEquals(cell, pressed)) return;
        e.Handled = true;
        ModeCellClick(cell);
    }
    private void Pick(string id)
    {
        if (manualDraft is not null)
        {
            if (TryTakeManual(id)) RefreshManualBoard(); return;
        }
        if (vm.Session?.Document.Current.Lessons.FirstOrDefault(x => x.Id == id) is not { } lesson) return;
        if (workMode != WorkMode.Browse && (!EditableSlot(lesson.SlotId) || lesson.Locked || new SchoolDayRules(vm.Session.Document).IsExcluded(lesson)))
        { ActionHint.Text = "잠금·작업 기간 밖·학년 행사 제외 수업은 교체할 수 없습니다. 조회 모드에서는 확인할 수 있습니다."; return; }
        vm.Select(id);
        targetCell = null; fixedTargetSlotId = null; boardAction = BoardAction.Normal; swapCandidates.Clear();
        PaintPlan(); UpdateWorkflowControls();
        ActionHint.Text = ModeGuide;
    }
    private bool AllowedTarget(string sourceId, BoardCell cell)
    {
        var state = vm.Session!.Document.Current;
        var source = state.Lessons.Single(x => x.Id == sourceId);
        if (boardAction == BoardAction.Swap && manualDraft is null) return cell.LessonId is not null;
        if (!cell.TeacherRow) return cell.RowId == source.ClassId;
        if (cell.RowId == source.TeacherId) return true;
        return cell.LessonId is { } id && state.Lessons.Single(x => x.Id == id).ClassId == source.ClassId;
    }
    private void PreviewTarget(BoardCell cell)
    {
        if (workMode == WorkMode.Manual && manualDraft is not null) { ManualCell(cell); return; }
        if (workMode != WorkMode.Auto || vm.Session is null || vm.SelectedLessonId is not { } id) return;
        if (!EditableSlot(cell.SlotId)) { ActionHint.Text = "이 시간은 작업 기간 밖입니다. 작업 기간을 늘린 뒤 목적을 선택하세요. 오늘 이전은 조회만 가능합니다."; return; }
        if (!AllowedTarget(id, cell)) { ActionHint.Text = "같은 학급의 칸 또는 같은 교사 행의 목적 시간을 지정하세요."; return; }
        targetCell = cell; fixedTargetSlotId = cell.SlotId;
        vm.CancelPreview(); PaintPlan();
        ActionHint.Text = "목적 교시를 고정했습니다. ‘자동 교체안 찾기’를 누르면 날짜 허용과 학년 행사 제외를 반영하여 검토합니다.";
    }
    private void PaintPlan()
    {
        var teacherId = (workMode == WorkMode.Browse || ViewMode.SelectedIndex == 1) && vm.SelectedLessonId is { } id
            ? vm.Session?.Document.Current.Lessons.FirstOrDefault(x => x.Id == id)?.TeacherId : null;
        var validTarget = vm.Pending is null || vm.CanApply;
        var heldLesson = manualDraft?.ActiveId is { } active ? vm.Session?.Document.Current.Lessons.Single(x => x.Id == active) : null;
        var planCells = vm.Pending?.Changes.SelectMany(d => new[] { d.Before, d.After })
            .Select(x => (ViewMode.SelectedIndex == 0 ? x.TeacherId ?? "unassigned:" + x.ClassId : x.ClassId, x.SlotId)).ToHashSet() ?? [];
        foreach (var row in boardRows)
        foreach (var cell in row.Cells)
        {
            var related = teacherId is not null && cell.TeacherId == teacherId;
            var changed = planCells.Contains((cell.RowId, cell.SlotId));
            var picked = cell.HeldOrigin || cell.LessonId is not null && cell.LessonId == vm.SelectedLessonId;
            var target = ReferenceEquals(cell, targetCell);
            var swapReady = workMode == WorkMode.Swap && cell.LessonId is { } candidate && swapCandidates.Contains(candidate);
            var lockSelected = lockSelectionMode && lockCells.Contains((cell.RowId, cell.SlotId));
            var suggest = ShowHints.IsChecked && heldLesson is not null && cell.RowId == (cell.TeacherRow ? heldLesson.TeacherId : heldLesson.ClassId)
                && placementAdvice.TryGetValue(cell.SlotId, out var suggestion) ? suggestion : null;
            cell.Fill = lockSelected ? SelectedFill : target ? (validTarget ? TargetFill : InvalidFill) : picked ? SelectedFill :
                changed ? (vm.Pending?.Kind == ChangeKind.AssignTeacher ? CoverFill : PlanFill) : swapReady ? SwapFill : suggest is not null ?
                    (suggest.Recommended ? RecommendationFill : suggest.Allowed ? PossibleFill : UnavailableFill) : related ? TeacherFill : appliedQuery.Length > 0 && cell.SearchMatch ? SearchFill : cell.BaseFill;
            cell.Foreground = cell.Excluded ? Brushes.SlateGray : lockSelected || picked || target || suggest?.Recommended == true ? Brushes.White : cell.IsPast || cell.IsDeparture ? Brushes.SlateGray : Ink;
            var hasFocus = vm.SelectedLessonId is not null || appliedQuery.Length > 0 || lockSelectionMode;
            cell.Opacity = cell.Excluded ? 0.28 : cell.IsDeparture && !target && !lockSelected ? 0.5 : !hasFocus || lockSelected || swapReady || appliedQuery.Length > 0 && cell.SearchMatch || related || changed || picked || target || suggest is not null ? 1 : 0.7;
        }
        RefreshPendingDiagram();
    }
    private static bool Matches(BoardCell cell, LessonInstance lesson) => cell.SlotId == lesson.SlotId &&
        (cell.TeacherRow ? cell.RowId == (lesson.TeacherId ?? "unassigned:" + lesson.ClassId) : cell.RowId == lesson.ClassId);

    private void BoardMouseMove(object sender, MouseEventArgs e)
    {
        if (lockSelectionMode || workMode is not (WorkMode.Manual or WorkMode.Auto)) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            pressedCell = null;
            if (!busy && manualDraft?.ActiveId is { } held)
            {
                if (dragCardLessonId != held) ShowDragCard(held, e.GetPosition(Workspace));
                var cell = HitCell(e.OriginalSource);
                MoveDragCard(e.GetPosition(Workspace), cell is not null && AllowedTarget(held, cell) &&
                    placementAdvice.TryGetValue(cell.SlotId, out var advice) && advice.Allowed);
            }
            return;
        }
        if (busy || dragActive || pressedCell?.LessonId is not { } id || !EditableSlot(pressedCell.SlotId)) return;
        var point = e.GetPosition(TimetableGrid);
        if (Math.Abs(point.X - pressedPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - pressedPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var actionBeforeDrag = boardAction;
        Pick(id);
        if (vm.SelectedLessonId != id) return;
        if (workMode == WorkMode.Manual && manualDraft is null) ManualStartClick(this, new RoutedEventArgs());
        if (workMode == WorkMode.Manual && manualDraft is null) return;
        if (manualDraft is null) boardAction = actionBeforeDrag;
        var payload = new LessonDrag(vm.Session!.Revision, id, Guid.NewGuid().ToString("N"));
        activeDrag = payload;
        dragActive = true;
        ShowDragCard(id, e.GetPosition(Workspace));
        try
        {
            var effect = DragDrop.DoDragDrop(TimetableGrid, new DataObject(DragFormat, payload.Ticket), DragDropEffects.Move);
            if (effect == DragDropEffects.None)
            {
                if (manualDraft is not null) { manualDraft.Undo(); RefreshManualBoard(); }
                else CancelInteraction();
            }
        }
        finally { ClearDragCard(); dragActive = false; pressedCell = null; activeDrag = null; if (manualDraft is not null) { RefreshGrid(true); FocusPriorityTeacher(); } QueuePlanPreview(); }
        e.Handled = true;
    }
    private bool ValidDrag(DragEventArgs e, out LessonDrag? payload, out BoardCell? cell)
    {
        payload = activeDrag;
        cell = HitCell(e.OriginalSource);
        return !busy && workMode is (WorkMode.Manual or WorkMode.Auto) && !lockSelectionMode && payload is not null && e.Data.GetDataPresent(DragFormat) &&
            e.Data.GetData(DragFormat) is string ticket && ticket == payload.Ticket &&
            payload.Revision == vm.Session?.Revision && cell is not null && EditableSlot(cell.SlotId) && AllowedTarget(payload.LessonId, cell);
    }
    private void BoardDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ValidDrag(e, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        MoveDragCard(e.GetPosition(Workspace), e.Effects == DragDropEffects.Move);
        e.Handled = true;
    }
    private void BoardDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (ValidDrag(e, out var payload, out var cell))
        {
            if (vm.SelectedLessonId != payload!.LessonId) Pick(payload.LessonId);
            PreviewTarget(cell!);
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }
    private async void AutoClick(object sender, RoutedEventArgs e) => await FindAuto();
    private async Task FindAuto()
    {
        if (busy || workMode != WorkMode.Auto || lockSelectionMode || manualDraft is not null || vm.Session is null) return;
        if (vm.SelectedLessonId is not { } id)
        { ActionHint.Text = "교체할 수업을 먼저 선택하세요. 목적 칸은 생략할 수 있습니다."; return; }
        if (!EditableSlot(vm.Session.Document.Current.Lessons.Single(x => x.Id == id).SlotId))
        { ActionHint.Text = "오늘 이전과 선택한 작업 기간 밖의 수업은 교체할 수 없습니다."; return; }
        var session = vm.Session; var revision = session.Revision;
        var source = session.Document.Current.Lessons.Single(x => x.Id == id);
        if (source.Locked || source.TeacherId is null || new SchoolDayRules(session.Document).IsExcluded(source))
        { ActionHint.Text = "잠금·교사 미배정·행사 제외 수업은 자동 이동할 수 없습니다."; return; }
        var target = fixedTargetSlotId; var direction = Direction;
        var scope = WorkPeriod.EditableSlots(session.Document).Select(x => x.Id).ToHashSet();
        rechangeReview.Reset(); automaticApproval.Reset(); teacherDayPlan = null; teacherDayReviewDetail = null;
        var requested = new HashSet<string> { id };
        var restrictions = new AutomaticSearchRestrictions();
        consecutiveApprovedPlan = null; consecutiveApprovedSession = null;
        vm.CancelPreview(); PaintPlan();
        using var cancellation = new CancellationTokenSource(); searchCancellation = cancellation;
        boardAction = BoardAction.Normal; swapCandidates.Clear();
        try
        {
            SetBusy(true);
            while (true)
            {
                ActionHint.Text = target is null ? "가능한 교체 시간을 찾습니다. Esc로 중단합니다." : "지정 목적 교시를 고정하고 충돌 수업을 조정하는 중입니다. Esc로 중단합니다.";
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                var immovable = restrictions.Immovable(session, requested);
                var result = await Task.Run(() => CalendarSwapSearch.Find(session, id, target, scope, direction, cancellation.Token,
                    immovable: immovable, avoidConsecutiveTeachers: restrictions.AvoidConsecutiveTeachers));
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(vm.Session, session) || session.Revision != revision || vm.SelectedLessonId != id) return;
                if (result.Plan is null)
                {
                    ActionHint.Text = "연강 없는 안을 찾지 못했습니다. 연강 허용 시 가능한 안을 확인합니다. Esc로 중단합니다.";
                    result = await Task.Run(() => CalendarSwapSearch.Find(session, id, target, scope, direction, cancellation.Token,
                        allowConsecutive: true, immovable: immovable, avoidConsecutiveTeachers: restrictions.AvoidConsecutiveTeachers));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(vm.Session, session) || session.Revision != revision || vm.SelectedLessonId != id) return;
                }
                if (result.Plan is { } plan)
                {
                    var review = ReviewAutomaticPlan(session, plan, requested, restrictions);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (review == AutomaticReviewResult.Cancelled) return;
                    if (review == AutomaticReviewResult.Retry) continue;
                    vm.ShowPlan(plan);
                    var moved = plan.Changes.Single(x => x.Before.Id == id).After;
                    targetCell = boardRows.SelectMany(x => x.Cells).FirstOrDefault(x => Matches(x, moved));
                    ActionHint.Text = result.Message; PaintPlan(); QueuePlanPreview(); break;
                }
                else ActionHint.Text = (restrictions.HasRefusals ? "허용하지 않은 조건을 유지하는 대체안을 찾지 못했습니다. " : "") + result.Message;
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(vm.Session, session) || session.Revision != revision || vm.SelectedLessonId != id) return;
                PaintPlan();
                if (direction != SwapDirection.SameDay) break;
                var expanded = ChooseExpandedDates(session, id, target, scope);
                if (expanded is null) break;
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(vm.Session, session) || session.Revision != revision || vm.SelectedLessonId != id) return;
                direction = expanded.Value; SetDirectionChecks(direction);
            }
        }
        catch (OperationCanceledException) { CancelInteraction(); ActionHint.Text = "탐색을 취소했습니다. 시간표는 바뀌지 않았습니다."; }
        catch (Exception ex) { ActionHint.Text = "교체안 탐색 실패: " + ex.Message; }
        finally { searchCancellation = null; SetBusy(false); QueuePlanPreview(); }
    }
    private void CancelInteraction()
    {
        ClearDragCard();
        ExitLockSelection();
        var wasPromoted = manualDraft is not null || workMode == WorkMode.Swap;
        swapCandidates.Clear();
        manualDraft = null; rechangeReview.Reset(); automaticApproval.Reset(); teacherDayPlan = null; teacherDayReviewDetail = null; placementAdvice.Clear(); fixedTargetSlotId = null; boardAction = BoardAction.Normal;
        consecutiveApprovedPlan = null; consecutiveApprovedSession = null;
        vm.Select(null); targetCell = null; pressedCell = null;
        if (wasPromoted) RefreshGrid();
        PaintPlan(); UpdateWorkflowControls();
        ActionHint.Text = "선택을 취소했습니다. 확정된 교체는 그대로 유지됩니다.";
    }
    private void CancelClick(object sender, RoutedEventArgs e) { searchCancellation?.Cancel(); CancelInteraction(); }
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (busy || !ModeAllows(vm.Pending) || !vm.CanApply) return;
        if (!CalendarReady)
        { ActionHint.Text = "날짜가 바뀌었습니다. 초안을 취소한 뒤 작업 기간을 다시 선택하세요."; return; }
        var plan = vm.Pending!;
        var followUp = ReferenceEquals(plan, teacherDayPlan) ? teacherDayReviewDetail : null;
        if (manualDraft is { } draft && plan.Changes.Any(x => !draft.AllowsDate(x.Before.SlotId) || !draft.AllowsDate(x.After.SlotId)))
        { ActionHint.Text = "수동 날짜 허용 범위를 확인하세요."; return; }
        if (workMode == WorkMode.Auto && !automaticApproval.IsApproved(vm.Session!, plan))
        { ActionHint.Text = "이 교체안의 이동 허용을 다시 확인해야 합니다. 자동 교체안을 다시 찾으세요."; return; }
        if (workMode == WorkMode.Manual && !ConfirmRechanges(plan.Changes.Select(x => x.Before.Id), plan)) return;
        if (!ConfirmConsecutivePlan(plan)) return;
        if (busy || !ReferenceEquals(vm.Pending, plan) || !vm.CanApply || !vm.Apply()) return;
        manualDraft = null; rechangeReview.Reset(); automaticApproval.Reset(); teacherDayPlan = null; teacherDayReviewDetail = null; placementAdvice.Clear(); boardAction = BoardAction.Normal;
        consecutiveApprovedPlan = null; consecutiveApprovedSession = null;
        RefreshGrid(); ActionHint.Text = "교체했습니다. Ctrl+Z로 이 작업 전체를 되돌릴 수 있습니다.";
        if (!string.IsNullOrEmpty(followUp))
        { ActionHint.Text = "배치 완료 · 수작업 확인 목록에서 연강 수업을 검토하고 필요한 수업만 수동 교체하세요."; ShowTeacherDayFollowUp(followUp); }
    }
    private void BoardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (busy || dragActive || vm.Session is null) return;
        if (OpenTeacherNameContext(e.OriginalSource)) { pressedCell = null; e.Handled = true; return; }
        if (HitCell(e.OriginalSource) is not { } cell) return;
        pressedCell = null; e.Handled = true;
        if (lockSelectionMode) { OpenLockContext(cell); return; }
        var session = vm.Session; var revision = session.Revision; var mode = workMode;
        var menu = new ContextMenu { PlacementTarget = TimetableGrid };
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => { if (!busy && workMode == mode && ReferenceEquals(vm.Session, session) && session.Revision == revision) action(); };
            menu.Items.Add(item);
        }
        var label = workMode switch
        {
            WorkMode.Browse => "이 교사의 수업 강조", WorkMode.Swap => "맞교환 선택/확인",
            WorkMode.Cover => "공강 교사로 보강", WorkMode.Manual => "카드 꺼내기/이 칸에 놓기", _ => "수업 선택/목적 지정"
        };
        Add(label, () => ModeCellClick(cell), workMode == WorkMode.Browse || !cell.Excluded);
        if (workMode == WorkMode.Auto)
            Add("자동 교체안 찾기", () => AutoClick(this, new RoutedEventArgs()), vm.SelectedLessonId is not null);
        if (manualDraft is not null)
        {
            Add("연쇄 한 단계 뒤로", () => DraftUndoClick(this, new RoutedEventArgs()), manualDraft.CanUndo);
            Add("연쇄 작업 취소", CancelInteraction);
        }
        if (cell.LessonId is { } id)
        {
            Add("수업 정보…", () => MessageBox.Show(this, cell.Hint, "수업 정보", MessageBoxButton.OK, MessageBoxImage.Information));
            if (workMode == WorkMode.Browse)
                Add(cell.Locked ? "수업 잠금 해제" : "수업 잠금", () =>
                {
                    var result = session.Apply(ChangePlanner.ToggleLock(session, id));
                    if (!result.IsValid) { ActionHint.Text = string.Join(" ", result.Issues.Select(x => x.Message)); return; }
                    vm.Refresh(); RefreshGrid();
                });
        }
        if (manualDraft is null) menu.Items.Add(BuildTeacherDayMenu(cell));
        if (manualDraft is null && session.Document.Calendar is { } dateCalendar)
            Add("이 날짜에 기초 요일 시간표 적용…", () => PreviewDayTemplate(dateCalendar.DateOf(session.Document.Current.Slots.Single(x => x.Id == cell.SlotId))), CalendarReady);
        if (workMode == WorkMode.Browse) menu.Items.Add(BuildBulkLockMenu(cell));
        Add("변경 내역 종합 다이어그램…", () => SummaryClick(this, new RoutedEventArgs()));
        Add("작업별 변경 내역…", () => HistoryClick(this, new RoutedEventArgs()));
        menu.IsOpen = true;
    }
    private void UndoClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (manualDraft is not null) { DraftUndoClick(sender, e); return; }
        CancelInteraction(); var changed = vm.Session?.Undo() == true; vm.Refresh(); RefreshGrid();
        if (!changed) ActionHint.Text = "되돌릴 작업이 없거나 행사 제외에 해당하는 변경입니다. 필요하면 학년 행사 제외 설정을 확인하세요.";
    }
    private void RedoClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null) return;
        CancelInteraction(); var changed = vm.Session?.Redo() == true; vm.Refresh(); RefreshGrid();
        if (!changed) ActionHint.Text = "다시 실행할 작업이 없거나 행사 제외에 해당하는 변경입니다. 필요하면 학년 행사 제외 설정을 확인하세요.";
    }
}
