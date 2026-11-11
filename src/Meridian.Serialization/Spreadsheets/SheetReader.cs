using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Meridian.Serialization.Spreadsheets;

/// <summary>Size caps for spreadsheet imports. Uploaded workbooks are untrusted: every limit fails with a clear message.</summary>
public static class SheetLimits
{
    /// <summary>Data rows read from one sheet (the header row is not counted).</summary>
    public const int MaxRows = 100_000;
    public const int MaxColumns = 1_000;
    /// <summary>Excel's own cell limit; longer cells are cut with a warning.</summary>
    public const int MaxCellChars = 32_767;
    /// <summary>Uploaded file size (.xlsx, .csv, .tsv or pasted text).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;
    /// <summary>Decompressed size of one part of an .xlsx (shared strings, one worksheet).</summary>
    public const long MaxPartBytes = SafeZip.MaxEntryBytes;
    public const int MaxZipEntries = 10_000;
    public const int MaxSheets = 256;
    public const int MaxSharedStrings = 2_000_000;
}

/// <summary>One sheet of a workbook (or the single table of a delimited file) as rows of strings.</summary>
public sealed class SheetTable
{
    public required string Name { get; init; }
    public List<List<string>> Rows { get; init; } = [];
    /// <summary>Excel cell indent levels (alignment indent), keyed by (row, column), zero-based. Only cells with an indent are listed.</summary>
    public Dictionary<(int Row, int Col), int> Indents { get; init; } = [];
    /// <summary>Cells that held a formula; only their cached values were read.</summary>
    public int FormulaCells { get; set; }
    /// <summary>Indent level of a cell: Excel's alignment indent, or leading spaces (2 per level) / tabs in the text.</summary>
    public int IndentOf(int row, int col)
    {
        if (Indents.TryGetValue((row, col), out var n)) return n;
        if (row < 0 || row >= Rows.Count || col < 0 || col >= Rows[row].Count) return 0;
        var s = Rows[row][col];
        var spaces = 0;
        foreach (var c in s)
        {
            if (c == ' ' || c == ' ') spaces++;
            else if (c == '\t') spaces += 2;
            else break;
        }
        return spaces / 2;
    }
}

/// <summary>A read workbook: its sheets, how it was read, and any warnings (formulas, external links, cut cells).</summary>
public sealed class SheetBook
{
    public required string Format { get; init; }
    public List<SheetTable> Sheets { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    /// <summary>Delimited files: the delimiter and encoding used (detected or chosen).</summary>
    public char? Delimiter { get; init; }
    public string? EncodingName { get; init; }
}

/// <summary>
/// Reads .xlsx workbooks (every sheet), .csv / .tsv / .txt files (delimiter and encoding detected, both overridable) and
/// pasted clipboard cells. The .xlsx reader walks the zip itself through <see cref="SafeZip"/> limits with a forward-only
/// XML reader (no DTDs, no external resolution); formulas are never evaluated (cached values only) and external links are
/// ignored.
/// </summary>
public static class SheetReader
{
    public static readonly string[] Extensions = [".xlsx", ".xlsm", ".csv", ".tsv", ".txt"];

    public static bool IsSheetFile(string fileName) => Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Delimiters offered and detected: comma, semicolon (European Excel), tab and pipe.</summary>
    public static readonly char[] Delimiters = [',', ';', '\t', '|'];

    /// <summary>Encodings offered for override (names accepted by <see cref="EncodingByName"/>).</summary>
    public static readonly string[] EncodingNames = ["utf-8", "utf-16le", "utf-16be", "windows-1252", "iso-8859-1"];

    /// <summary>Reads a file by extension. <paramref name="delimiter"/> and <paramref name="encoding"/> override detection for delimited files.</summary>
    public static SheetBook Read(string fileName, Stream content, char? delimiter = null, string? encoding = null)
    {
        var bytes = ReadAllBounded(content);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm" || ext is not (".csv" or ".tsv" or ".txt") && LooksLikeZip(bytes))
            return ReadXlsx(bytes);
        if (LooksLikeZip(bytes)) throw new FormatException("This file is a zip archive, not a text file; save it as .xlsx or CSV");
        return ReadDelimited(bytes, Path.GetFileNameWithoutExtension(fileName), delimiter ?? (ext == ".tsv" ? '\t' : null), encoding);
    }

    private static bool LooksLikeZip(byte[] b) => b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;

    private static byte[] ReadAllBounded(Stream s)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > SheetLimits.MaxFileBytes) throw TooBig();
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static FormatException TooBig() => new($"The file is larger than {SheetLimits.MaxFileBytes / (1024 * 1024)} MB");

    // ------------------------------------------------------------------ delimited text

    /// <summary>Pasted cells (Excel puts tab-separated text on the clipboard).</summary>
    public static SheetBook ReadText(string text, char? delimiter = null)
    {
        if (text.Length > SheetLimits.MaxFileBytes) throw TooBig();
        var d = delimiter ?? DetectDelimiter(text);
        var warnings = new List<string>();
        var table = new SheetTable { Name = "Pasted cells", Rows = ParseDelimited(text, d, warnings) };
        return new SheetBook { Format = "paste", Sheets = [table], Warnings = warnings, Delimiter = d, EncodingName = "utf-16" };
    }

    public static SheetBook ReadDelimited(byte[] bytes, string name = "Sheet1", char? delimiter = null, string? encoding = null)
    {
        if (bytes.Length > SheetLimits.MaxFileBytes) throw TooBig();
        var (enc, bom) = encoding is { Length: > 0 } ? (EncodingByName(encoding), BomLength(bytes, EncodingByName(encoding))) : DetectEncoding(bytes);
        var text = enc.GetString(bytes, bom, bytes.Length - bom);
        var d = delimiter ?? DetectDelimiter(text);
        var warnings = new List<string>();
        var table = new SheetTable { Name = name.Length > 0 ? name : "Sheet1", Rows = ParseDelimited(text, d, warnings) };
        return new SheetBook { Format = d == '\t' ? "tsv" : "csv", Sheets = [table], Warnings = warnings, Delimiter = d, EncodingName = enc.WebName };
    }

    public static Encoding EncodingByName(string name)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "utf-8" or "utf8": return new UTF8Encoding(false);
            case "utf-16" or "utf-16le" or "unicode": return Encoding.Unicode;
            case "utf-16be": return Encoding.BigEndianUnicode;
            case "iso-8859-1" or "latin1": return Encoding.Latin1;
            case "windows-1252" or "cp1252" or "ansi":
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            default: throw new FormatException($"Unknown encoding '{name}'");
        }
    }

    private static int BomLength(byte[] b, Encoding enc)
    {
        var pre = enc.GetPreamble();
        if (pre.Length == 0 && enc is UTF8Encoding) pre = [0xEF, 0xBB, 0xBF];
        return pre.Length > 0 && b.AsSpan().StartsWith(pre) ? pre.Length : 0;
    }

    /// <summary>BOM first; then strict UTF-8; then UTF-16 without BOM (many zero bytes); otherwise Windows-1252 (Excel's "CSV" on Windows).</summary>
    public static (Encoding Encoding, int BomLength) DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode, 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode, 2);
        var sample = Math.Min(b.Length, 4096);
        if (sample >= 4)
        {
            int evenZero = 0, oddZero = 0;
            for (var i = 0; i < sample; i++) if (b[i] == 0) { if (i % 2 == 0) evenZero++; else oddZero++; }
            if (oddZero > sample / 4 && evenZero == 0) return (Encoding.Unicode, 0);
            if (evenZero > sample / 4 && oddZero == 0) return (Encoding.BigEndianUnicode, 0);
        }
        try
        {
            new UTF8Encoding(false, true).GetCharCount(b);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (EncodingByName("windows-1252"), 0);
        }
    }

    /// <summary>
    /// Picks the delimiter whose per-line count (outside quotes) over the first lines is highest and most consistent.
    /// A single-column file falls back to a comma.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        var lines = new List<Dictionary<char, int>>();
        var counts = Delimiters.ToDictionary(d => d, _ => 0);
        var inQuotes = false;
        for (var i = 0; i < text.Length && lines.Count < 30; i++)
        {
            var c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c == '\n')
            {
                if (counts.Values.Any(v => v > 0)) lines.Add(counts);
                counts = Delimiters.ToDictionary(d => d, _ => 0);
            }
            else if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }
        if (counts.Values.Any(v => v > 0)) lines.Add(counts);
        if (lines.Count == 0) return ',';
        char best = ',';
        double bestScore = 0;
        foreach (var d in Delimiters)
        {
            var values = lines.Select(l => l[d]).ToList();
            var linesWith = values.Count(v => v > 0);
            if (linesWith == 0) continue;
            var mode = values.Where(v => v > 0).GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            // Lines agreeing on the same count matter most; a tab beats a comma at equal scores (pasted cells).
            var score = mode.Count() * 1000.0 / lines.Count + Math.Min(mode.Key, 50) + (d == '\t' ? 0.5 : 0);
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    /// <summary>RFC 4180 parsing with any delimiter (quoted fields, doubled quotes, embedded newlines), within the row / column / cell limits.</summary>
    public static List<List<string>> ParseDelimited(string text, char delimiter, List<string>? warnings = null)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var cut = 0;
        void EndField()
        {
            if (row.Count >= SheetLimits.MaxColumns) throw new FormatException($"Row {rows.Count + 1} has more than {SheetLimits.MaxColumns} columns");
            var s = Meridian.Core.Analysis.CsvSafety.Restore(field.ToString());
            if (s.Length > SheetLimits.MaxCellChars) { s = s[..SheetLimits.MaxCellChars]; cut++; }
            row.Add(s);
            field.Clear();
        }
        void EndRow()
        {
            EndField();
            if (rows.Count > SheetLimits.MaxRows) throw RowLimit();
            rows.Add(row);
            row = [];
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else if (field.Length <= SheetLimits.MaxCellChars) field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) EndField();
            else if (c == '\r') { }
            else if (c == '\n') EndRow();
            else if (field.Length <= SheetLimits.MaxCellChars) field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        if (cut > 0) warnings?.Add($"{cut} cell(s) were longer than {SheetLimits.MaxCellChars:N0} characters and were cut");
        return rows;
    }

    private static FormatException RowLimit() => new($"The sheet has more than {SheetLimits.MaxRows:N0} rows; split it into smaller files");

    /// <summary>
    /// Guesses the header row among the first 20: the first row that fills at least half as many cells as the widest
    /// row and holds only text (title rows above a table and numeric data rows are skipped). 0 when unsure.
    /// </summary>
    public static int DetectHeaderRow(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sample = rows.Take(20).ToList();
        if (sample.Count == 0) return 0;
        static int Filled(IReadOnlyList<string> r) => r.Count(c => !string.IsNullOrWhiteSpace(c));
        var widest = sample.Max(Filled);
        if (widest <= 1) return 0;
        for (var i = 0; i < sample.Count; i++)
        {
            var r = sample[i];
            var filled = Filled(r);
            if (filled < Math.Max(2, (widest + 1) / 2)) continue;
            var cells = r.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            if (cells.Any(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) continue;
            if (cells.Distinct(StringComparer.OrdinalIgnoreCase).Count() != cells.Count) continue;
            return i;
        }
        return 0;
    }

    // ------------------------------------------------------------------ .xlsx

    private sealed record Style(int Indent, bool IsDate);

    public static SheetBook ReadXlsx(byte[] bytes)
    {
        if (bytes.Length > SheetLimits.MaxFileBytes) throw TooBig();
        // Size limits of the zip (SafeZip) surface as FormatException, so the message ("… larger than 64 MB when unpacked") reaches the user.
        try { return ReadXlsxParts(bytes); }
        catch (InvalidDataException ex) { throw new FormatException(ex.Message, ex); }
    }

    private static SheetBook ReadXlsxParts(byte[] bytes)
    {
        using var zip = OpenZip(bytes);
        if (zip.Entries.Count > SheetLimits.MaxZipEntries) throw new FormatException("The workbook has too many parts");
        var warnings = new List<string>();
        var workbook = Entry(zip, "xl/workbook.xml") ?? throw new FormatException("Not an Excel workbook (xl/workbook.xml is missing)");

        // Sheet names and their relationship ids, then the relationship targets.
        var sheetRefs = new List<(string Name, string RelId, bool Hidden)>();
        using (var r = XmlFor(workbook))
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                {
                    var relId = Attr(r, "id") ?? "";
                    sheetRefs.Add((r.GetAttribute("name") ?? $"Sheet{sheetRefs.Count + 1}", relId, r.GetAttribute("state") is "hidden" or "veryHidden"));
                    if (sheetRefs.Count > SheetLimits.MaxSheets) throw new FormatException($"The workbook has more than {SheetLimits.MaxSheets} sheets");
                }
        var targets = new Dictionary<string, (string Target, string Type)>(StringComparer.Ordinal);
        if (Entry(zip, "xl/_rels/workbook.xml.rels") is { } rels)
            using (var r = XmlFor(rels))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") is { } id)
                    {
                        if (r.GetAttribute("TargetMode") == "External") continue;   // never follow external targets
                        targets[id] = (r.GetAttribute("Target") ?? "", r.GetAttribute("Type") ?? "");
                    }

        if (zip.Entries.Any(e => e.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("The workbook links to other workbooks; those links were ignored (only the values stored in this file were read)");

        var shared = ReadSharedStrings(zip);
        var styles = ReadStyles(zip);

        var book = new SheetBook { Format = "xlsx", Warnings = warnings };
        foreach (var (name, relId, hidden) in sheetRefs)
        {
            if (!targets.TryGetValue(relId, out var t) || !t.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;   // chartsheets, dialogs
            var path = t.Target.StartsWith('/') ? t.Target.TrimStart('/') : "xl/" + t.Target;
            path = NormalizePath(path);
            if (Entry(zip, path) is not { } entry) continue;
            var table = ReadWorksheet(entry, name + (hidden ? " (hidden)" : ""), shared, styles, warnings);
            book.Sheets.Add(table);
        }
        if (book.Sheets.Count == 0) throw new FormatException("The workbook has no worksheets");
        var formulas = book.Sheets.Sum(s => s.FormulaCells);
        if (formulas > 0)
            warnings.Add($"{formulas} cell(s) hold formulas: formulas are not evaluated, the values Excel last calculated and saved are used");
        return book;
    }

    private static ZipArchive OpenZip(byte[] bytes)
    {
        try { return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new FormatException("The file is not a valid .xlsx workbook"); }
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    private static ZipArchiveEntry? Entry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static XmlReader XmlFor(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0, CloseInput = true,
        };
        return DepthLimitedXmlReader.Create(SafeZip.Open(entry, SheetLimits.MaxPartBytes), settings, 64);
    }

    private static string? Attr(XmlReader r, string localName)
    {
        if (!r.HasAttributes) return null;
        for (var i = 0; i < r.AttributeCount; i++)
        {
            r.MoveToAttribute(i);
            if (r.LocalName == localName) { var v = r.Value; r.MoveToElement(); return v; }
        }
        r.MoveToElement();
        return null;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (Entry(zip, "xl/sharedStrings.xml") is not { } e) return list;
        using var r = XmlFor(e);
        StringBuilder? current = null;
        var phonetic = 0;
        var positioned = false;   // ReadElementContentAsString already moved to the next node
        while (positioned || r.Read())
        {
            positioned = false;
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.LocalName == "si") { current = new StringBuilder(); if (r.IsEmptyElement) { list.Add(""); current = null; } }
                else if (r.LocalName == "rPh" && !r.IsEmptyElement) phonetic++;
                else if (r.LocalName == "t" && !r.IsEmptyElement)
                {
                    var s = r.ReadElementContentAsString();
                    positioned = true;
                    if (current != null && phonetic == 0 && current.Length + s.Length <= SheetLimits.MaxCellChars) current.Append(s);
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "rPh") phonetic--;
                else if (r.LocalName == "si" && current != null) { list.Add(current.ToString()); current = null; }
            }
            if (list.Count > SheetLimits.MaxSharedStrings) throw new FormatException("The workbook has too many distinct strings");
        }
        return list;
    }

    private static List<Style> ReadStyles(ZipArchive zip)
    {
        var styles = new List<Style>();
        if (Entry(zip, "xl/styles.xml") is not { } e) return styles;
        var customDates = new HashSet<int>();
        using var r = XmlFor(e);
        var inCellXfs = false;
        int numFmt = 0, indent = 0;
        var inXf = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "numFmt" when int.TryParse(r.GetAttribute("numFmtId"), out var id) && IsDateFormat(r.GetAttribute("formatCode") ?? ""):
                        customDates.Add(id);
                        break;
                    case "cellXfs":
                        inCellXfs = !r.IsEmptyElement;
                        break;
                    case "xf" when inCellXfs:
                        numFmt = int.TryParse(r.GetAttribute("numFmtId"), out var n) ? n : 0;
                        indent = 0;
                        if (r.IsEmptyElement) styles.Add(new Style(0, IsBuiltInDate(numFmt) || customDates.Contains(numFmt)));
                        else inXf = true;
                        break;
                    case "alignment" when inXf:
                        indent = int.TryParse(r.GetAttribute("indent"), out var ind) ? Math.Clamp(ind, 0, 250) : 0;
                        break;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "xf" && inXf) { styles.Add(new Style(indent, IsBuiltInDate(numFmt) || customDates.Contains(numFmt))); inXf = false; }
                else if (r.LocalName == "cellXfs") inCellXfs = false;
            }
            if (styles.Count > 100_000) break;
        }
        return styles;
    }

    private static bool IsBuiltInDate(int id) => id is >= 14 and <= 22 or >= 45 and <= 47;

    private static bool IsDateFormat(string code)
    {
        // Drop quoted literals and [colour]/[$-locale] sections, then look for date / time tokens.
        var sb = new StringBuilder();
        var quoted = false;
        var bracket = false;
        foreach (var c in code)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[') { bracket = true; continue; }
            if (c == ']') { bracket = false; continue; }
            if (!bracket) sb.Append(char.ToLowerInvariant(c));
        }
        var s = sb.ToString();
        return s.Contains('y') || s.Contains('d') || s.Contains("h") && s.Contains("mm") || s.Contains("m") && !s.Contains('0') && !s.Contains('#');
    }

    private static SheetTable ReadWorksheet(ZipArchiveEntry entry, string name, List<string> shared, List<Style> styles, List<string> warnings)
    {
        var table = new SheetTable { Name = name };
        using var r = XmlFor(entry);
        List<string>? row = null;
        var rowIndex = -1;
        var cut = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
            {
                rowIndex = int.TryParse(r.GetAttribute("r"), out var ri) && ri > 0 ? ri - 1 : rowIndex + 1;
                if (rowIndex > SheetLimits.MaxRows + 50) throw RowLimit();
                while (table.Rows.Count < rowIndex) table.Rows.Add([]);
                row = [];
                if (r.IsEmptyElement) { table.Rows.Add(row); row = null; }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && row != null)
            {
                while (table.Rows.Count < rowIndex) table.Rows.Add([]);
                table.Rows.Add(row);
                row = null;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && row != null)
            {
                var col = r.GetAttribute("r") is { } cref ? ColumnIndex(cref) : row.Count;
                if (col < 0) col = row.Count;
                if (col >= SheetLimits.MaxColumns) throw new FormatException($"Sheet '{name}' has more than {SheetLimits.MaxColumns} columns");
                var type = r.GetAttribute("t") ?? "n";
                var style = int.TryParse(r.GetAttribute("s"), out var si) && si >= 0 && si < styles.Count ? styles[si] : null;
                string? value = null, inline = null;
                var formula = false;
                if (!r.IsEmptyElement)
                {
                    var depth = r.Depth;
                    var positioned = false;
                    while ((positioned || r.Read()) && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
                    {
                        positioned = false;
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "f") { formula = true; if (!r.IsEmptyElement) { r.Skip(); positioned = true; } }
                        else if (r.LocalName == "v" && !r.IsEmptyElement) { value = r.ReadElementContentAsString(); positioned = true; }
                        else if (r.LocalName == "t" && type == "inlineStr" && !r.IsEmptyElement) { inline = (inline ?? "") + r.ReadElementContentAsString(); positioned = true; }
                        else if (r.LocalName == "rPh" && !r.IsEmptyElement) { r.Skip(); positioned = true; }
                    }
                }
                if (formula) table.FormulaCells++;
                var text = type switch
                {
                    "s" => int.TryParse(value, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                    "inlineStr" => inline ?? "",
                    "b" => value == "1" ? "TRUE" : "FALSE",
                    "str" => value ?? "",
                    "e" => value ?? "",
                    _ => NumberText(value, style?.IsDate == true),
                };
                if (text.Length > SheetLimits.MaxCellChars) { text = text[..SheetLimits.MaxCellChars]; cut++; }
                while (row.Count < col) row.Add("");
                if (row.Count == col) row.Add(text); else row[col] = text;
                if (style is { Indent: > 0 }) table.Indents[(rowIndex, col)] = style.Indent;
            }
        }
        // Trailing empty rows carry nothing.
        while (table.Rows.Count > 0 && table.Rows[^1].All(string.IsNullOrWhiteSpace)) table.Rows.RemoveAt(table.Rows.Count - 1);
        if (table.Rows.Count > SheetLimits.MaxRows + 1) throw RowLimit();
        if (cut > 0) warnings.Add($"Sheet '{name}': {cut} cell(s) were longer than {SheetLimits.MaxCellChars:N0} characters and were cut");
        return table;
    }

    private static string NumberText(string? raw, bool date)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return raw;
        if (date && d is > 0 and < 2958466)
        {
            var dt = DateTime.FromOADate(d);
            return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        return d.ToString(CultureInfo.InvariantCulture);
    }

    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        var letters = 0;
        foreach (var c in cellRef)
        {
            if (!char.IsAsciiLetter(c)) break;
            if (++letters > 3) return -1;
            n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return n - 1;
    }
}
