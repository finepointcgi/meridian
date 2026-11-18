using System.Text;
using Meridian.Core.Model;

namespace Meridian.Serialization.Spreadsheets;

/// <summary>What a column of an org chart sheet holds.</summary>
public enum OrgColumn { Id, Name, FirstName, LastName, Title, Manager, Department, Email, Photo }

/// <summary>An org chart read from a sheet: the columns found, the people and how they report to each other.</summary>
public sealed class OrgSheet
{
    public required string SheetName { get; init; }
    public required int HeaderRow { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }

    /// <summary>Column index of each role found.</summary>
    public required IReadOnlyDictionary<OrgColumn, int> Columns { get; init; }

    public required IReadOnlyList<OrgPerson> People { get; init; }
    public required OrgTree Tree { get; init; }

    /// <summary>Problems reading the sheet, then problems resolving managers.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public string? HeaderOf(OrgColumn c) => Columns.TryGetValue(c, out var i) && i < Headers.Count ? Headers[i] : null;
}

/// <summary>
/// Reads an org chart from a spreadsheet (one row per person): finds the name, title, manager, id, department and
/// email columns by their headers, then resolves who reports to whom (see <see cref="OrgChart.Resolve"/>).
/// </summary>
public static class OrgChartSheet
{
    private static readonly (OrgColumn Column, string[] Names)[] Synonyms =
    [
        (OrgColumn.Manager, ["manager", "reports to", "reportsto", "reporting to", "supervisor", "parent", "manager id", "manager name", "managerid",
            "line manager", "boss", "reports to id", "supervisor id", "parent id", "manager email", "lead", "team lead"]),
        (OrgColumn.Id, ["id", "employee id", "emp id", "employee number", "employee no", "emp no", "person id", "staff id", "user id", "personnel number", "key", "number", "no"]),
        (OrgColumn.Name, ["name", "full name", "employee", "employee name", "person", "display name", "staff name", "member"]),
        (OrgColumn.FirstName, ["first name", "firstname", "given name", "forename"]),
        (OrgColumn.LastName, ["last name", "lastname", "surname", "family name"]),
        (OrgColumn.Title, ["title", "job title", "role", "position", "job", "designation", "job role", "job position"]),
        (OrgColumn.Department, ["department", "dept", "team", "division", "group", "unit", "org unit", "organisation", "organization", "function", "business unit"]),
        (OrgColumn.Email, ["email", "e mail", "mail", "email address", "e mail address", "work email"]),
        (OrgColumn.Photo, ["photo", "photo url", "picture", "image", "image url", "avatar", "photo link"]),
    ];

    /// <summary>Words that, inside a longer header, point to a role ("Manager (name)", "Department / team").</summary>
    private static readonly (OrgColumn Column, string Word)[] Hints =
    [
        (OrgColumn.Manager, "reports"), (OrgColumn.Manager, "manager"), (OrgColumn.Manager, "supervisor"), (OrgColumn.Manager, "parent"),
        (OrgColumn.Photo, "photo"), (OrgColumn.Email, "email"), (OrgColumn.Title, "title"), (OrgColumn.Title, "role"), (OrgColumn.Title, "position"),
        (OrgColumn.Department, "department"), (OrgColumn.Department, "dept"), (OrgColumn.Department, "team"), (OrgColumn.Id, "id"), (OrgColumn.Name, "name"),
    ];

    /// <summary>
    /// Finds each role's column by header name, ignoring case and punctuation: exact synonyms first ("Reports to",
    /// "Supervisor", "Job title"…), then headers containing a telling word ("Manager (name)").
    /// </summary>
    public static Dictionary<OrgColumn, int> DetectColumns(IReadOnlyList<string> headers)
    {
        var norm = headers.Select(Normalise).ToList();
        var found = new Dictionary<OrgColumn, int>();
        var used = new HashSet<int>();
        foreach (var (col, names) in Synonyms)
            for (var i = 0; i < norm.Count; i++)
                if (!used.Contains(i) && !found.ContainsKey(col) && names.Contains(norm[i])) { found[col] = i; used.Add(i); }
        foreach (var (col, word) in Hints)
        {
            if (found.ContainsKey(col)) continue;
            for (var i = 0; i < norm.Count; i++)
                if (!used.Contains(i) && norm[i].Split(' ').Contains(word)) { found[col] = i; used.Add(i); break; }
        }
        return found;
    }

    private static string Normalise(string h)
    {
        var sb = new StringBuilder(h.Length);
        foreach (var c in h.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Reads a spreadsheet file (.xlsx, .csv, .tsv…) and the org chart on its first (or chosen) sheet.</summary>
    public static OrgSheet Read(string fileName, byte[] bytes, int sheet = 0)
    {
        var book = SheetReader.Read(fileName, new MemoryStream(bytes));
        if (book.Sheets.Count == 0) throw new FormatException("The file has no sheets.");
        var s = book.Sheets[Math.Clamp(sheet, 0, book.Sheets.Count - 1)];
        return Read(s, warnings: book.Warnings);
    }

    /// <summary>Reads the org chart on <paramref name="table"/>; the header row is detected unless given.</summary>
    public static OrgSheet Read(SheetTable table, int? headerRow = null, IEnumerable<string>? warnings = null)
    {
        var problems = new List<string>(warnings ?? []);
        var rows = table.Rows;
        var h = headerRow ?? SheetReader.DetectHeaderRow(rows);
        var headers = h < rows.Count ? rows[h].Select(c => c.Trim()).ToList() : [];
        var cols = DetectColumns(headers);
        if (!cols.ContainsKey(OrgColumn.Name) && !cols.ContainsKey(OrgColumn.FirstName) && !cols.ContainsKey(OrgColumn.LastName))
        {
            // No name column: the first column nobody claimed holds the names.
            var first = Enumerable.Range(0, headers.Count).FirstOrDefault(i => !cols.ContainsValue(i), -1);
            if (first >= 0)
            {
                cols[OrgColumn.Name] = first;
                problems.Add($"No “Name” column; using “{headers[first]}” for names.");
            }
        }
        if (!cols.ContainsKey(OrgColumn.Manager)) problems.Add("No “Manager” or “Reports to” column; everyone is shown at the top.");

        string? Cell(List<string> row, OrgColumn c) => cols.TryGetValue(c, out var i) && i < row.Count && row[i].Trim() is { Length: > 0 } v ? v : null;
        var people = new List<OrgPerson>();
        for (var r = h + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var name = Cell(row, OrgColumn.Name)
                ?? string.Join(' ', new[] { Cell(row, OrgColumn.FirstName), Cell(row, OrgColumn.LastName) }.Where(x => x != null));
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"Row {r + 1}: no name; skipped.");
                continue;
            }
            people.Add(new OrgPerson(name, Cell(row, OrgColumn.Title), Cell(row, OrgColumn.Department), Cell(row, OrgColumn.Manager),
                Cell(row, OrgColumn.Id), Cell(row, OrgColumn.Email), r + 1));
        }
        var tree = OrgChart.Resolve(people);
        return new OrgSheet
        {
            SheetName = table.Name, HeaderRow = h, Headers = headers, Columns = cols, People = people, Tree = tree,
            Warnings = [.. problems, .. tree.Warnings],
        };
    }
}
