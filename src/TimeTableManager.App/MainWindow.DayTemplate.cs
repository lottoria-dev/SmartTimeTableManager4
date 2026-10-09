using System.Windows;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private void DayTemplateClick(object sender, RoutedEventArgs e) => PreviewDayTemplate(null);
    private void PreviewDayTemplate(DateOnly? selectedDate)
    {
        if (busy || dragActive || manualDraft is not null || vm.Session is not { } session || session.Document.Calendar is not { } calendar || !CalendarReady) return;
        var revision = session.Revision;
        var picker = new DayTemplateWindow(session.Document, selectedDate);
        if (!ShowWorkDialog(picker) || !ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
        var slots = session.Document.Current.Slots.ToDictionary(x => x.Id);
        var locked = session.Document.Current.Lessons.Where(x => x.Locked && calendar.DateOf(slots[x.SlotId]) == picker.TargetDate).ToArray();
        var result = DayTemplatePlanner.Create(session, picker.TargetDate, picker.SourceDay, allowLocked: locked.Length > 0);
        if (result.Plan is not { } plan) { ActionHint.Text = result.Message; return; }
        if (locked.Length > 0 && !ShowWorkDialog(new ReviewWindow("잠긴 수업이 있는 날짜", $"잠긴 수업 {locked.Length}개가 있습니다. 그래도 기초 요일을 적용할까요?",
            $"{picker.TargetDate:M/d} → 기초 {"월화수목금"[picker.SourceDay - 1]}요일 시간표", "기초 요일 적용",
            "• 같은 학급·교시의 잠금 유지\n• 없어지는 교시의 잠금은 함께 제외\n• 이번 기초 배정 변경만 허용"))) return;
        if (!ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
        // School planning is a baseline edit: no rechange/consecutive prompts and no move diagram.
        var applied = session.Apply(plan);
        if (!applied.IsValid) { ActionHint.Text = string.Join(" / ", applied.Issues.Select(x => x.Message)); return; }
        CancelInteraction(); ChangedRowsOnly.IsChecked = false; vm.Refresh(); RefreshGrid();
        ActionHint.Text = $"{picker.TargetDate:M/d}의 기초 배정을 {"월화수목금"[picker.SourceDay - 1]}요일 시간표로 바꿨습니다. 교체 이력과 변동 색상에는 포함하지 않습니다. 저장하세요.";
    }
}
