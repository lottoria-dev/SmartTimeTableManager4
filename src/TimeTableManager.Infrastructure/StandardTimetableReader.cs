using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

public static class StandardTimetableReader
{
    public static IReadOnlyList<ProjectDocument> Read(string path)
    {
        var documents = new List<ProjectDocument>(); var errors = new List<string>();
        foreach (var table in TabularReader.Read(path))
        {
            var profile = LongTableImporter.Suggest(table);
            var isList = profile.ClassColumn >= 0 && profile.DayColumn >= 0 && profile.PeriodColumn >= 0 && profile.SubjectColumn >= 0;
            var preview = isList ? new LongTableImporter().Parse(table,profile) : new LegacyCsvImporter().Parse(table);
            if (preview.CanImport) documents.Add(preview.Document!);
            else errors.AddRange(preview.Issues.Where(x=>x.Severity==Severity.Error).Take(6).Select(x=>$"{table.Sheet} · {x.Location} · {x.Message}"));
        }
        if (documents.Count == 0) throw new InvalidDataException("표준 전체 시간표를 읽지 못했습니다. 교과명·교사명 동일셀 또는 분리셀 양식을 확인하세요.\n" + string.Join("\n",errors.Take(12)));
        return documents;
    }
}
