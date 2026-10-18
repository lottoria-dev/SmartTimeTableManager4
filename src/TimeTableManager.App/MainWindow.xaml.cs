using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeTableManager.Application;
using TimeTableManager.Domain;
using TimeTableManager.Infrastructure;

namespace TimeTableManager.App;


public partial class MainWindow : Window
{
    private readonly MainViewModel vm = new();
    private ProjectFile? projectFile;
    private bool refreshing, busy, closeApproved;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Guid? recoveredState;
    private int recoveredSchema, recoveredBaselineSlots;
    private ScheduleCalendar? recoveredCalendar;
    private ImmutableArray<GradeDateExclusion> recoveredExclusions = [];
    private MonthlySchedule? recoveredMonthly;
    private static string RecoveryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeTableManager", "Recovery");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = vm;
        TimetableGrid.Loaded += (_, _) => { boardScroll = FindGridScroll(TimetableGrid); UpdateDateHeaders(); };
        TimetableGrid.LayoutUpdated += (_, _) => UpdateDateHeaders();
        TimetableGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateDateHeaders()));
        refreshing = true;
        ViewMode.SelectedIndex = 0;
        DayFilter.ItemsSource = new[] { new Option("0", "모든 요일") }.Concat(
            Enumerable.Range(1, 5).Select(d => new Option(d.ToString(), "월화수목금토일"[d - 1] + "요일"))).ToArray();
        DayFilter.SelectedIndex = 0;
        refreshing = false;
        Activated += async (_, _) =>
        {
            if (!busy && manualDraft is null && vm.Session?.Document.Calendar is { } dates && dates.EditableFrom != Today)
                await RefreshWorkPeriod(showWorkingPeriod: true);
        };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); ApplySearch(); };
        recoveryTimer.Tick += RecoveryTick;
        recoveryTimer.Start();
        UpdateWorkflowControls();
        if (Directory.Exists(RecoveryDirectory) && Directory.EnumerateFiles(RecoveryDirectory, "*.ttm").Any())
            vm.Status = "복구본이 있습니다. ‘복구본 열기’에서 확인할 수 있습니다.";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        // Input handlers block edits while busy; keep the table visible and scrollable.
        FileTools.IsEnabled = !value;
        BoardTools.IsEnabled = !value;
        AutoButton.IsEnabled = !value;
        CalendarTools.IsEnabled = !value;
        UpdateWorkflowControls();
        Mouse.OverrideCursor = value ? Cursors.Wait : null;
    }
    private void Error(Exception ex) => MessageBox.Show(this, ex.Message, "작업을 완료하지 못했습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
    private string RecoveryPath => Path.Combine(RecoveryDirectory, vm.Session!.Document.ProjectId + ".ttm");

    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null) return;
        var picker = new OpenFileDialog { Title = "시간표 파일 열기", Filter = "모든 시간표 파일|*.xlsx;*.csv;*.ttm;*.ttm.bak|기초 시간표 Excel·CSV|*.xlsx;*.csv|저장한 작업 파일|*.ttm;*.ttm.bak", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (picker.FileName.EndsWith(".ttm",StringComparison.OrdinalIgnoreCase) || picker.FileName.EndsWith(".ttm.bak",StringComparison.OrdinalIgnoreCase))
        { await OpenProject(picker.FileName,false); return; }
        try
        {
            SetBusy(true);
            var documents = await Task.Run(() => StandardTimetableReader.Read(picker.FileName));
            SetBusy(false);
            ProjectDocument document = documents[0];
            if (documents.Count > 1)
            {
                var chooser = new ChoiceWindow("시간표 시트 선택", "시간표가 여러 시트에 있습니다. 사용할 시트만 선택하세요.",
                    documents.Select((d,i)=>new Option(i.ToString(), $"{d.Source?.Sheet} · {d.Current.Classes.Length}학급 · {d.Current.Lessons.Length}수업"))) { Owner=this };
                if (chooser.ShowDialog()!=true || chooser.Choice is null) return;
                document=documents[int.Parse(chooser.Choice.Id)];
            }
            SetBusy(true);
            var imported = document;
            document = await Task.Run(() => WorkPeriod.Prepare(imported, Today));
            SetBusy(false);
            if (!await MayAbandon()) return;
            projectFile?.Dispose(); projectFile=null;
            Attach(new ProjectSession(document));
            if (visibleSlots.Count > 0) ActionHint.Text=$"오늘부터 {document.Calendar!.EditableUntil:M/d}까지 작업할 수 있습니다. 작업 기간과 학년 행사를 확인하세요.";
        }
        catch(Exception ex){Error(ex);}
        finally{SetBusy(false);}
    }
    private async Task OpenProject(string path, bool recovery)
    {
        ProjectFile? nextFile = null;
        try
        {
            if (!recovery && string.Equals(projectFile?.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
            SetBusy(true);
            ProjectDocument doc;
            if (recovery || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) doc = await Task.Run(() => ProjectRepository.Read(path));
            else
            {
                nextFile = new ProjectFile(path);
                doc = await Task.Run(nextFile.Load);
            }
            var saved = nextFile is not null;
            var nextSession = await Task.Run(() =>
            {
                var session = new ProjectSession(doc, saved);
                session.SetWorkPeriod(Today, doc.Calendar?.WeeksAhead ?? WorkPeriod.DefaultWeeksAhead);
                return session;
            });
            SetBusy(false);
            if (!await MayAbandon()) return;
            projectFile?.Dispose();
            projectFile = nextFile;
            nextFile = null;
            Attach(nextSession);
        }
        catch (Exception ex) { Error(ex); }
        finally { nextFile?.Dispose(); SetBusy(false); }
    }
    private void Attach(ProjectSession session)
    {
        ExitLockSelection();
        recoveredState = null;
        vm.Attach(session);
        refreshing = true;
        viewedMonth = MonthlyTimetable.MonthOf(Today);
        LoadCalendarControls(session);
        vm.Refresh();
        refreshing = false;
        RefreshGrid();
    }
    private async void SaveClick(object sender, RoutedEventArgs e) => await Save(false);
    private async void SaveAsClick(object sender, RoutedEventArgs e) => await Save(true);
    private async Task<bool> Save(bool saveAs)
    {
        if (vm.Session is null || busy) return false;
        if (manualDraft is not null) { ActionHint.Text="수동 연쇄를 적용하거나 취소한 뒤 저장하세요."; return false; }
        ProjectFile? next = null;
        try
        {
            var path = projectFile?.Path ?? "시간표.ttm";
            if (projectFile is null || saveAs)
            {
                var picker = new SaveFileDialog { Title = "저장 시각을 붙여 새 작업 파일 저장", Filter = "시간표 프로젝트|*.ttm",
                    FileName = Path.GetFileName(TimestampedFileNames.Stamp(path, DateTime.Now)),
                    InitialDirectory = projectFile is null ? "" : Path.GetDirectoryName(projectFile.Path) ?? "",
                    DefaultExt = ".ttm", AddExtension = true, OverwritePrompt = false };
                if (picker.ShowDialog(this) != true) return false;
                path = picker.FileName;
            }
            path = TimestampedFileNames.Next(path, DateTime.Now);
            next = new ProjectFile(path);
            var destination = next;
            SetBusy(true);
            var document = vm.Session.Document;
            await Task.Run(() => destination.Save(document));
            if (next is not null) { projectFile?.Dispose(); projectFile = next; next = null; }
            vm.Session.MarkSaved(document);
            DeleteRecovery();
            vm.Refresh();
            ActionHint.Text = "저장 완료: " + Path.GetFileName(destination.Path);
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
        finally { next?.Dispose(); SetBusy(false); }
    }
    private async Task<bool> MayAbandon()
    {
        if (manualDraft is not null) { ActionHint.Text="수동 연쇄를 적용하거나 취소한 뒤 파일을 바꾸거나 종료하세요."; return false; }
        if (vm.Session?.IsDirty != true) return true;
        var answer = MessageBox.Show(this, "현재 프로젝트의 변경 내용을 저장할까요?\n‘아니요’를 선택하면 현재 변경 내용과 자동 복구본을 버립니다.", "저장하지 않은 변경", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return await Save(false);
        DeleteRecovery();
        return true;
    }
    private void DeleteRecovery()
    {
        if (vm.Session is null) return;
        try
        {
            if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
            if (File.Exists(RecoveryPath + ".bak")) File.Delete(RecoveryPath + ".bak");
            recoveredState = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { vm.Status = "이전 복구본 정리는 완료하지 못했습니다. 복구본을 열 때 저장 시각을 확인하세요."; }
    }
    private async void RecoveryTick(object? sender, EventArgs e)
    {
        if (busy || lockSelectionMode || manualDraft is not null || dragActive || (pressedCell is not null && Mouse.LeftButton == MouseButtonState.Pressed) || !IsActive || vm.Session is null) return;
        if (vm.Session.Document.Calendar?.EditableFrom != Today) await RefreshWorkPeriod(showWorkingPeriod: true);
        if (busy || vm.Session is null) return;
        if (!vm.Session.IsDirty) { if (recoveredState is not null) DeleteRecovery(); return; }
        if (recoveredSchema == vm.Session.Document.SchemaVersion && recoveredBaselineSlots == vm.Session.Document.Baseline.Slots.Length && recoveredState == vm.Session.Document.CurrentStateId && recoveredCalendar == vm.Session.Document.Calendar && recoveredMonthly == vm.Session.Document.Monthly && recoveredExclusions.SequenceEqual(SchoolDayRules.Entries(vm.Session.Document))) return;
        try
        {
            SetBusy(true);
            var doc = vm.Session.Document;
            var path = RecoveryPath;
            await Task.Run(() => ProjectRepository.WriteAtomic(path, doc));
            recoveredState = doc.CurrentStateId;
            recoveredSchema = doc.SchemaVersion; recoveredBaselineSlots = doc.Baseline.Slots.Length;
            recoveredCalendar = doc.Calendar;
            recoveredExclusions = SchoolDayRules.Entries(doc);
            recoveredMonthly = doc.Monthly;
        }
        catch (Exception ex) { vm.Status = "자동 복구본 저장 실패: " + ex.Message; }
        finally { SetBusy(false); }
    }
    private async void RecoveryClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RecoveryDirectory);
        var picker = new OpenFileDialog { Filter = "자동 복구본|*.ttm;*.ttm.bak", InitialDirectory = RecoveryDirectory, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await OpenProject(picker.FileName, true);
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (busy || manualDraft is not null || vm.Session is null) return;
        var picker = new SaveFileDialog { Title = "배정 결과 내보내기 · Excel 권장", Filter = "Excel 통합 문서|*.xlsx|CSV 파일|*.csv", DefaultExt = ".xlsx", AddExtension = true,
            FileName = TimestampedFileNames.Stamp("시간표_배정결과.xlsx", DateTime.Now), OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true);
            var state = vm.Session.Document.Current;
            var path = TimestampedFileNames.Next(Path.ChangeExtension(picker.FileName, picker.FilterIndex == 2 ? ".csv" : ".xlsx"), DateTime.Now);
            await Task.Run(() => CsvExporter.Write(path, state));
            vm.Status = "현재 수업 목록을 내보냈습니다. 잠금·담임·기초 원본·이력·빈 교시는 .ttm 프로젝트에 저장하세요.";
            ActionHint.Text = "내보내기 완료: " + Path.GetFileName(path);
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1) { ModeHelpClick(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Escape) { CancelClick(sender, e); e.Handled = true; return; }
        if (busy) return;
        if (Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.O) { e.Handled = true; OpenClick(sender, e); }
        if (e.Key == Key.S) { e.Handled = true; await Save(false); }
        if (e.Key == Key.Z) { e.Handled = true; UndoClick(sender, e); }
        if (e.Key == Key.Y) { e.Handled = true; RedoClick(sender, e); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        e.Cancel = true;
        if (busy) return;
        if (!await MayAbandon()) return;
        closeApproved = true;
        recoveryTimer.Stop();
        searchTimer.Stop();
        projectFile?.Dispose();
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
