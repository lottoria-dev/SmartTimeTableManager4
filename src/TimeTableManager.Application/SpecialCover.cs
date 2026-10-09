using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public static class SpecialCover
{
    public const string Principal = "special-cover:principal";
    public const string VicePrincipal = "special-cover:vice-principal";
    public static bool IsSpecial(string id) => id is Principal or VicePrincipal;
    public static string Name(string id) => id == Principal ? "교장" : id == VicePrincipal ? "교감" : throw new ArgumentException("알 수 없는 특별 보강 담당입니다.");
    internal static ProjectDocument WithTeacher(ProjectDocument document, string id)
    {
        var teacher = new Teacher(id, Name(id));
        if (document.Current.Teachers.Any(x => x.Id == id)) return document;
        return document with
        {
            Baseline = document.Baseline with { Teachers = document.Baseline.Teachers.Add(teacher) },
            Current = document.Current with { Teachers = document.Current.Teachers.Add(teacher) }
        };
    }
    public static IReadOnlyList<CoverCandidate> Candidates(ProjectSession session, string lessonId)
    {
        var document = WithTeacher(WithTeacher(session.Document, Principal), VicePrincipal);
        var draft = new ProjectSession(document);
        return TeachingAdvice.CoverCandidates(draft, lessonId).Where(x => IsSpecial(x.TeacherId)).ToArray();
    }
}
