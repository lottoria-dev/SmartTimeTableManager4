using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public sealed record Option(string Id, string Label);
public sealed record PreviewCard(string Label, string Before, string After);
public sealed record PreviewGroup(string Label, IReadOnlyList<PreviewCard> Cards);
public sealed class MainViewModel : ObservableObject
{
    public ProjectSession? Session { get; private set; }
    public ChangePlan? Pending { get; private set; }
    private string status = "CSV/XLSX를 가져오거나 .ttm 프로젝트를 여세요.";
    private string preview = "표에서 집은 수업의 교체안을 확인합니다.";
    private string selectedSummary = "선택한 수업 없음";
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string PreviewText { get => preview; set => SetProperty(ref preview, value); }
    public string SelectedSummary { get => selectedSummary; set => SetProperty(ref selectedSummary, value); }
    public string? SelectedLessonId { get; private set; }
    public ObservableCollection<Option> Teachers { get; } = [];
    public string Title => $"{Session?.Document.Name ?? "새 프로젝트"}{(Session?.IsDirty == true ? " *" : "")} · Smart Timetable Manager 4 · 4.0.3{(Session?.Document.Monthly is { } monthly ? $" · {monthly.Month:yyyy년 M월}" : "")}";
    private bool groupPreviewByTeacher;
    public bool GroupPreviewByTeacher
    {
        get => groupPreviewByTeacher;
        set { if (SetProperty(ref groupPreviewByTeacher, value)) RefreshPreviewGroups(); }
    }
    public IReadOnlyList<PreviewGroup> PreviewGroups { get; private set; } = [];
    private void RefreshPreviewGroups()
    {
        PreviewGroups = [];
        if (Pending is { } plan && Session is { } session)
        {
            var state = session.Document.Current;
            var classes = state.Classes.ToDictionary(x => x.Id, x => x.Name);
            var teachers = Teachers.ToDictionary(x => x.Id, x => x.Label);
            var slots = (plan.DayTemplate is { } day ? DayTemplatePlanner.Extend(state, day) : state).Slots.ToDictionary(x => x.Id);
            string TeacherName(string? id) => id is null ? "미배정" : teachers[id];
            string Position(LessonInstance lesson) => SlotLabel(slots[lesson.SlotId]) +
                (plan.Kind == ChangeKind.AssignTeacher ? " · " + TeacherName(lesson.TeacherId) : "") +
                (plan.Kind == ChangeKind.ToggleLock ? (lesson.Locked ? " · 잠금" : " · 해제") : "");
            PreviewGroups = plan.Changes
                .OrderBy(d => slots[d.Before.SlotId].Week).ThenBy(d => slots[d.Before.SlotId].Day).ThenBy(d => slots[d.Before.SlotId].Period)
                .GroupBy(d => GroupPreviewByTeacher ? d.Before.TeacherId : d.Before.ClassId)
                .Select(g => new PreviewGroup(GroupPreviewByTeacher ? TeacherName(g.Key) : classes[g.Key!],
                    g.Select(d => new PreviewCard(d.Before.Subject + " · " +
                        (GroupPreviewByTeacher ? classes[d.Before.ClassId] : TeacherName(d.Before.TeacherId)),
                        d.Added ? "추가 수업" : Position(d.Before), d.Removed ? "요일 변경으로 제외" : Position(d.After))).ToArray())).ToArray();
        }
        OnPropertyChanged(nameof(PreviewGroups));
    }
    private ChangePlan? validatedPlan;
    private Guid validatedRevision;
    private ValidationResult? pendingValidation;
    private ValidationResult ValidatePending()
    {
        var session = Session!; var plan = Pending!;
        if (!ReferenceEquals(validatedPlan, plan) || validatedRevision != session.Revision || pendingValidation is null)
        {
            pendingValidation = session.Preview(plan); validatedPlan = plan; validatedRevision = session.Revision;
        }
        return pendingValidation;
    }
    public bool CanApply => Pending is not null && Session is not null && ValidatePending().IsValid;
    public bool CanUndo => Session?.CanUndo == true;
    public bool CanRedo => Session?.CanRedo == true;
    public void Attach(ProjectSession session)
    {
        Session = session;
        SelectedLessonId = null;
        SelectedSummary = "선택한 수업 없음";
        Teachers.Clear();
        var duplicates = session.Document.Current.Teachers.GroupBy(x => x.Name).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        foreach (var t in session.Document.Current.Teachers.OrderBy(x => x.Name)) Teachers.Add(new(t.Id, duplicates.Contains(t.Name) ? $"{t.Name} [{t.Id}]" : t.Name));
        CancelPreview();
        Refresh();
    }
    public void Select(string? lessonId)
    {
        CancelPreview();
        SelectedLessonId = lessonId;
        SelectedSummary = lessonId is null ? "선택한 수업 없음" : Describe(Session!.Document.Current.Lessons.Single(x => x.Id == lessonId));
    }
    public void ShowPlan(ChangePlan plan)
    {
        Pending = plan;
        var result = ValidatePending();
        PreviewText = $"{plan.Description} · {plan.Changes.Length}개 수업";
        RefreshPreviewGroups();
        var errors = result.Issues.Where(x => x.Severity == Severity.Error).Select(x => x.Message).Distinct().ToArray();
        if (errors.Length > 0) PreviewText += " · 적용 불가: " + string.Join(" / ", errors);
        else PreviewText += " · 검증 통과";
        if (plan.Kind == ChangeKind.AssignTeacher && result.Issues.Any(x => x.Code == "UNASSIGNED"))
            PreviewText += "\n다른 미배정 수업이 남아 있어 전체 교사 가용성 확인은 미완료입니다.";
        OnPropertyChanged(nameof(CanApply));
    }
    public bool Apply()
    {
        if (Pending is null || Session is null) return false;
        var result = Session.Apply(Pending);
        if (!result.IsValid) { PreviewText = string.Join("\n", result.Issues.Select(x => x.Message)); OnPropertyChanged(nameof(CanApply)); return false; }
        CancelPreview();
        Refresh();
        if (SelectedLessonId is not null && Session.Document.Current.Lessons.FirstOrDefault(x => x.Id == SelectedLessonId) is { } selected) SelectedSummary = Describe(selected);
        else { SelectedLessonId = null; SelectedSummary = "선택한 수업 없음"; }
        return true;
    }
    public void CancelPreview()
    {
        Pending = null;
        validatedPlan = null; pendingValidation = null;
        RefreshPreviewGroups();
        PreviewText = "미리보기 대기 · Esc는 미리보기만 취소합니다.";
        OnPropertyChanged(nameof(CanApply));
    }
    public void Refresh()
    {
        if (Session is { } session)
        {
            var s = session.Document.Current;
            var history = ChangeHistory.Operations(session.Document);
            Status = $"{s.Classes.Length}학급 · {s.Teachers.Length}교사 · {s.Lessons.Length}수업 · 미배정 {s.Lessons.Count(x => x.TeacherId is null)} · {(session.IsDirty ? "미저장" : "저장됨")} · 변경 {history.Count(x => x.IsApplied)}/{history.Count}";
        }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanApply));
    }
    private string SlotLabel(TimeSlot slot) => Session?.Document.Calendar?.Label(slot) ?? slot.Label;
    public string Describe(LessonInstance lesson)
    {
        var s = Pending?.DayTemplate is { } day ? DayTemplatePlanner.Extend(Session!.Document.Current, day) : Session!.Document.Current;
        return $"{s.Classes.Single(x => x.Id == lesson.ClassId).Name} · {SlotLabel(s.Slots.Single(x => x.Id == lesson.SlotId))}\n{lesson.Subject} / {(lesson.TeacherId is null ? "교사 미배정" : Teachers.Single(x => x.Id == lesson.TeacherId).Label)}{(lesson.Locked ? " · 잠금" : "")}";
    }
}
