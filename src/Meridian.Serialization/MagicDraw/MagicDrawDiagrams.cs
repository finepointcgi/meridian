using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class MagicDrawImporter
{
    /// <summary>A diagram found in the model: the <c>uml:Diagram</c> element and the element it was found under.</summary>
    private sealed record DiagramSpec(XElement X, Element FoundUnder);

    /// <summary>One MagicDraw presentation element (<c>mdElement</c>): a shape or a path.</summary>
    internal sealed record View(string Id, string Class, string? ElementRef, string? Geometry, string? Parent, string? FirstEnd, string? SecondEnd)
    {
        public bool IsPath => FirstEnd != null || SecondEnd != null || Geometry?.Contains(';') == true;
    }

    /// <summary>Maps MagicDraw's diagram type names ("SysML Block Definition Diagram", umlType "Class Diagram" …).</summary>
    internal static DiagramType? MapDiagramType(string? type, string? umlType)
    {
        var s = $"{type} | {umlType}".ToLowerInvariant();
        if (s.Contains("table") || s.Contains("matrix") || s.Contains("relation map") || s.Contains("glossary") || s.Contains("content diagram")
            || s.Contains("dependency map") || s.Contains("timing") || s.Contains("communication")) return null;
        if (s.Contains("block definition")) return DiagramType.BlockDefinition;
        if (s.Contains("internal block") || s.Contains("composite structure")) return DiagramType.InternalBlock;
        if (s.Contains("parametric")) return DiagramType.Parametric;
        if (s.Contains("requirement")) return DiagramType.Requirement;
        if (s.Contains("activity")) return DiagramType.Activity;
        if (s.Contains("state machine") || s.Contains("statechart") || s.Contains("state diagram")) return DiagramType.StateMachine;
        if (s.Contains("sequence")) return DiagramType.Sequence;
        if (s.Contains("use case")) return DiagramType.UseCase;
        if (s.Contains("package") || s.Contains("profile")) return DiagramType.Package;
        if (s.Contains("class") || s.Contains("component") || s.Contains("deployment") || s.Contains("object")) return DiagramType.Class;
        return null;
    }

    /// <summary>Flattens MagicDraw's view tree (mdElement / mdOwnedViews, any wrappers) into a list, parents first.</summary>
    internal static List<View> ParseViews(XElement root)
    {
        var views = new List<View>();
        var n = 0;
        void Walk(XElement x, string? parent)
        {
            foreach (var ch in x.Elements())
            {
                var ln = ch.Name.LocalName;
                if (ln == "mdElement")
                {
                    var id = XmiId(ch) ?? Attr(ch, "id") ?? $"view{++n}";
                    string? R(string name) => Child(ch, name) is { } r ? IdRef(r) : null;
                    var v = new View(id, Attr(ch, "elementClass") ?? "", R("elementID"), Child(ch, "geometry")?.Value, parent, R("linkFirstEndID"), R("linkSecondEndID"));
                    views.Add(v);
                    Walk(ch, id);
                }
                else if (ln is not ("elementID" or "geometry" or "linkFirstEndID" or "linkSecondEndID" or "properties" or "propertyID" or "binaryObject"))
                    Walk(ch, parent);
            }
        }
        Walk(root, null);
        return views;
    }

    internal static Bounds? ParseBounds(string? g)
    {
        if (g == null || g.Contains(';')) return null;
        var p = g.Split(',').Select(Num).ToArray();
        if (p.Length != 4 || p.Any(double.IsNaN)) return null;
        return new Bounds(p[0], p[1], Math.Max(4, p[2]), Math.Max(4, p[3]));
    }

    internal static List<Pt> ParsePath(string? g)
    {
        var pts = new List<Pt>();
        if (g == null) return pts;
        foreach (var part in g.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(',').Select(Num).ToArray();
            if (xy.Length >= 2 && !double.IsNaN(xy[0]) && !double.IsNaN(xy[1])) pts.Add(new Pt(xy[0], xy[1]));
        }
        return pts;
    }

    private sealed partial class Builder
    {
        private readonly HashSet<string> _usedBinaries = new(StringComparer.OrdinalIgnoreCase);

        private void CollectDiagrams(XElement ext, Element owner, string? ownerXmiId)
        {
            foreach (var d in ext.Descendants().Where(d => d.Name.LocalName == "ownedDiagram" || d.Name.LocalName != "DiagramRepresentationObject" && XmiType(d) == "Diagram"))
                if (!d.Ancestors().Any(a => a.Name.LocalName == "ownedDiagram"))
                    _diagrams.Add(new DiagramSpec(new XElement(d), owner));
        }

        private void CollectDiagramsIn(XElement x, Element owner, string? ownerXmiId)
        {
            foreach (var ext in x.Elements().Where(e => e.Name.LocalName == "Extension")) CollectDiagrams(ext, owner, ownerXmiId);
        }

        private void BuildDiagrams()
        {
            foreach (var spec in _diagrams)
            {
                try { BuildDiagram(spec); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _report.Warn($"Skipped diagram '{Attr(spec.X, "name")}': {ex.Message}");
                    _report.Skip("Diagrams that failed to import");
                }
            }
        }

        /// <summary>The symbol data: inline (.mdxml) or in the zip entry named by binaryObject/@streamContentID.</summary>
        private XElement? DiagramContent(XElement dx, string name)
        {
            if (dx.Descendants().Any(d => d.Name.LocalName == "mdElement")) return dx;
            var bin = dx.Descendants().FirstOrDefault(d => d.Name.LocalName == "binaryObject");
            if (bin == null) return null;
            if (Attr(bin, "streamContentID") is { } sid)
            {
                using var s = _src.OpenBinary(sid);
                if (s == null) { _report.Warn($"Diagram '{name}': its content resource {sid} is missing from the project"); return null; }
                _usedBinaries.Add(sid);
                foreach (var e in _src.OtherEntries.Where(e => e.EndsWith(sid, StringComparison.OrdinalIgnoreCase))) _usedBinaries.Add(e);
                return LoadContent(s, name);
            }
            // Embedded content (base64), seen in some exports.
            var b64 = Attr(bin, "content") ?? Attr(bin, "binaryContent") ?? (bin.HasElements ? null : bin.Value);
            if (!string.IsNullOrWhiteSpace(b64))
            {
                try { return LoadContent(new MemoryStream(Convert.FromBase64String(b64.Trim())), name); }
                catch (FormatException) { _report.Warn($"Diagram '{name}': embedded content is not readable"); }
            }
            return null;
        }

        private XElement? LoadContent(Stream s, string name)
        {
            try
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                for (var depth = 0; depth < 3; depth++)
                {
                    if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
                    {
                        using var gz = new SafeZip.LimitedStream(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress), MaxBinaryBytes, name);
                        var o = new MemoryStream(); gz.CopyTo(o); bytes = o.ToArray();
                        continue;
                    }
                    if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
                    {
                        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                        var entry = z.Entries.FirstOrDefault(e => e.Length > 0);
                        if (entry == null) return null;
                        using var es = SafeZip.Open(entry, MaxBinaryBytes);
                        var o = new MemoryStream(); es.CopyTo(o); bytes = o.ToArray();
                        continue;
                    }
                    break;
                }
                using var xr = DepthLimitedXmlReader.Create(new MemoryStream(bytes), ReaderSettings);
                return XDocument.Load(xr).Root;
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
                _report.Warn($"Diagram '{name}': symbol data is not in a readable format ({ex.Message})");
                return null;
            }
        }

        private void BuildDiagram(DiagramSpec spec)
        {
            var dx = spec.X;
            var name = Attr(dx, "name") ?? "Diagram";
            var dro = dx.Descendants().FirstOrDefault(d => d.Name.LocalName == "DiagramRepresentationObject");
            var typeName = (dro != null ? Attr(dro, "type") : null) ?? Attr(dx, "diagramType");
            var dt = MapDiagramType(typeName, dro != null ? Attr(dro, "umlType") : null);
            if (dt == null)
            {
                _report.Skip($"Diagrams of type '{typeName ?? "unknown"}'");
                return;
            }
            var type = dt.Value;

            var owner = (Ref(dx, "ownerOfDiagram") is { } oid ? _map.GetValueOrDefault(oid) : null) ?? spec.FoundUnder;
            if (owner is Relationship || owner is Diagram || !_model.Contains(owner)) owner = _model.Root;
            if (!Metamodel.IsValidDiagramContext(type, owner.Kind) && Ref(dx, "context") is { } ctx && _map.TryGetValue(ctx, out var ce) && Metamodel.IsValidDiagramContext(type, ce.Kind))
                owner = ce;
            if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            {
                var was = type;
                if (Metamodel.IsNamespace(owner.Kind)) type = DiagramType.Class;
                else { owner = _model.DefaultOwnerFor(owner, ElementKind.Package); type = DiagramType.Class; }
                _report.Warn($"Diagram '{name}': a {Metamodel.DisplayName(was)} cannot be owned by {Metamodel.DisplayName(owner.Kind)} '{owner.Name}' in Meridian; imported as a {Metamodel.DisplayName(type)}");
            }

            var id = XmiId(dx);
            var d = new Diagram(type, id != null && !_map.ContainsKey(id) && _model.Find(id) == null ? id : null);
            ModelRepository.SetFieldForLoad(d, FieldKeys.Name, name);
            var doc = dx.Elements().Where(e => e.Name.LocalName == "ownedComment").Select(c => PlainText(Attr(c, "body") ?? Child(c, "body")?.Value)).FirstOrDefault(b => !string.IsNullOrEmpty(b));
            ModelRepository.SetFieldForLoad(d, FieldKeys.Documentation, doc);
            _model.AttachForLoad(owner, d);
            if (id != null) _map.TryAdd(id, d);
            _report.Diagrams++;
            _report.Count("Diagram: " + Metamodel.DisplayName(type));

            var content = DiagramContent(dx, name);
            var views = content == null ? [] : ParseViews(content);
            if (views.Count == 0)
            {
                InferContents(d, owner);
                AutoLayoutDiagram(d);
                _report.DiagramsAutoLaidOut++;
                _report.Warn($"Diagram '{name}': layout could not be read; contents were inferred from the model and auto-laid out");
                return;
            }
            var missingGeometry = PlaceViews(d, owner, views, name);
            if (missingGeometry > 0)
            {
                AutoLayoutDiagram(d);
                _report.DiagramsAutoLaidOut++;
                _report.Warn($"Diagram '{name}': {missingGeometry} shapes had no readable bounds; the diagram was auto-laid out");
            }
            else _report.DiagramsWithLayout++;
        }

        /// <summary>Creates shapes and paths from the views. Returns the number of shapes whose bounds were missing.</summary>
        private int PlaceViews(Diagram d, Element owner, List<View> views, string diagramName)
        {
            var viewById = new Dictionary<string, View>(StringComparer.Ordinal);
            foreach (var v in views) viewById.TryAdd(v.Id, v);
            var nodeByView = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
            var missing = 0;
            var unmapped = 0;

            DiagramNode? NearestNode(string? viewId)
            {
                for (var guard = 0; viewId != null && guard < 64; guard++)
                {
                    if (nodeByView.TryGetValue(viewId, out var n)) return n;
                    viewId = viewById.TryGetValue(viewId, out var v) ? v.Parent : null;
                }
                return null;
            }

            foreach (var v in views.Where(v => !v.IsPath))
            {
                if (v.ElementRef == null || !_map.TryGetValue(v.ElementRef, out var el)) { if (v.ElementRef != null) unmapped++; continue; }
                if (el is Relationship || el is Diagram || el.Kind == ElementKind.Model) continue;
                if (el.Kind == ElementKind.InteractionOperand) continue;   // drawn by its fragment (separators, below)
                // The diagram frame shows the context (block / activity / state machine / interaction) itself.
                if (ReferenceEquals(el, owner) && (v.Class.Contains("Frame", StringComparison.OrdinalIgnoreCase) || d.DiagramType is not (DiagramType.Package or DiagramType.BlockDefinition or DiagramType.Class or DiagramType.Requirement or DiagramType.UseCase)))
                    continue;
                var parent = NearestNode(v.Parent);
                if (d.NodeList.Any(n => ReferenceEquals(n.Element, el) && n.ParentId == parent?.Id)) continue;
                var b = ParseBounds(v.Geometry);
                if (b == null)
                {
                    missing++;
                    var (w, h) = Notation.DefaultSizeOf(el);
                    b = new Bounds(0, 0, w, h);
                }
                var node = new DiagramNode(el, b.Value, d.NodeList.Any(n => n.Id == v.Id) ? null : v.Id) { ParentId = parent?.Id };
                d.NodeList.Add(node);
                nodeByView[v.Id] = node;
            }

            // Swimlane contents: nest actions in the partition whose lane holds them.
            if (d.DiagramType == DiagramType.Activity)
            {
                var lanes = d.NodeList.Where(n => n.Element.Kind == ElementKind.Partition).ToList();
                foreach (var n in d.NodeList.Where(n => n.ParentId == null && n.Element.Kind != ElementKind.Partition))
                    if (lanes.FirstOrDefault(l => l.Bounds.Contains(n.Bounds.Center)) is { } lane) n.ParentId = lane.Id;
            }

            PlaceOperandSeparators(d, views, diagramName);

            var skippedPaths = 0;
            foreach (var v in views.Where(v => v.IsPath))
            {
                if (v.ElementRef == null || !_map.TryGetValue(v.ElementRef, out var el) || el is not Relationship rel) { if (v.ElementRef != null) unmapped++; continue; }
                if (d.EdgeList.Any(e => ReferenceEquals(e.Relationship, rel))) continue;
                var a = NearestNode(v.FirstEnd);
                var b = NearestNode(v.SecondEnd);
                var pts = ParsePath(v.Geometry);
                bool Fits(DiagramNode? n, Element e) => n != null && ReferenceEquals(n.Element, e);
                if (!(Fits(a, rel.Source) && Fits(b, rel.Target)) && Fits(a, rel.Target) && Fits(b, rel.Source))
                {
                    (a, b) = (b, a);
                    pts.Reverse();
                }
                if (!Fits(a, rel.Source)) a = ModelRepository.EndNode(d, rel.Source, rel.Get(FieldKeys.SourcePart));
                if (!Fits(b, rel.Target)) b = ModelRepository.EndNode(d, rel.Target, rel.Get(FieldKeys.TargetPart));
                if (a == null || b == null) { skippedPaths++; continue; }
                var edge = new DiagramEdge(rel, a, b, d.EdgeList.Any(e => e.Id == v.Id) ? null : v.Id);
                // Meridian keeps bends only (ends are clipped to the shapes); a message keeps its first point (its time).
                edge.Waypoints = rel.Kind == ElementKind.Message ? pts.Take(1).ToList() : pts.Count > 2 ? pts.Skip(1).Take(pts.Count - 2).ToList() : [];
                d.EdgeList.Add(edge);
            }
            if (skippedPaths > 0) _report.Skip("Diagram paths whose ends are not shown", skippedPaths);
            if (unmapped > 0) _report.Skip("Diagram symbols with no imported element", unmapped);
            return missing;
        }

        /// <summary>
        /// Operand separators of the combined fragments on <paramref name="d"/>. MagicDraw draws each operand as a shape of its own
        /// (an <c>InteractionOperand</c> view nested in the fragment's view, with its bounds); the top of each later operand's
        /// shape is where its dashed separator is. Stored as the fragment shape's <c>operand:&lt;id&gt;</c> styles, as an offset from
        /// the fragment's top. Operands with no readable shape share the rest of the fragment evenly (reported as guessed).
        /// </summary>
        private void PlaceOperandSeparators(Diagram d, List<View> views, string diagramName)
        {
            var tops = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var v in views.Where(v => !v.IsPath && v.ElementRef != null))
                if (_map.TryGetValue(v.ElementRef!, out var el) && el.Kind == ElementKind.InteractionOperand && ParseBounds(v.Geometry) is { } b)
                    tops.TryAdd(el.Id, b.Y);
            foreach (var n in d.NodeList.Where(n => n.Element.Kind == ElementKind.CombinedFragment))
            {
                var ops = InteractionOperands.Of(n.Element);
                if (ops.Count < 2) continue;
                var guessed = 0;
                var last = 0.0;
                foreach (var op in ops.Skip(1))
                {
                    var offset = tops.TryGetValue(op.Id, out var top) ? top - n.Bounds.Y : double.NaN;
                    // A separator must sit inside the fragment, below the one before it.
                    if (double.IsNaN(offset) || offset <= last || offset >= n.Bounds.Height) { guessed++; continue; }
                    ModelRepository.SetNodeStyleForLoad(n, InteractionOperands.StyleKey(op), InteractionOperands.Format(offset));
                    last = offset;
                }
                if (guessed > 0)
                    _report.Approximate(MagicDrawImportReport.OperandGeometryGuessed,
                        $"'{diagramName}': {Metamodel.DisplayName(n.Element.Kind)} {n.Element.Get(FieldKeys.Operator)} — {guessed} of {ops.Count - 1} separators spread evenly");
            }
        }

        /// <summary>Fallback when no symbols can be read: shows what the diagram's context owns, by diagram type.</summary>
        private void InferContents(Diagram d, Element owner)
        {
            IEnumerable<Element> shown = d.DiagramType switch
            {
                DiagramType.Activity => owner.Descendants().Where(e => Metamodel.IsActivityNode(e.Kind) && !Metamodel.IsPin(e.Kind) || e.Kind == ElementKind.Partition),
                DiagramType.StateMachine => owner.Descendants().Where(e => Metamodel.IsVertex(e.Kind)),
                DiagramType.Sequence => owner.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline),
                DiagramType.InternalBlock or DiagramType.Parametric => owner.OwnedElements.Where(e => e.Kind is ElementKind.Property or ElementKind.Port),
                _ => owner.OwnedElements.Where(e => e is not Relationship && e is not Diagram && e.Kind is not (ElementKind.Comment or ElementKind.Property or ElementKind.Port or ElementKind.Operation or ElementKind.EnumerationLiteral)),
            };
            foreach (var e in shown.Take(400))
            {
                var (w, h) = Notation.DefaultSizeOf(e);
                d.NodeList.Add(new DiagramNode(e, new Bounds(0, 0, w, h)));
            }
            var y = 120.0;
            foreach (var r in d.NodeList.SelectMany(n => _model.RelationshipsOf(n.Element)).Distinct().ToList())
            {
                if (d.EdgeFor(r) != null || d.NodeFor(r.Source) is not { } s || d.NodeFor(r.Target) is not { } t) continue;
                var edge = new DiagramEdge(r, s, t);
                if (r.Kind == ElementKind.Message) { edge.Waypoints = [new Pt(0, y)]; y += 40; }
                d.EdgeList.Add(edge);
            }
        }

        private static void AutoLayoutDiagram(Diagram d)
        {
            if (d.NodeList.Count == 0) return;
            // Nested shapes keep their offset to their (moved) parent.
            var top = d.NodeList.Where(n => n.ParentId == null).ToList();
            var layout = AutoLayout.Layered(d);
            foreach (var n in top)
            {
                if (!layout.TryGetValue(n, out var b)) continue;
                var dx = b.X - n.Bounds.X; var dy = b.Y - n.Bounds.Y;
                n.Bounds = b;
                foreach (var c in d.DescendantsOf(n)) c.Bounds = c.Bounds.Offset(dx, dy);
            }
        }
    }
}
