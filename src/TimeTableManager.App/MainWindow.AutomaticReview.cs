using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private readonly AutomaticPlanApproval automaticApproval = new();
    private enum AutomaticReviewResult { Approved, Retry, Cancelled }
    private AutomaticReviewResult ReviewAutomaticPlan(ProjectSession session, ChangePlan plan, IReadOnlySet<string> requested,
        AutomaticSearchRestrictions restrictions, bool placementFirst = false, string? consecutiveExemptTeacher = null)
    {
        var revision = session.Revision;
        if (!ReferenceEquals(vm.Session, session) || plan.Revision != revision) return AutomaticReviewResult.Cancelled;
        var groups = AutomaticPlanReview.Groups(session, plan, requested, consecutiveExemptTeacher);
        if (groups.Any(x => !x.Additional.IsEmpty || x.PreviouslyChanged.Count > 0 || !x.Risks.IsEmpty))
        {
            var dialog = new AutomaticMovementWindow(session, groups, placementFirst);
            if (!ShowWorkDialog(dialog) || !ReferenceEquals(vm.Session, session) || session.Revision != revision)
            { ActionHint.Text = "이동 허용을 취소했습니다. 시간표는 바뀌지 않았습니다."; return AutomaticReviewResult.Cancelled; }
            if (dialog.DeniedMovementTeachers.Count > 0 || dialog.DeniedConsecutiveTeachers.Count > 0)
            {
                restrictions.Refuse(dialog.DeniedMovementTeachers, dialog.DeniedConsecutiveTeachers);
                automaticApproval.Reset();
                consecutiveApprovedPlan = null; consecutiveApprovedSession = null;
                ActionHint.Text = "허용하지 않은 교사의 추가 수업과 연강 조건을 유지하고 대체안을 찾습니다.";
                return AutomaticReviewResult.Retry;
            }
        }
        automaticApproval.Approve(session, plan);
        consecutiveApprovedPlan = plan; consecutiveApprovedSession = session;
        return AutomaticReviewResult.Approved;
    }
}
