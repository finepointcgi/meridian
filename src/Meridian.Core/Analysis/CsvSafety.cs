namespace Meridian.Core.Analysis;

/// <summary>
/// CSV formula-injection guard. Spreadsheet programs run a cell that starts with =, +, -, @ (or a tab / carriage return
/// before one) as a formula, so exported model text is prefixed with an apostrophe; importers strip that prefix again.
/// </summary>
public static class CsvSafety
{
    private static bool Risky(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>The value to write into a CSV cell.</summary>
    public static string Neutralize(string s) => s.Length > 0 && Risky(s[0]) ? "'" + s : s;

    /// <summary>Undoes <see cref="Neutralize"/> for a cell read back from a CSV file.</summary>
    public static string Restore(string s) => s.Length > 1 && s[0] == '\'' && Risky(s[1]) ? s[1..] : s;
}
