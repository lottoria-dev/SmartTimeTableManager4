using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace TimeTableManager.Infrastructure;

internal static class TabularExport
{
    public static void Write(string path, string sheetName, string[] headers, IEnumerable<object[]> rows)
    {
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var book = new XLWorkbook();
            var sheet = book.AddWorksheet(sheetName);
            for (int c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
            int r = 2;
            foreach (var row in rows)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    var cell = sheet.Cell(r, c + 1);
                    if (row[c] is int number) cell.Value = number;
                    else
                    {
                        cell.Style.NumberFormat.Format = "@";
                        cell.Value = Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? "";
                    }
                }
                r++;
            }
            sheet.Row(1).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1);
            sheet.Columns(1, headers.Length).AdjustToContents(1, Math.Min(r - 1, 200));
            book.SaveAs(path);
            return;
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", headers.Select(CsvExporter.Escape)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", row.Select((value, c) => value is int number ? number.ToString(CultureInfo.InvariantCulture) : headers[c] == "학급"
                ? CsvExporter.EscapeClass(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                : CsvExporter.Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))));
    }
}
