using Meridian.Core.Model;

namespace Meridian.Serialization.Printing;

/// <summary>One printed page: the part of the diagram (in diagram units, CSS px) it shows.</summary>
public sealed record PrintTile(int Index, int Column, int Row, Bounds Region)
{
    /// <summary>"Row 1 · Column 2", for assembling tiles.</summary>
    public string Label => $"Row {Row + 1} · Column {Column + 1}";
}

/// <summary>
/// Where a diagram goes on paper. All page measures are in PDF points (1/72 in); <see cref="ImageArea"/> is the box on
/// every sheet that shows a tile (inside the margins, below the header and above the footer).
/// </summary>
public sealed partial record PrintLayout(
    double PageWidth, double PageHeight, Bounds ImageArea, double HeaderHeight, double FooterHeight,
    double Scale, int Columns, int Rows, double Overlap, Bounds Content, IReadOnlyList<PrintTile> Tiles)
{
    public int PageCount => Tiles.Count;

    /// <summary>Scale as a percentage (100% = the diagram's on-screen size at 96 dpi).</summary>
    public double ScalePercent => Scale * 100;

    /// <summary>Diagram units per point at this scale.</summary>
    public double UnitsPerPoint => 1 / (PrintTiling.PointsPerPx * Scale);

    /// <summary>The part of the diagram the whole tile grid covers.</summary>
    public Bounds Grid => Tiles.Count == 0 ? Content
        : new(Tiles[0].Region.X, Tiles[0].Region.Y, Tiles[^1].Region.Right - Tiles[0].Region.X, Tiles[^1].Region.Bottom - Tiles[0].Region.Y);
}

/// <summary>Tiling math: scale ↔ page count, overlap and margins.</summary>
public static class PrintTiling
{
    /// <summary>A CSS pixel (the diagram's unit) is 1/96 in; a point is 1/72 in.</summary>
    public const double PointsPerPx = 0.75;

    /// <summary>Height of the header and of the footer band, when they show anything.</summary>
    public const double BandHeight = 14, BandGap = 6;

    /// <summary>Most pages one diagram may print on (a fixed scale on a big diagram can ask for thousands).</summary>
    public const int MaxPages = 400;

    private const double Epsilon = 1e-6;

    /// <summary>Lays <paramref name="content"/> (diagram units) out on pages per <paramref name="setup"/>.</summary>
    public static PrintLayout Compute(PageSetup setup, Bounds content)
    {
        var (wMm, hMm) = setup.PaperMm;
        var pageW = wMm * PageSetup.PointsPerMm;
        var pageH = hMm * PageSetup.PointsPerMm;
        var header = PrintHeaderFooter.HasHeader(setup) ? BandHeight + BandGap : 0;
        var footer = PrintHeaderFooter.HasFooter(setup) ? BandHeight + BandGap : 0;
        var left = setup.MarginLeftMm * PageSetup.PointsPerMm;
        var top = setup.MarginTopMm * PageSetup.PointsPerMm;
        var areaW = Math.Max(36, pageW - left - setup.MarginRightMm * PageSetup.PointsPerMm);
        var areaH = Math.Max(36, pageH - top - setup.MarginBottomMm * PageSetup.PointsPerMm - header - footer);
        var area = new Bounds(left, top + header, areaW, areaH);

        if (content.Width <= 0 || content.Height <= 0) content = content with { Width = Math.Max(1, content.Width), Height = Math.Max(1, content.Height) };
        // The overlap repeats a strip of each neighbour; never more than a third of a page.
        var overlap = Math.Min(setup.OverlapMm * PageSetup.PointsPerMm, Math.Min(areaW, areaH) / 3);
        var cw = content.Width * PointsPerPx;
        var ch = content.Height * PointsPerPx;

        double scale;
        int cols, rows;
        switch (setup.Scale)
        {
            case PrintScaleMode.FitToPages:
                cols = setup.PagesAcross; rows = setup.PagesDown;
                scale = Math.Min(Span(cols, areaW, overlap) / cw, Span(rows, areaH, overlap) / ch);
                break;
            case PrintScaleMode.Fixed:
                scale = setup.ScalePercent / 100;
                cols = PagesFor(cw * scale, areaW, overlap);
                rows = PagesFor(ch * scale, areaH, overlap);
                break;
            default:
                cols = rows = 1;
                scale = Math.Min(areaW / cw, areaH / ch);
                break;
        }
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;

        var unitsPerPt = 1 / (PointsPerPx * scale);
        var tileW = areaW * unitsPerPt;
        var tileH = areaH * unitsPerPt;
        var overlapU = overlap * unitsPerPt;
        var gridW = cols * tileW - (cols - 1) * overlapU;
        var gridH = rows * tileH - (rows - 1) * overlapU;
        // The grid is centred on the content: a fitted diagram sits in the middle of its pages.
        var x0 = content.X + content.Width / 2 - gridW / 2;
        var y0 = content.Y + content.Height / 2 - gridH / 2;
        var tiles = new List<PrintTile>(Math.Min(cols * rows, MaxPages + 1));
        for (var r = 0; r < rows && tiles.Count <= MaxPages; r++)
            for (var c = 0; c < cols && tiles.Count <= MaxPages; c++)
                tiles.Add(new PrintTile(tiles.Count, c, r, new Bounds(x0 + c * (tileW - overlapU), y0 + r * (tileH - overlapU), tileW, tileH)));
        return new PrintLayout(pageW, pageH, area, header, footer, scale, cols, rows, overlap, content, tiles)
        {
            TotalPages = (long)cols * rows,
        };
    }

    /// <summary>Points covered by <paramref name="pages"/> pages of width <paramref name="page"/> that overlap by <paramref name="overlap"/>.</summary>
    public static double Span(int pages, double page, double overlap) => pages * page - (pages - 1) * overlap;

    /// <summary>Pages needed to cover <paramref name="length"/> points with pages of <paramref name="page"/> points overlapping by <paramref name="overlap"/>.</summary>
    public static int PagesFor(double length, double page, double overlap)
    {
        if (length <= page + Epsilon) return 1;
        var step = Math.Max(1, page - overlap);
        var n = (long)Math.Ceiling((length - overlap) / step - Epsilon);
        return (int)Math.Clamp(n, 1, 100_000);
    }

    /// <summary>The scale (fraction) at which <paramref name="content"/> needs no more than the given pages across and down —
    /// the inverse of <see cref="PrintScaleMode.Fixed"/>'s page count.</summary>
    public static double ScaleFor(PageSetup setup, Bounds content, int across, int down) =>
        Compute(Copy(setup, s => { s.Scale = PrintScaleMode.FitToPages; s.PagesAcross = across; s.PagesDown = down; }), content).Scale;

    private static PageSetup Copy(PageSetup s, Action<PageSetup> change)
    {
        var c = s.Clone();
        change(c);
        return c;
    }
}

public sealed partial record PrintLayout
{
    /// <summary>Pages the setup asks for (columns × rows); more than <see cref="PrintTiling.MaxPages"/> are not laid out.</summary>
    public long TotalPages { get; init; }

    /// <summary>The setup asks for more pages than one diagram may print on.</summary>
    public bool TooManyPages => TotalPages > PrintTiling.MaxPages;
}
