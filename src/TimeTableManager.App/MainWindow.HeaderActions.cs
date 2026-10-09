using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private void OpenDateContext(DateOnly date, FrameworkElement target)
    {
        if (busy || dragActive || vm.Session is not { } session) return;
        var revision = session.Revision;
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(new MenuItem { Header = $"{date:yyyy/M/d} · 날짜 작업", IsEnabled = false });
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title, IsEnabled = manualDraft is null && CalendarReady };
            item.Click += (_, _) => { if (!busy && ReferenceEquals(vm.Session, session) && session.Revision == revision) action(); };
            menu.Items.Add(item);
        }
        Add("이 날짜의 학년 행사 제외…", () => EditExclusions(date));
        Add("이 날짜에 기초 요일 시간표 적용…", () => PreviewDayTemplate(date));
        menu.IsOpen = true;
    }
    private void PeriodHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridColumnHeader { Tag: TimeSlot slot } header || busy || dragActive || vm.Session is not { } session) return;
        e.Handled = true;
        var revision = session.Revision;
        var menu = new ContextMenu { PlacementTarget = header };
        menu.Items.Add(new MenuItem { Header = $"{session.Document.Calendar!.Label(slot)} · 학년별 잠금", IsEnabled = false });
        var grades = new List<(int Grade, MenuItem Item)>();
        for (int grade = 1; grade <= 3; grade++)
        {
            var item = new MenuItem { Header = $"{grade}학년", IsCheckable = true, IsChecked = true, StaysOpenOnClick = true };
            grades.Add((grade, item)); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (bool locked in new[] { true, false })
        {
            var item = new MenuItem { Header = locked ? "선택 학년 · 이 교시 잠금" : "선택 학년 · 이 교시 잠금 해제",
                IsEnabled = workMode == WorkMode.Browse && manualDraft is null };
            item.Click += (_, _) =>
            {
                if (busy || !ReferenceEquals(vm.Session, session) || session.Revision != revision) return;
                var selected = grades.Where(x => x.Item.IsChecked).Select(x => x.Grade).ToHashSet();
                if (selected.Count == 0) { ActionHint.Text = "잠금할 학년을 하나 이상 선택하세요."; return; }
                var ids = HeaderLockScope.LessonIds(session.Document.Current, slot.Id, selected);
                if (ids.Count == 0) { ActionHint.Text = "선택한 학년·교시에 수업이 없습니다."; return; }
                ApplyLockPlan(ChangePlanner.SetLocks(session, ids, locked), locked);
            };
            menu.Items.Add(item);
        }
        if (workMode != WorkMode.Browse) menu.Items.Add(new MenuItem { Header = "잠금 변경은 조회 모드에서 사용", IsEnabled = false });
        menu.IsOpen = true;
    }
}
