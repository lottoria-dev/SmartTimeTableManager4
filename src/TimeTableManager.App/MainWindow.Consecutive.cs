using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private ChangePlan? consecutiveApprovedPlan;
    private ProjectSession? consecutiveApprovedSession;
    private bool ConfirmConsecutivePlan(ChangePlan plan, bool automaticFallback = false, string? exemptTeacher = null)
    {
        if (vm.Session is not { } session || plan.Revision != session.Revision) return false;
        if (ReferenceEquals(consecutiveApprovedPlan, plan) && ReferenceEquals(consecutiveApprovedSession, session)) return true;
        var risks = new ConsecutiveTeaching(session.Document.Current).Evaluate(plan).Where(x => x.TeacherId != exemptTeacher).ToArray();
        if (!ConfirmConsecutive(session, risks, automaticFallback)) return false;
        consecutiveApprovedPlan = plan; consecutiveApprovedSession = session;
        return true;
    }
    private bool ConfirmConsecutive(ProjectSession session, IReadOnlyList<ConsecutiveRisk> risks, bool automaticFallback = false)
    {
        if (risks.Count == 0) return true;
        var revision = session.Revision;
        var state = session.Document.Current; var calendar = session.Document.Calendar;
        string Line(ConsecutiveRisk risk)
        {
            var teacher = state.Teachers.FirstOrDefault(x => x.Id == risk.TeacherId)?.Name ??
                (SpecialCover.IsSpecial(risk.TeacherId) ? SpecialCover.Name(risk.TeacherId) : risk.TeacherId);
            var day = calendar is null ? $"{risk.Day.Week}주 {"월화수목금토일"[risk.Day.Day - 1]}요일" : $"{calendar.DateOf(risk.Day):yyyy/M/d} ({"월화수목금토일"[risk.Day.Day - 1]})";
            return $"• {teacher} · {day} · {risk.FirstPeriod}~{risk.LastPeriod}교시 ({risk.Length}연강)";
        }
        var hint = automaticFallback
            ? "연강 회피 탐색에서 안을 찾지 못했습니다. 아래 3연강 이상을 허용할까요?"
            : "이번 배치로 3연강 이상이 생기거나 늘어납니다. 허용할까요?";
        var accepted = ShowWorkDialog(new ReviewWindow("3연강 이상 배치 확인", hint,
            string.Join("\n", risks.Select(Line)), automaticFallback ? "연강 허용 · 교체안 보기" : "연강 허용",
            (automaticFallback ? "• 제한 탐색 결과이며, 연강 없는 안이 불가능하다는 뜻은 아님\n• 교체안 확인 후 ‘변경 적용’으로 확정\n" : "") +
            "• 허용은 이번 교체안에만 적용\n• 취소하면 이번 배치 중단\n• 잠금·충돌·행사 제외 조건 유지"));
        if (!accepted) ActionHint.Text = "3연강 배치를 취소했습니다. 다른 시간이나 날짜 범위를 검토하세요.";
        return accepted && ReferenceEquals(vm.Session, session) && session.Revision == revision;
    }
}
