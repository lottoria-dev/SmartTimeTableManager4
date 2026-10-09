using TimeTableManager.Application;

namespace TimeTableManager.Infrastructure;

public static class MonthlyReportExporter
{
    public static void Write(string path, MonthlyReport report, bool details)
    {
        if (details)
            TabularExport.Write(path, "변동 상세", ["통계월", "수업ID", "학급", "과목", "원래교사", "현재교사", "기초일시", "현재일시", "월경계"],
                report.Changes.Select(x => new object[] { report.Month.ToString("yyyy-MM"), x.LessonId, x.Class, x.Subject,
                    x.OriginalTeacher, x.Teacher, x.Before, x.After, x.CrossMonth ? "예" : "" }));
        else
            TabularExport.Write(path, "교사별 통계", ["통계월", "교사", "기초배정", "담당교체결강", "담당교체보강", "최종배정", "증감"],
                report.Teachers.Select(x => new object[] { report.Month.ToString("yyyy-MM"), x.Teacher,
                    x.Planned, x.CoverOut, x.CoverIn, x.Assigned, x.Difference }));
    }
}
