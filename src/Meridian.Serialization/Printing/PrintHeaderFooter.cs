namespace Meridian.Serialization.Printing;

/// <summary>What the header and footer fields of one printed diagram say.</summary>
public sealed record PrintContext(
    string Project, string DiagramName, string DiagramType, string PackagePath, string Version, string Date)
{
    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The six slots of a printed page's header and footer.</summary>
public sealed record HeaderFooterText(string HeaderLeft, string HeaderCenter, string HeaderRight, string FooterLeft, string FooterCenter, string FooterRight)
{
    public bool HasHeader => HeaderLeft.Length + HeaderCenter.Length + HeaderRight.Length > 0;
    public bool HasFooter => FooterLeft.Length + FooterCenter.Length + FooterRight.Length > 0;
}

/// <summary>
/// Header and footer layout, as in the report PDFs: the classification marking is centred at the top and the bottom of
/// every page. Header: project · package path | classification | diagram name (type). Footer: date · version |
/// classification · custom text | Page n of N.
/// </summary>
public static class PrintHeaderFooter
{
    private const string Dot = "  ·  ";

    public static bool HasHeader(PageSetup s) =>
        s.ShowProject || s.ShowPackagePath || s.ShowDiagramName || s.ShowDiagramType || Marking(s).Length > 0;

    public static bool HasFooter(PageSetup s) =>
        s.ShowDate || s.ShowVersion || s.ShowPageNumbers || Marking(s).Length > 0 || s.CustomText.Trim().Length > 0;

    /// <summary>The classification marking, when shown.</summary>
    public static string Marking(PageSetup s) => s.ShowClassification ? s.Classification.Trim() : "";

    public static string PageText(int page, int total) => $"Page {page} of {total}";

    /// <summary>The texts of page <paramref name="page"/> of <paramref name="total"/> (1-based).</summary>
    public static HeaderFooterText Compose(PageSetup s, PrintContext c, int page, int total)
    {
        var marking = Marking(s);
        var headerLeft = Join(s.ShowProject ? c.Project : "", s.ShowPackagePath ? c.PackagePath : "");
        var headerRight = (s.ShowDiagramName ? c.DiagramName : "") is var name && s.ShowDiagramType && c.DiagramType.Length > 0
            ? name.Length > 0 ? $"{name} ({c.DiagramType})" : c.DiagramType
            : name;
        var footerLeft = Join(s.ShowDate ? c.Date : "", s.ShowVersion ? c.Version : "");
        var footerCenter = Join(marking, s.CustomText.Trim());
        var footerRight = s.ShowPageNumbers ? PageText(page, total) : "";
        return new HeaderFooterText(headerLeft, marking, headerRight, footerLeft, footerCenter, footerRight);
    }

    private static string Join(params string[] parts) => string.Join(Dot, parts.Select(p => p.Trim()).Where(p => p.Length > 0));
}
