using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private ChangePlan? teacherDayPlan;
    private string? teacherDayReviewDetail;
    private TeacherDayReviewWindow? teacherDayReviewWindow;
    private string DescribeTeacherDayReview(ProjectSession session, ChangePlan plan)
    {
        var state = session.Document.Current;
        var slots = state.Slots.ToDictionary(x => x.Id);
        var classes = state.Classes.ToDictionary(x => x.Id, x => x.Name);
        var teachers = state.Teachers.ToDictionary(x => x.Id, x => x.Name);
        var changed = plan.Changes.Select(x => x.Before.Id).ToHashSet();
        var reviews = TeacherDayReview.Create(state, plan);
        string Label(TimeSlot slot) => session.Document.Calendar is { } calendar
            ? $"{calendar.DateOf(slot):yyyy/M/d} ({"월화수목금"[slot.Day - 1]})" : $"{slot.Week}주 {"월화수목금"[slot.Day - 1]}요일";
        return string.Join("\n\n", reviews.Select(item =>
            $"{teachers.GetValueOrDefault(item.Risk.TeacherId, item.Risk.TeacherId)} · {Label(item.Risk.Day)} · {item.Risk.FirstPeriod}~{item.Risk.LastPeriod}교시 ({item.Risk.Length}연강)\n" +
            string.Join("\n", item.Lessons.Select(lesson => $"  {slots[lesson.SlotId].Period}교시 · {classes.GetValueOrDefault(lesson.ClassId, lesson.ClassId)} · {lesson.Subject} · {(changed.Contains(lesson.Id) ? "이번에 이동" : "기존 수업")}"))));
    }
    private void ShowTeacherDayFollowUp(string detail)
    {
        teacherDayReviewWindow?.Close();
        var window = new TeacherDayReviewWindow(detail) { Owner = this };
        teacherDayReviewWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(teacherDayReviewWindow, window)) teacherDayReviewWindow = null; };
        window.Show();
    }
    private MenuItem BuildTeacherDayMenu(BoardCell cell)
    {
        var session = vm.Session!; var revision = session.Revision;
        var state = session.Document.Current;
        var teacherId = cell.TeacherRow ? cell.RowId : state.Lessons.FirstOrDefault(x => x.Id == cell.LessonId)?.TeacherId;
        var teacher = state.Teachers.FirstOrDefault(x => x.Id == teacherId);
        var slot = state.Slots.Single(x => x.Id == cell.SlotId);
        var calendar = session.Document.Calendar;
        var menu = new MenuItem { Header = "교사 하루 수업 · AI 자동 이동",
            IsEnabled = teacher is not null && calendar is not null && CalendarReady && calendar.CanEdit(slot) };
        if (teacher is null || calendar is null) return menu;
        var date = calendar.DateOf(slot);
        var maxPeriod = DayTemplatePlanner.Periods(session.Document, date);
        menu.Header = $"{teacher.Name} · {date:M/d} 하루 수업 이동";
        foreach (var goal in new[] { TeacherDayGoal.Morning, TeacherDayGoal.Afternoon, TeacherDayGoal.OtherDates, TeacherDayGoal.ClearMorning })
        {
            var item = new MenuItem { Header = goal == TeacherDayGoal.Morning ? "모두 오전으로…" : goal == TeacherDayGoal.Afternoon ? "모두 오후로…" : goal == TeacherDayGoal.ClearMorning ? "오전 수업만 다른 날짜로…" : "하루 수업 모두 다른 날짜로…",
                IsEnabled = goal is (TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning) || maxPeriod >= 2 };
            item.Click += async (_, _) =>
            {
                if (!busy && manualDraft is null && ReferenceEquals(vm.Session, session) && session.Revision == revision)
                    await FindTeacherDay(teacher.Id, date, goal);
            };
            menu.Items.Add(item);
        }
        return menu;
    }
    private async Task FindTeacherDay(string teacherId, DateOnly date, TeacherDayGoal goal)
    {
        if (busy || manualDraft is not null || vm.Session is not { } session || session.Document.Calendar is not { } calendar || !CalendarReady) return;
        var revision = session.Revision; var state = session.Document.Current;
        var slots = state.Slots.ToDictionary(x => x.Id);
        var lessons = state.Lessons.Where(x => x.TeacherId == teacherId && calendar.DateOf(slots[x.SlotId]) == date).ToArray();
        if (lessons.Length == 0) { ActionHint.Text = "선택한 날짜에 이 교사의 수업이 없습니다."; return; }
        var picker = new TeacherDayWindow(state.Teachers.Single(x => x.Id == teacherId).Name, date, goal,
            DayTemplatePlanner.Periods(session.Document, date));
        if (!ShowWorkDialog(picker) || !ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
        if (goal == TeacherDayGoal.ClearMorning)
        {
            lessons = lessons.Where(x => slots[x.SlotId].Period <= picker.MorningLastPeriod).ToArray();
            if (lessons.Length == 0) { ActionHint.Text = "선택한 오전 시간대에 이 교사의 수업이 없습니다."; return; }
        }
        CancelInteraction();
        changingMode = true; workMode = WorkMode.Auto; WorkModes.SelectedIndex = (int)workMode; changingMode = false;
        SetDirectionChecks(picker.Direction); vm.Select(lessons[0].Id); UpdateWorkflowControls(); PaintPlan();
        var request = new TeacherDayRequest(teacherId, date, goal, picker.MorningLastPeriod, picker.Direction, picker.SpillOverflow);
        var scope = MonthlyTimetable.WorkingSlots(session.Document).Select(x => x.Id).ToHashSet();
        var requested = lessons.Select(x => x.Id).ToHashSet();
        var restrictions = new AutomaticSearchRestrictions();
        using var cancellation = new CancellationTokenSource(); searchCancellation = cancellation;
        try
        {
            bool placementFirst = goal is (TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning);
            SetBusy(true);
            while (true)
            {
                ActionHint.Text = placementFirst ? "배치 우선으로 전체 이동안을 찾습니다. 연강 허용은 교사별로 선택합니다. Esc로 중단합니다." : "요청한 시간대로 전체 수업을 배치하는 안을 찾습니다. Esc로 중단합니다.";
                var immovable = restrictions.Immovable(session, requested);
                var result = await Task.Run(() => TeacherDaySearch.Find(session, request, scope, cancellation.Token,
                    immovable: immovable, avoidConsecutiveTeachers: restrictions.AvoidConsecutiveTeachers));
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
                if (result.Plan is null && !placementFirst)
                {
                    ActionHint.Text = "관련 교사의 연강 회피 안을 찾지 못했습니다. 허용 시 가능한 안을 확인합니다. 대상 교사의 연강은 기본 허용입니다.";
                    result = await Task.Run(() => TeacherDaySearch.Find(session, request, scope, cancellation.Token,
                        allowConsecutive: true, immovable: immovable, avoidConsecutiveTeachers: restrictions.AvoidConsecutiveTeachers));
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
                }
                if (result.Plan is { } plan)
                {
                    var review = ReviewAutomaticPlan(session, plan, requested, restrictions, placementFirst,
                        placementFirst ? null : teacherId);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (review == AutomaticReviewResult.Cancelled) return;
                    if (review == AutomaticReviewResult.Retry) continue;
                    teacherDayPlan = plan;
                    teacherDayReviewDetail = placementFirst ? DescribeTeacherDayReview(session, plan) : null;
                    vm.ShowPlan(plan);
                }
                ActionHint.Text = (result.Plan is null && restrictions.HasRefusals
                    ? "허용하지 않은 조건을 유지하는 대체안을 찾지 못했습니다. " : "") + result.Message;
                PaintPlan(); break;
            }
        }
        catch (OperationCanceledException) { CancelInteraction(); ActionHint.Text = "하루 이동 탐색을 취소했습니다. 시간표는 바뀌지 않았습니다."; }
        catch (Exception ex) { ActionHint.Text = "하루 이동 탐색 실패: " + ex.Message; }
        finally { searchCancellation = null; SetBusy(false); QueuePlanPreview(); }
    }
}
