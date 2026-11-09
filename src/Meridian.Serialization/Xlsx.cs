using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Meridian.Serialization;

/// <summary>Minimal .xlsx writer and reader (string cells, bold frozen header row, sized columns) on DocumentFormat.OpenXml.</summary>
public static class Xlsx
{
    public sealed record Sheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);

    public static byte[] Write(params Sheet[] sheets)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var styles = wbPart.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new Fonts(new Font(new FontSize { Val = 11 }), new Font(new Bold(), new FontSize { Val = 11 })) { Count = 2 },
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                          new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDCE8FF" }) { PatternType = PatternValues.Solid })) { Count = 3 },
                new Borders(new Border()) { Count = 1 },
                new CellFormats(
                    new CellFormat(),
                    new CellFormat { FontId = 1, FillId = 2, ApplyFont = true, ApplyFill = true },
                    new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top }) { ApplyAlignment = true }) { Count = 3 });
            var sheetList = wbPart.Workbook.AppendChild(new Sheets());
            uint id = 1;
            foreach (var s in sheets)
            {
                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                data.Append(MakeRow(1, s.Header, 1));
                uint r = 2;
                foreach (var row in s.Rows) data.Append(MakeRow(r++, row, 2));
                var widths = Enumerable.Range(0, s.Header.Count).Select(i =>
                    Math.Clamp(s.Rows.Select(x => i < x.Count ? x[i].Length : 0).Append(s.Header[i].Length).Max() + 2, 8, 60)).ToList();
                var cols = new Columns(widths.Select((w, i) => new Column { Min = (uint)i + 1, Max = (uint)i + 1, Width = w, CustomWidth = true }));
                var views = new SheetViews(new SheetView(new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen })
                    { WorkbookViewId = 0 });
                wsPart.Worksheet = new Worksheet(views, cols, data);
                if (s.Rows.Count > 0 && s.Header.Count > 0)
                    wsPart.Worksheet.Append(new AutoFilter { Reference = $"A1:{ColumnName(s.Header.Count - 1)}{s.Rows.Count + 1}" });
                sheetList.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart), SheetId = id++, Name = SafeSheetName(s.Name),
                });
            }
            wbPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static Row MakeRow(uint index, IReadOnlyList<string> values, uint style)
    {
        var row = new Row { RowIndex = index };
        for (var i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrEmpty(values[i]) && style != 1) continue;
            row.Append(new Cell
            {
                CellReference = ColumnName(i) + index,
                DataType = CellValues.InlineString,
                StyleIndex = style,
                InlineString = new InlineString(new Text(Clean(values[i])) { Space = SpaceProcessingModeValues.Preserve }),
            });
        }
        return row;
    }

    /// <summary>Strips characters XML 1.0 cannot carry.</summary>
    private static string Clean(string s) => new(s.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray());

    private static string SafeSheetName(string name)
    {
        var s = new string(name.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        return s.Length == 0 ? "Sheet" : s.Length > 31 ? s[..31] : s;
    }

    public static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var c in cellRef.TakeWhile(char.IsLetter)) n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return n - 1;
    }

    /// <summary>Reads the first worksheet as rows of strings (shared, inline, numeric and boolean cells).</summary>
    public static List<List<string>> Read(Stream stream)
    {
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wb = doc.WorkbookPart ?? throw new FormatException("Not an Excel workbook");
        var first = wb.Workbook.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().FirstOrDefault()
                    ?? throw new FormatException("The workbook has no sheets");
        var ws = (WorksheetPart)wb.GetPartById(first.Id!.Value!);
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        var result = new List<List<string>>();
        foreach (var row in ws.Worksheet.Descendants<Row>())
        {
            var values = new List<string>();
            var next = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var col = cell.CellReference?.Value is { } r ? ColumnIndex(r) : next;
                while (values.Count < col) values.Add("");
                values.Add(CellText(cell, shared));
                next = col + 1;
            }
            // Keep row positions stable: missing rows become empty rows.
            var index = (int)(row.RowIndex?.Value ?? (uint)(result.Count + 1));
            while (result.Count < index - 1) result.Add([]);
            result.Add(values);
        }
        return result;
    }

    private static string CellText(Cell cell, List<string> shared)
    {
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
        var raw = cell.CellValue?.InnerText ?? "";
        if (type == CellValues.SharedString) return int.TryParse(raw, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
        if (type == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }
}
