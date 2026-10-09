namespace TimeTableManager.Application;

public static class TeacherIdentifiers
{
    // Only presentation changes; persisted identities and history remain untouched.
    public static string Display(string? id) => id is null ? "" :
        id.StartsWith("name:", StringComparison.Ordinal) ? id[5..] : id;
}
