using System.Text.RegularExpressions;

namespace TimeTableManager.Infrastructure;

public static class TimestampedFileNames
{
    public static string Stamp(string path, DateTime localTime)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var extension = Path.GetExtension(path);
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"_\d{8}_\d{6}(?:_\d+)?$", "", RegexOptions.CultureInvariant);
        return Path.Combine(directory, stem + "_" + localTime.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + extension);
    }
    public static string Next(string path, DateTime localTime, Func<string, bool>? exists = null)
    {
        exists ??= candidate => File.Exists(candidate) || File.Exists(candidate + ".editing");
        var stamped = Stamp(path, localTime);
        if (!exists(stamped)) return stamped;
        var directory = Path.GetDirectoryName(stamped) ?? "";
        var stem = Path.GetFileNameWithoutExtension(stamped); var extension = Path.GetExtension(stamped);
        for (int sequence = 2; sequence <= 10000; sequence++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{sequence:00}{extension}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException("동일 시각의 저장 파일이 너무 많습니다. 다른 파일명을 지정하세요.");
    }
}
