using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Serialization.Printing;

public enum PaperSize { A4, A3, A2, A1, A0, Letter, Legal, Tabloid, Custom }

/// <summary>How the diagram is scaled onto paper.</summary>
public enum PrintScaleMode
{
    /// <summary>Shrink (or grow) the diagram to fill one page.</summary>
    FitToPage,
    /// <summary>Spread it over exactly <see cref="PageSetup.PagesAcross"/> × <see cref="PageSetup.PagesDown"/> pages.</summary>
    FitToPages,
    /// <summary>A fixed scale (<see cref="PageSetup.ScalePercent"/>; 100% = one CSS pixel per 1/96 in); the page count follows.</summary>
    Fixed,
}

/// <summary>What part of the diagram is printed.</summary>
public enum PrintRegion { WholeDiagram, CurrentView, Selection }

/// <summary>
/// Page setup for printing a diagram (Cameo / EA "Print" and "Print Preview"): paper, orientation, margins, scaling,
/// tiling overlap and registration marks, header and footer fields, frame, legend and theme. Stored per user (preferences)
/// and optionally per diagram (<see cref="DiagramField"/>), as JSON; unknown or invalid values fall back to the defaults.
/// </summary>
public sealed class PageSetup
{
    /// <summary>Diagram field that holds a page setup saved with the diagram.</summary>
    public const string DiagramField = "printSetup";

    public const int MaxPagesAcross = 20;
    public const double MinScalePercent = 1, MaxScalePercent = 1000;

    public PaperSize Paper { get; set; } = PaperSize.A4;
    public double CustomWidthMm { get; set; } = 210;
    public double CustomHeightMm { get; set; } = 297;
    public bool Landscape { get; set; } = true;

    public double MarginTopMm { get; set; } = 10;
    public double MarginRightMm { get; set; } = 10;
    public double MarginBottomMm { get; set; } = 10;
    public double MarginLeftMm { get; set; } = 10;

    public PrintScaleMode Scale { get; set; } = PrintScaleMode.FitToPage;
    public int PagesAcross { get; set; } = 1;
    public int PagesDown { get; set; } = 1;
    public double ScalePercent { get; set; } = 100;

    /// <summary>How far neighbouring tiles repeat each other's edge, for assembling them (0 = none).</summary>
    public double OverlapMm { get; set; }
    /// <summary>Corner registration marks and overlap guides on every tile.</summary>
    public bool RegistrationMarks { get; set; }

    // ---------------------------------------------------------------- header and footer fields
    public bool ShowProject { get; set; } = true;
    public bool ShowDiagramName { get; set; } = true;
    public bool ShowDiagramType { get; set; } = true;
    public bool ShowPackagePath { get; set; }
    public bool ShowVersion { get; set; }
    public bool ShowDate { get; set; } = true;
    public bool ShowPageNumbers { get; set; } = true;
    public bool ShowClassification { get; set; }
    public string Classification { get; set; } = "";
    public string CustomText { get; set; } = "";

    // ---------------------------------------------------------------- content
    /// <summary>The SysML diagram frame and its header.</summary>
    public bool Frame { get; set; } = true;
    /// <summary>The auto-generated legend, whether or not the diagram shows one on screen.</summary>
    public bool Legend { get; set; }
    /// <summary>Print light even when the canvas is dark (the default).</summary>
    public bool ForceLight { get; set; } = true;
    public PrintRegion Region { get; set; } = PrintRegion.WholeDiagram;

    /// <summary>Width and height of the sheet in millimetres, in the chosen orientation.</summary>
    public (double Width, double Height) PaperMm
    {
        get
        {
            var (w, h) = PaperDimensionsMm(Paper, CustomWidthMm, CustomHeightMm);
            var (shortSide, longSide) = (Math.Min(w, h), Math.Max(w, h));
            return Landscape ? (longSide, shortSide) : (shortSide, longSide);
        }
    }

    /// <summary>Portrait dimensions of a paper size in millimetres.</summary>
    public static (double Width, double Height) PaperDimensionsMm(PaperSize paper, double customWidth = 210, double customHeight = 297) => paper switch
    {
        PaperSize.A4 => (210, 297),
        PaperSize.A3 => (297, 420),
        PaperSize.A2 => (420, 594),
        PaperSize.A1 => (594, 841),
        PaperSize.A0 => (841, 1189),
        PaperSize.Letter => (215.9, 279.4),
        PaperSize.Legal => (215.9, 355.6),
        PaperSize.Tabloid => (279.4, 431.8),
        _ => (customWidth, customHeight),
    };

    public static string PaperLabel(PaperSize p) => p switch
    {
        PaperSize.Letter => "Letter (8.5 × 11 in)",
        PaperSize.Legal => "Legal (8.5 × 14 in)",
        PaperSize.Tabloid => "Tabloid (11 × 17 in)",
        PaperSize.Custom => "Custom…",
        _ => $"{p} ({PaperDimensionsMm(p).Width:0} × {PaperDimensionsMm(p).Height:0} mm)",
    };

    public const double PointsPerMm = 72 / 25.4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a stored setup; anything unreadable gives the defaults, and values are clamped to sane ranges.</summary>
    public static PageSetup FromJson(string? json)
    {
        PageSetup s;
        if (string.IsNullOrWhiteSpace(json)) s = new();
        else
        {
            try { s = JsonSerializer.Deserialize<PageSetup>(json, Json) ?? new(); }
            catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { s = new(); }
        }
        return s.Normalize();
    }

    public PageSetup Clone() => FromJson(ToJson());

    /// <summary>Repairs values a hand-edited or older document may carry.</summary>
    public PageSetup Normalize()
    {
        if (!Enum.IsDefined(Paper)) Paper = PaperSize.A4;
        if (!Enum.IsDefined(Scale)) Scale = PrintScaleMode.FitToPage;
        if (!Enum.IsDefined(Region)) Region = PrintRegion.WholeDiagram;
        CustomWidthMm = Clamp(CustomWidthMm, 50, 5000, 210);
        CustomHeightMm = Clamp(CustomHeightMm, 50, 5000, 297);
        var (w, h) = PaperMm;
        // Margins leave at least 40% of each side for the picture.
        MarginLeftMm = Clamp(MarginLeftMm, 0, w * 0.3, 10);
        MarginRightMm = Clamp(MarginRightMm, 0, w * 0.3, 10);
        MarginTopMm = Clamp(MarginTopMm, 0, h * 0.3, 10);
        MarginBottomMm = Clamp(MarginBottomMm, 0, h * 0.3, 10);
        PagesAcross = Math.Clamp(PagesAcross, 1, MaxPagesAcross);
        PagesDown = Math.Clamp(PagesDown, 1, MaxPagesAcross);
        ScalePercent = Clamp(ScalePercent, MinScalePercent, MaxScalePercent, 100);
        OverlapMm = Clamp(OverlapMm, 0, 50, 0);
        Classification = Trim(Classification, 80);
        CustomText = Trim(CustomText, 200);
        return this;
    }

    private static double Clamp(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, Math.Max(min, max)) : fallback;
    private static string Trim(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();
}
