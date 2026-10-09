using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;

namespace TimeTableManager.Infrastructure;

public sealed record RawTable(string FileName, string Sha256, string Encoding, string Sheet, ImmutableArray<ImmutableArray<string>> Rows)
{
    public string Cell(int row, int column) => row >= 0 && row < Rows.Length && column >= 0 && column < Rows[row].Length ? Rows[row][column].Trim() : "";
}
public static class TabularReader
{
    public const int MaxRows = 100_000;
    public const int MaxColumns = 512;
    private const int MaxFileBytes = 20 * 1024 * 1024;
    public static ImmutableArray<RawTable> Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("입력 파일은 20MB 이하여야 합니다.");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".csv")
        {
            string content;
            string encoding;
            try { content = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); encoding = "UTF-8"; }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                content = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
                encoding = "CP949";
            }
            return [ReadCsvText(content, System.IO.Path.GetFileName(path), hash, encoding)];
        }
        if (extension != ".xlsx") throw new InvalidDataException("CSV와 XLSX를 지원합니다. XLS 파일은 Excel에서 XLSX로 저장한 뒤 가져오세요.");
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            if (zip.Entries.Count > 10_000 || zip.Entries.Sum(x => x.Length) > 128L * 1024 * 1024)
                throw new InvalidDataException("Excel 파일의 압축 해제 크기가 제한을 초과했습니다.");
        }
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var tables = ImmutableArray.CreateBuilder<RawTable>();
        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed(XLCellsUsedOptions.Contents);
            if (used is null) continue;
            var rows = used.RangeAddress.LastAddress.RowNumber;
            var columns = used.RangeAddress.LastAddress.ColumnNumber;
            if (rows > MaxRows || columns > MaxColumns || (long)rows * columns > 2_000_000)
                throw new InvalidDataException("Excel 행·열 범위가 가져오기 제한을 초과했습니다.");
            var cells = new string[rows][];
            for (int r = 0; r < rows; r++)
            {
                cells[r] = new string[columns];
                for (int c = 0; c < columns; c++)
                {
                    var cell = sheet.Cell(r + 1, c + 1);
                    if (cell.HasFormula) throw new InvalidDataException($"{sheet.Name}!{cell.Address}: 수식 대신 값으로 저장한 파일을 사용하세요.");
                    cells[r][c] = cell.GetFormattedString();
                }
            }
            // The two standard grids merge only day headers and class/homeroom labels.
            // Keep anchor values only: filling a merged class into the teacher row duplicates lessons.
            var header = Array.FindIndex(cells, row => row.Length > 1 &&
                (row[0].Trim() is "학반" or "학급" || row[0].Trim() == "학년" && row[1].Trim() == "반"));
            var splitGrade = header >= 0 && cells[header][0].Trim() == "학년";
            var homeroomColumn = header < 0 ? -1 : Array.FindIndex(cells[header], x => x.Trim() is "담임" or "담임교사");
            foreach (var merge in sheet.MergedRanges)
            {
                var a = merge.RangeAddress;
                if (a.LastAddress.RowNumber > rows || a.LastAddress.ColumnNumber > columns) continue;
                var value = cells[a.FirstAddress.RowNumber - 1][a.FirstAddress.ColumnNumber - 1];
                var dayHeader = a.FirstAddress.RowNumber == a.LastAddress.RowNumber &&
                    System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^(?:[1-4]주\s*)?[월화수목금토일](?:요일)?$");
                var firstRow = a.FirstAddress.RowNumber - 1;
                var lastRow = a.LastAddress.RowNumber - 1;
                var column = a.FirstAddress.ColumnNumber - 1;
                var vertical = column == a.LastAddress.ColumnNumber - 1;
                var headerLabel = header >= 0 && vertical && firstRow == header && lastRow == header + 1 &&
                    value.Trim() is "학반" or "학급" or "학년" or "반" or "담임" or "담임교사";
                var gradeLabel = header >= 0 && splitGrade && vertical && column == 0 && firstRow >= header + 2 &&
                    int.TryParse(value, out var grade) && grade > 0 &&
                    Enumerable.Range(firstRow, lastRow - firstRow + 1).All(r => int.TryParse(cells[r][1].Trim(), out var cls) && cls > 0);
                var pairedRows = header >= 0 && !splitGrade && vertical && firstRow >= header + 2 && lastRow == firstRow + 1 &&
                    System.Text.RegularExpressions.Regex.IsMatch(cells[firstRow][0].Trim(), @"^\d+[\-\.\s]+\d+$") &&
                    string.IsNullOrWhiteSpace(cells[lastRow][0]);
                var classOrHomeroom = pairedRows && (column == 0 || column == homeroomColumn);
                if (!dayHeader && !headerLabel && !gradeLabel && !classOrHomeroom)
                    throw new InvalidDataException($"{sheet.Name}!{merge.RangeAddress}: 표준 양식의 학급·담임·요일 병합이 아닙니다. 병합된 수업을 임의로 복제하지 않습니다.");
            }
            tables.Add(new(System.IO.Path.GetFileName(path), hash, "XLSX", sheet.Name, cells.Select(x => x.ToImmutableArray()).ToImmutableArray()));
        }
        if (tables.Count == 0) throw new InvalidDataException("읽을 수 있는 시트가 없습니다.");
        return tables.ToImmutable();
    }
    public static RawTable ReadCsvText(string content, string fileName = "memory.csv", string hash = "synthetic", string encoding = "UTF-8")
    {
        using var parser = new TextFieldParser(new StringReader(content));
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        try
        {
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? [];
                if (rows.Count >= MaxRows || fields.Length > MaxColumns) throw new InvalidDataException("CSV 행·열 제한을 초과했습니다.");
                rows.Add(fields.ToImmutableArray());
            }
        }
        catch (MalformedLineException ex) { throw new InvalidDataException($"CSV {parser.ErrorLineNumber}줄의 따옴표 형식이 잘못되었습니다.", ex); }
        return new(fileName, hash, encoding, "CSV", rows.ToImmutable());
    }
}
