using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

/// <summary>Built-in, offline help. Opening it never touches the timetable or the current draft.</summary>
public sealed class HelpWindow : Window
{
    private sealed record Topic(string Title, string Body);
    private static readonly Topic[] Topics =
    [
        new("처음 사용하기", "• 파일 → 시간표 파일 열기\n• templates: 동일셀·분리셀 Excel 중 하나 편집\n• 동일셀: 교과명↵교사명 / 분리셀: 교과 행+교사 행\n• Excel·CSV: 기초 시간표 / .ttm: 저장한 작업\n• 관리 월 확정 → 편집 허용 시작일 확인\n• 교사 전체·학급 전체 선택 → 작업 모드 선택\n• 교체 후 Ctrl+S로 저장\n\n표가 비어 있으면\n• 조회 범위를 관리 월 전체·시작 주로 변경"),
        new("조회 모드", "• 셀 클릭: 같은 교사의 수업 강조\n• 같은 셀 재클릭: 선택 해제\n• 변경된 행만 보기: 변동 교사·학급만 조회\n• 찾기: 교사·과목·학급 검색\n• 흐린 ‘변경’: 원래 수업이 이동한 자리\n• ‘보’: 보강 수업. 마우스를 올려 담당 교사 확인\n• 긴 이름: 마우스를 올리거나 첫 열 폭 확대"),
        new("맞교환 모드", "• 첫 수업 클릭 → 초록색 후보 확인\n• 상대 수업 클릭 → 확인 후 맞교환\n• 새 3연강 이상은 추가 확인\n• 같은 교사끼리의 교환은 제외\n• 교사 전체: 후보 교사를 선택 교사 옆에 모음\n• 후보 없음: 조회 범위 확대 또는 다른 모드 사용"),
        new("보강 모드", "• 수업 클릭 → 공강 교사 선택\n• 교장·교감 특별 보강도 선택 가능\n• *: 배치 후 3연강 이상. 새 연강은 확인 후 적용\n• 수업 시간은 유지. 담당 교사만 변경\n• 셀 팁: 원래 교사·새 교사 표시"),
        new("수동 교체 모드", "• 수업 선택 → 목적 칸 클릭·드롭\n• 밀려난 수업은 대기 카드로 이동\n• 진한 초록: 추천 / 연한 초록: 배치 가능\n• 회색: 배치 불가 / !: 3연강 주의\n• 셀 팁에서 밀려날 과목·교사 확인\n• 직전 자리와 본인 수업 밀어내기는 주의\n• 대기 카드 재클릭은 선택 유지\n• 새 연강은 허용 확인. 모두 배치 후 변경 적용\n• Ctrl+Z: 한 단계 뒤로 / Esc: 초안 취소"),
        new("AI 자동 모드", "• 시작 수업 → 목적 시간 → 자동 교체안 찾기\n• 지정 목적은 고정. 다른 시간으로 대체하지 않음\n• 본인의 다른 수업도 교사별 허용 선택\n• 3연강 회피 우선. 필요 시 교사별 허용 선택\n• 당일 실패 시 이전·이후·모두로 범위 확대\n• 교체안 확인 → 변경 적용\n• 재변경 표시 확인. 미허용 교사는 유지 후 재탐색\n• Esc: 탐색 중단\n• 제한 탐색 실패 ≠ 교체 불가능"),
        new("재변경·날짜 허용", "• 이동·보강 이력이 있는 수업은 재변경 확인\n• AI: 교사별 허용 선택. 대체안은 다시 확인\n• 허용은 현재 작업에만 적용\n• 잠금만 변경하거나 Undo한 이력은 제외\n• 날짜 기준: 처음 선택한 수업의 실제 날짜\n• 편집 허용 시작일: 이전 수업의 편집 차단\n• 과거 날짜 검토는 시작일을 앞당겨 허용\n• 이전·이후 허용은 연쇄 수업 전체에 적용\n• 잠금·행사 제외·편집 허용 시작일은 항상 유지\n• 월 경계 이동 후 양쪽 월 통계 확인"),
        new("교사 하루 수업 이동", "• 수업 우클릭 → 교사 하루 수업 이동\n• 오전·오후 몰기: 앞·뒤 교시부터 배치. 본인 연강 허용\n• 하루·오전 비우기: 가까운 날짜로 이동\n• 배치 우선 → 연강 확인 → 필요한 수업만 수동 조정\n• 오후·다른 날짜의 기존 본인 수업 유지\n• 적용 후 확인 목록 제공. Ctrl+Z로 전체 복원"),
        new("저장·월 통계", "• .ttm: 원본·배정·잠금·날짜·변경 이력 보존\n• Excel·CSV: 배정 결과 전달용\n• Excel 편집은 XLSX 권장\n• 다음 월: 최신 .ttm에서 관리 월 변경\n• 독립 편집한 여러 파일은 자동 병합 불가\n• 통계: 학교 계획을 반영한 기초 배정과 현재 배정의 최종 차이\n• 실제 수업 실시·미실시 기록과는 별개"),
        new("잠금·학년 행사", "• 조회 모드 → 수업 우클릭 → 잠금·해제\n• 다중 잠금: 셀 선택 후 잠금·해제\n• 교사명 우클릭: 해당 교사의 전체 기간 잠금\n• 날짜 제목 우클릭: 학년 행사 제외\n• 교시 제목 우클릭: 해당 시간 학년별 잠금\n• 흐린 제외 수업은 이동·보강 불가\n• 전체 초기화: 현재 기초 배정 복귀, 교체 이력 삭제\n• 적용한 기초 요일 계획은 유지\n• 초기화는 Undo 불가. 먼저 저장"),
        new("단축키·마우스", "• F1: 현재 모드 도움말\n• Ctrl+O: 열기 / Ctrl+S: 저장\n• Ctrl+Z: 되돌리기 / Ctrl+Y: 다시 실행\n• Esc: 선택·초안 취소, AI 탐색 중단\n• 우클릭: 수업 정보·잠금·하루 이동\n• 열 경계 드래그: 이름·수업 열 폭 조절\n• Alt+↓: 목록 열기 / 방향키: 항목 선택\n• 입력칸에서는 텍스트 편집 단축키 우선\n• 도움말을 닫아도 대기 카드 유지"),
        new("기초 요일 시간표 적용", "• 날짜 제목 우클릭 또는 편집 → 기초 요일 적용\n• 변경 날짜 + 원래 기초 시간표의 요일 선택\n• 선택 날짜만 변경. 6→7교시도 반영\n• 잠금이 있으면 확인. 행사 제외·부재는 계속 검증\n• 학교 계획: 교체 이력·변동 색상에서 제외\n• Ctrl+Z 대상 아님. 바꾸려면 다시 요일 적용"),
        new("사례 · 지정 시간 이동", "상황: 지각·회의로 수요일 1교시를 6교시로 이동\n\n• AI 자동 → 1교시 수업 → 6교시 목적 칸 선택\n• 당일 안 찾기 → 어려우면 이전·이후 날짜 허용\n• 지정한 6교시는 고정. 함께 바뀌는 수업 확인 후 적용"),
        new("사례 · 갑작스러운 결근", "상황: 당일 결근으로 담당 교사 변경\n\n• 보강 모드 → 해당 수업 → 공강 교사 선택\n• * 표시 교사의 연강을 확인하고 배정\n• 교장·교감 특별 보강도 가능. 셀 팁에서 원래·새 담당 확인"),
        new("사례 · 하루·오전 비우기", "상황: 출장 또는 오전 외부 업무\n\n• 수업 우클릭 → 하루 모두 / 오전만 다른 날짜로 이동\n• 이후 날짜 또는 이전·이후 범위 선택\n• 전체 배치안 확인 → 적용 → 연강 수업만 수동 조정"),
        new("사례 · AI+수동 조정", "상황: AI 배치 후 B교사가 특정 시간 불가 통보\n\n• AI 교체안 확인 후 변경 적용\n• 수동 교체 → B교사 수업 → 새 목적 칸 선택\n• 재변경 확인 → 밀려난 카드를 초록색 시간에 순서대로 배치\n• 모든 카드 배치 후 적용·저장. 연한 초록도 배치 가능"),
        new("사례 · 행사·요일 변경", "상황: 2학년 행사 / 월요일에 목요일 일과 운영\n\n• 행사: 날짜 제목 우클릭 → 해당 학년 제외\n• 일과 변경: 날짜 제목 우클릭 → 기초 요일 적용 → 목요일\n• 기초 요일 적용은 학교 계획. 교체 이력·색상 제외\n• 적용 전 저장 권장. Ctrl+Z 대신 기초 요일을 다시 적용")
    ];
    private readonly ListBox navigation;
    private readonly TextBlock heading;
    private readonly TextBlock body;
    private readonly ScrollViewer scroll;
    public HelpWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "도움말 · Smart Timetable Manager 4"; Width = 920; Height = 660; MinWidth = 700; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "닫기", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0), MinWidth = 80 };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        navigation = new ListBox { ItemsSource = Topics, DisplayMemberPath = nameof(Topic.Title), Width = 190,
            Background = (Brush)FindResource("AquaGlass"), BorderBrush = (Brush)FindResource("AquaEdge"), Padding = new Thickness(5), Margin = new Thickness(0,0,12,0) };
        var itemStyle = new Style(typeof(ListBoxItem)); itemStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(9,8,9,8)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        navigation.ItemContainerStyle = itemStyle; DockPanel.SetDock(navigation, Dock.Left); panel.Children.Add(navigation);
        heading = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,16) };
        body = new TextBlock { FontSize = 14, LineHeight = 24, TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel(); content.Children.Add(heading); content.Children.Add(body);
        scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = (Brush)FindResource("AquaEdge"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Child = scroll });
        Content = panel;
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is not Topic topic) return;
            heading.Text = topic.Title; body.Text = topic.Body; scroll.ScrollToTop();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        SelectTopic(0);
    }
    public void SelectTopic(int index) => navigation.SelectedIndex = Math.Clamp(index,0,Topics.Length - 1);
}
