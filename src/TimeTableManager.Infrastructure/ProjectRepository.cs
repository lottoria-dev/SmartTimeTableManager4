using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.Infrastructure;

/// <summary>A held OS file lock and disk fingerprint prevent accidental second-writer saves.</summary>
public sealed class ProjectFile : IDisposable
{
    private readonly FileStream lease;
    private string? fingerprint;
    public string Path { get; }
    public ProjectFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        lease = new FileStream(Path + ".editing", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        fingerprint = File.Exists(Path) ? Hash(Path) : null;
    }
    public ProjectDocument Load()
    {
        var document = ProjectRepository.Read(Path);
        fingerprint = Hash(Path);
        return document;
    }
    public void Save(ProjectDocument document)
    {
        var actual = File.Exists(Path) ? Hash(Path) : null;
        if (actual != fingerprint) throw new IOException("파일이 다른 작업에서 변경되었습니다. 다른 이름으로 저장하거나 다시 여세요.");
        ProjectRepository.WriteAtomic(Path, document);
        fingerprint = Hash(Path);
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public void Dispose() => lease.Dispose(); // The reusable lock marker is not ownership evidence; the OS handle is.
}

public static class ProjectRepository
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false, MaxDepth = 64 };
    private const long MaxBytes = 100 * 1024 * 1024;

    public static ProjectDocument Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("프로젝트 파일 크기 제한(100MB)을 초과했습니다.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        if (!Equals(cmd.ExecuteScalar(), "ok")) throw new InvalidDataException("프로젝트 데이터베이스가 손상되었습니다.");
        cmd.CommandText = "SELECT schema_version, payload, sha256 FROM project WHERE id=1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1) throw new InvalidDataException("지원하지 않는 프로젝트 파일입니다.");
        var json = reader.GetString(1);
        if (Digest(json) != reader.GetString(2)) throw new InvalidDataException("프로젝트 검증값이 일치하지 않습니다.");
        ProjectDocument doc;
        try { doc = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions) ?? throw new InvalidDataException("프로젝트 내용이 비었습니다."); }
        catch (JsonException ex) { throw new InvalidDataException("프로젝트 형식이 손상되었습니다.", ex); }
        DocumentIntegrity.EnsureValid(doc);
        return doc;
    }

    public static void WriteAtomic(string path, ProjectDocument document)
    {
        DocumentIntegrity.EnsureValid(document);
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes / 2) throw new InvalidDataException("프로젝트 내용이 저장 한도를 초과했습니다.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE project(id INTEGER PRIMARY KEY CHECK(id=1),schema_version INTEGER NOT NULL,payload TEXT NOT NULL,sha256 TEXT NOT NULL);";
                command.ExecuteNonQuery();
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO project VALUES (1,1,$payload,$hash)";
                command.Parameters.AddWithValue("$payload", json);
                command.Parameters.AddWithValue("$hash", Digest(json));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            // Only closed, fully committed databases are replaced/copied.
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
