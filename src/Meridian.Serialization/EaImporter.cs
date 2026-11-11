using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Microsoft.Data.Sqlite;

namespace Meridian.Serialization;

/// <summary>
/// Imports Sparx Enterprise Architect repositories stored as SQLite (.qea / .qeax, EA 16+): packages, elements,
/// attributes, operations, connectors (incl. SysML stereotypes), tagged values and diagram layouts.
/// </summary>
public static class EaImporter
{
    public static (ModelRepository Model, ImportReport Report) Import(string path)
    {
        var warnings = new List<string>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();

        List<Dictionary<string, object?>> Query(string sql)
        {
            var rows = new List<Dictionary<string, object?>>();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                    rows.Add(row);
                }
            }
            catch (SqliteException ex) { warnings.Add($"Skipped query ({ex.Message})"); }
            return rows;
        }
        static string S(Dictionary<string, object?> row, string col) => row.TryGetValue(col, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        static int I(Dictionary<string, object?> row, string col) => int.TryParse(S(row, col), out var v) ? v : 0;
        // Columns that older or trimmed repositories may lack are read only when present.
        HashSet<string> Columns(string table) => Query($"pragma table_info('{table}')").Select(r => S(r, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objectCols = Columns("t_object");
        var connectorCols = Columns("t_connector");
        string Opt(HashSet<string> cols, string col) => cols.Contains(col) ? ", " + col : "";

        var packages = Query("select Package_ID, Name, Parent_ID, Notes from t_package order by Package_ID");
        if (packages.Count == 0) throw new FormatException("No t_package table — this does not look like an Enterprise Architect repository.");
        var root = packages.FirstOrDefault(p => I(p, "Parent_ID") == 0) ?? packages[0];
        var model = new ModelRepository(S(root, "Name") is { Length: > 0 } rn ? rn : "EA Model");

        var pkgById = new Dictionary<int, Element> { [I(root, "Package_ID")] = model.Root };
        // Packages may appear before their parents; resolve iteratively.
        var remaining = packages.Where(p => !ReferenceEquals(p, root)).ToList();
        for (var pass = 0; pass < 50 && remaining.Count > 0; pass++)
            foreach (var p in remaining.ToList())
            {
                var parent = pkgById.GetValueOrDefault(I(p, "Parent_ID")) ?? (pkgById.ContainsKey(I(p, "Parent_ID")) ? null : (pass > 10 ? model.Root : null));
                if (parent == null) continue;
                var e = new Element(ElementKind.Package);
                ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(p, "Name"));
                ModelRepository.SetFieldForLoad(e, FieldKeys.Documentation, Clean(S(p, "Notes")));
                model.AttachForLoad(parent, e);
                pkgById[I(p, "Package_ID")] = e;
                remaining.Remove(p);
            }

        var objects = Query("select Object_ID, Object_Type, Name, Alias, Note, Package_ID, Stereotype, ParentID, Classifier, Abstract, NType" + Opt(objectCols, "ea_guid") + Opt(objectCols, "RunState") + " from t_object order by Object_ID");
        var typeOfObject = objects.ToDictionary(o => I(o, "Object_ID"), o => S(o, "Object_Type"));
        // Fork / join bars (EA "Synchronization") of state machines are pseudostates; in activities they are fork nodes.
        var outgoing = Query("select Start_Object_ID from t_connector").GroupBy(r => I(r, "Start_Object_ID")).ToDictionary(g => g.Key, g => g.Count());
        var props = Query("select Object_ID, Property, Value, Notes from t_objectproperties")
            .GroupBy(r => I(r, "Object_ID")).ToDictionary(g => g.Key, g => g.ToList());
        var objById = new Dictionary<int, Element>();
        var objByGuid = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        var typeRefs = new List<(Element e, int classifier)>();
        var instances = new List<(Element e, int classifier, string runState)>();   // EA objects: classifier and run state (slot values)
        var helperContainers = new Dictionary<(Element pkg, ElementKind kind), Element>();

        Element ContainerFor(Element pkg, ElementKind childKind)
        {
            // EA lets actions/states live directly in packages; UML needs an owning activity / state machine.
            var behaviorKind = Metamodel.IsActivityNode(childKind) || childKind == ElementKind.Partition ? ElementKind.Activity
                : childKind is ElementKind.Lifeline or ElementKind.CombinedFragment ? ElementKind.Interaction : ElementKind.StateMachine;
            if (!helperContainers.TryGetValue((pkg, behaviorKind), out var c))
            {
                c = new Element(behaviorKind);
                ModelRepository.SetFieldForLoad(c, FieldKeys.Name, $"{pkg.Name} {behaviorKind switch { ElementKind.Activity => "activity", ElementKind.Interaction => "interaction", _ => "states" }}");
                model.AttachForLoad(pkg, c);
                helperContainers[(pkg, behaviorKind)] = c;
            }
            return c;
        }

        // Pass 1: create every element; pass 2: nest by ParentID.
        var pendingParents = new List<(Element e, int parentId, Element pkg)>();
        foreach (var o in objects)
        {
            var inStateMachine = typeOfObject.GetValueOrDefault(I(o, "ParentID")) is "StateMachine" or "State";
            var kind = MapObject(S(o, "Object_Type"), S(o, "Stereotype"), I(o, "NType"), inStateMachine, outgoing.GetValueOrDefault(I(o, "Object_ID")), out var subtype, out var approx);
            if (kind is null) { if (S(o, "Object_Type") is not ("Package" or "Boundary" or "Text" or "NoteLink")) warnings.Add($"Skipped {S(o, "Object_Type")} '{S(o, "Name")}'"); continue; }
            if (approx != null) warnings.Add($"Approximated: {S(o, "Object_Type")} '{S(o, "Name")}' ({approx})");
            var e = new Element(kind.Value);
            ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(o, "Name"));
            if (kind == ElementKind.CombinedFragment) ModelRepository.SetFieldForLoad(e, FieldKeys.Operator, subtype ?? "alt");
            else ModelRepository.SetFieldForLoad(e, FieldKeys.Subtype, subtype);
            var note = Clean(S(o, "Note"));
            if (kind == ElementKind.Comment) ModelRepository.SetFieldForLoad(e, FieldKeys.Body, note);
            else if (kind == ElementKind.Requirement) ModelRepository.SetFieldForLoad(e, FieldKeys.Text, note);
            else ModelRepository.SetFieldForLoad(e, FieldKeys.Documentation, note);
            if (S(o, "Abstract") == "1") ModelRepository.SetFieldForLoad(e, FieldKeys.IsAbstract, "true");
            var stereo = S(o, "Stereotype");
            if (stereo.Length > 0 && Metamodel.Keyword(kind.Value) != stereo && !IsSysmlStereo(stereo) && !IsKindStereo(stereo)) ModelRepository.SetFieldForLoad(e, FieldKeys.Stereotypes, stereo);
            if (kind == ElementKind.Requirement)
                ModelRepository.SetFieldForLoad(e, FieldKeys.RequirementId, S(o, "Alias") is { Length: > 0 } alias ? alias : null);
            foreach (var tv in props.GetValueOrDefault(I(o, "Object_ID")) ?? [])
            {
                var name = S(tv, "Property");
                var value = S(tv, "Value") == "<memo>" ? Clean(S(tv, "Notes")) : S(tv, "Value");
                if (kind == ElementKind.Requirement && name.Equals("id", StringComparison.OrdinalIgnoreCase)) ModelRepository.SetFieldForLoad(e, FieldKeys.RequirementId, value);
                else if (kind == ElementKind.Requirement && name.Equals("text", StringComparison.OrdinalIgnoreCase)) ModelRepository.SetFieldForLoad(e, FieldKeys.Text, value);
                else if (name.Length > 0) ModelRepository.SetFieldForLoad(e, FieldKeys.TagPrefix + name, value);
            }
            if (Metamodel.IsTypedFeature(kind.Value) && I(o, "Classifier") > 0) typeRefs.Add((e, I(o, "Classifier")));
            if (kind == ElementKind.InstanceSpecification) instances.Add((e, I(o, "Classifier"), S(o, "RunState")));
            objById[I(o, "Object_ID")] = e;
            if (S(o, "ea_guid") is { Length: > 0 } guid) objByGuid[guid] = e;
            pendingParents.Add((e, I(o, "ParentID"), pkgById.GetValueOrDefault(I(o, "Package_ID")) ?? model.Root));
        }
        // Attach parents before children (parents may themselves be nested).
        var attached = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        void Attach((Element e, int parentId, Element pkg) item, int depth = 0)
        {
            if (attached.Contains(item.e)) return;
            var owner = item.pkg;
            if (item.parentId > 0 && objById.TryGetValue(item.parentId, out var parent))
            {
                var parentItem = pendingParents.First(p => ReferenceEquals(p.e, parent));
                if (depth < 20) Attach(parentItem, depth + 1);
                if (Metamodel.CanOwn(parent.Kind, item.e.Kind)) owner = parent;
            }
            if (!Metamodel.CanOwn(owner.Kind, item.e.Kind))
                owner = Metamodel.IsActivityNode(item.e.Kind) || Metamodel.IsVertex(item.e.Kind) || item.e.Kind is ElementKind.Partition or ElementKind.Lifeline or ElementKind.CombinedFragment
                    ? ContainerFor(item.pkg, item.e.Kind) : model.DefaultOwnerFor(owner, item.e.Kind);
            if (!Metamodel.CanOwn(owner.Kind, item.e.Kind)) { warnings.Add($"Could not place {item.e.Kind} '{item.e.Name}'"); attached.Add(item.e); return; }
            model.AttachForLoad(owner, item.e);
            attached.Add(item.e);
        }
        foreach (var item in pendingParents) Attach(item);

        // Interaction operands: EA keeps a combined fragment's operands as its "Partitions", one t_xref row per fragment
        // (Name = 'Partitions', Client = the fragment's ea_guid) whose Description lists @PAR;Name=<guard>;Size=<height>;…;@ENDPAR;
        // segments, top operand first. Sizes are the operands' heights in pixels (turned into separators on each diagram below).
        var operandSizes = new Dictionary<Element, List<double>>(ReferenceEqualityComparer.Instance);
        if (objByGuid.Count > 0 && Columns("t_xref").Count > 0)
            foreach (var x in Query("select Client, Name, Description from t_xref where Name = 'Partitions'"))
            {
                if (!objByGuid.TryGetValue(S(x, "Client"), out var owner)) continue;
                var parts = ParsePartitions(S(x, "Description"));
                if (owner.Kind == ElementKind.CombinedFragment)
                {
                    var sizes = new List<double>();
                    foreach (var (guard, size) in parts)
                    {
                        var op = new Element(ElementKind.InteractionOperand);
                        ModelRepository.SetFieldForLoad(op, FieldKeys.Guard, guard);
                        model.AttachForLoad(owner, op);
                        sizes.Add(size);
                    }
                    operandSizes[owner] = sizes;
                }
                else if (owner.Kind == ElementKind.State && parts.Count > 1)
                    warnings.Add($"State '{owner.Name}' has {parts.Count} regions; they were flattened into one.");
            }

        var columnsById = new Dictionary<int, Element>();
        // Attributes and operations.
        foreach (var a in Query("select ID, Object_ID, Name, Type, Classifier, LowerBound, UpperBound, \"Default\" as Def, Notes, Scope from t_attribute order by Pos"))
        {
            if (!objById.TryGetValue(I(a, "Object_ID"), out var owner)) continue;
            var kind = owner.Kind == ElementKind.Enumeration ? ElementKind.EnumerationLiteral : owner.Kind == ElementKind.Table ? ElementKind.Column : ElementKind.Property;
            if (!Metamodel.CanOwn(owner.Kind, kind)) continue;
            var e = new Element(kind);
            ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(a, "Name"));
            ModelRepository.SetFieldForLoad(e, FieldKeys.Documentation, Clean(S(a, "Notes")));
            if (kind == ElementKind.Property)
            {
                var lo = S(a, "LowerBound"); var hi = S(a, "UpperBound");
                ModelRepository.SetFieldForLoad(e, FieldKeys.Multiplicity, XmiExchange.FormatMultiplicity(lo.Length > 0 ? lo : "1", hi.Length > 0 ? hi : "1"));
                ModelRepository.SetFieldForLoad(e, FieldKeys.DefaultValue, S(a, "Def"));
                ModelRepository.SetFieldForLoad(e, FieldKeys.Visibility, S(a, "Scope") is { Length: > 0 } sc && sc != "Public" ? sc.ToLowerInvariant() : null);
                if (int.TryParse(S(a, "Classifier"), out var cid) && cid > 0) typeRefs.Add((e, cid));
                else ModelRepository.SetFieldForLoad(e, FieldKeys.Type, PrimitiveName(S(a, "Type")));
            }
            if (kind == ElementKind.Column) { ModelRepository.SetFieldForLoad(e, FieldKeys.SqlType, S(a, "Type")); columnsById[I(a, "ID")] = e; }
            model.AttachForLoad(owner, e);
        }
        var fkColumns = ReadDataModel(model, Query, columnsById, objById);
        var parameters = Query("select OperationID, Name, Type, Kind, Pos from t_operationparams order by Pos").GroupBy(p => I(p, "OperationID")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var op in Query("select OperationID, Object_ID, Name, Type, Notes from t_operation order by Pos"))
        {
            if (!objById.TryGetValue(I(op, "Object_ID"), out var owner) || !Metamodel.CanOwn(owner.Kind, ElementKind.Operation)) continue;
            var e = new Element(ElementKind.Operation);
            ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(op, "Name"));
            ModelRepository.SetFieldForLoad(e, FieldKeys.Documentation, Clean(S(op, "Notes")));
            var ps = (parameters.GetValueOrDefault(I(op, "OperationID")) ?? []).Where(p => !S(p, "Kind").Equals("return", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{S(p, "Name")} : {PrimitiveName(S(p, "Type"))}");
            var ret = S(op, "Type");
            ModelRepository.SetFieldForLoad(e, FieldKeys.Signature, $"({string.Join(", ", ps)})" + (ret is { Length: > 0 } && ret != "void" ? $" : {PrimitiveName(ret)}" : ""));
            model.AttachForLoad(owner, e);
        }
        foreach (var (e, cid) in typeRefs)
            if (objById.TryGetValue(cid, out var t)) ModelRepository.SetFieldForLoad(e, FieldKeys.Type, t.Id);

        // Connectors.
        var relById = new Dictionary<int, Relationship>();
        var rels = 0;
        var messageOrder = new Dictionary<Relationship, int>(ReferenceEqualityComparer.Instance);
        foreach (var c in Query("select Connector_ID, Connector_Type, SubType, Stereotype, Name, Start_Object_ID, End_Object_ID, DestCard, PDATA2"
                                + Opt(connectorCols, "SourceCard") + Opt(connectorCols, "SeqNo") + " from t_connector"))
        {
            if (!objById.TryGetValue(I(c, "Start_Object_ID"), out var s) || !objById.TryGetValue(I(c, "End_Object_ID"), out var t)) continue;
            var kind = MapConnector(S(c, "Connector_Type"), S(c, "SubType"), S(c, "Stereotype"));
            if (kind is null) continue;
            var k = kind.Value;
            // EA draws composition from part to whole; Meridian's composition goes whole → part.
            var swapped = k is ElementKind.Composition or ElementKind.Aggregation;
            if (swapped) (s, t) = (t, s);
            // Between two objects an association is a link (typed below by the one association between their classifiers).
            if (s.Kind == ElementKind.InstanceSpecification && t.Kind == ElementKind.InstanceSpecification && k is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation)
                k = ElementKind.Link;
            if (Metamodel.CheckEnds(k, s.Kind, t.Kind) != null)
            {
                if (Metamodel.CheckEnds(k, t.Kind, s.Kind) == null) (s, t) = (t, s);
                else if (Metamodel.CheckEnds(ElementKind.Trace, s.Kind, t.Kind) == null) { warnings.Add($"{k} '{S(c, "Name")}' ({s.Kind}→{t.Kind}) imported as Trace"); k = ElementKind.Trace; }
                else continue;
            }
            var r = new Relationship(k, s, t);
            ModelRepository.SetFieldForLoad(r, FieldKeys.Name, S(c, "Name"));
            if (k == ElementKind.ForeignKey)
            {
                // EA keeps the key columns on «FK» / «PK» operations of the two tables.
                if (fkColumns.TryGetValue((I(c, "Start_Object_ID"), S(c, "Name")), out var fkc) || fkColumns.TryGetValue((I(c, "End_Object_ID"), S(c, "Name")), out fkc))
                    ModelRepository.SetFieldForLoad(r, FieldKeys.FkColumns, string.Join(", ", fkc));
                var pk = t.OwnedElements.Where(x => x.Kind == ElementKind.Column && x.Get(FieldKeys.PrimaryKey) == "true").Select(x => x.Name).ToList();
                if (pk.Count > 0) ModelRepository.SetFieldForLoad(r, FieldKeys.RefColumns, string.Join(", ", pk));
            }
            if (k is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation)
            {
                // SourceCard / DestCard are the multiplicities at EA's start / end object. A composition is drawn part → whole in EA and
                // whole → part in Meridian, so its ends swap. Repositories without SourceCard keep DestCard as the target's.
                var (atTarget, atSource) = swapped && connectorCols.Contains("SourceCard") ? (S(c, "SourceCard"), S(c, "DestCard")) : (S(c, "DestCard"), S(c, "SourceCard"));
                ModelRepository.SetFieldForLoad(r, FieldKeys.Multiplicity, atTarget);
                ModelRepository.SetFieldForLoad(r, FieldKeys.SourceMultiplicity, atSource);
            }
            if (k == ElementKind.Message)
            {
                ModelRepository.SetFieldForLoad(r, FieldKeys.MessageSort, "synchCall");
                messageOrder[r] = I(c, "SeqNo");
            }
            if (k is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow)
            {
                var guard = S(c, "PDATA2").Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("guard=", StringComparison.OrdinalIgnoreCase));
                if (guard != null) ModelRepository.SetFieldForLoad(r, FieldKeys.Guard, guard[6..]);
            }
            var owner = k is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Connector or ElementKind.Message ? s.Owner ?? model.Root : model.DefaultOwnerFor(s.Owner ?? model.Root, k);
            if (!Metamodel.CanOwn(owner.Kind, k)) owner = model.DefaultOwnerFor(owner, k);
            model.AttachForLoad(owner, r);
            relById[I(c, "Connector_ID")] = r;
            rels++;
        }

        EaInstances.Resolve(model, instances, objById, warnings);   // after generalizations: slots may be for inherited attributes
        EaInstances.TypeLinks(model);

        // Diagrams and layouts.
        var diagramObjects = Query("select Diagram_ID, Object_ID, RectTop, RectLeft, RectRight, RectBottom from t_diagramobjects order by Sequence desc")
            .GroupBy(r => I(r, "Diagram_ID")).ToDictionary(g => g.Key, g => g.ToList());
        var diagramLinks = Query("select DiagramID, ConnectorID, Hidden from t_diagramlinks").GroupBy(r => I(r, "DiagramID")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var d in Query("select Diagram_ID, Package_ID, ParentID, Diagram_Type, Name, StyleEx from t_diagram"))
        {
            var owner = I(d, "ParentID") > 0 && objById.TryGetValue(I(d, "ParentID"), out var po) ? po : pkgById.GetValueOrDefault(I(d, "Package_ID")) ?? model.Root;
            var type = MapDiagram(S(d, "Diagram_Type"), S(d, "StyleEx"));
            if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            {
                var shown = (diagramObjects.GetValueOrDefault(I(d, "Diagram_ID")) ?? []).Select(o => objById.GetValueOrDefault(I(o, "Object_ID"))).OfType<Element>().ToList();
                var behavior = shown.Select(x => x.Owner).FirstOrDefault(o => o != null && Metamodel.IsValidDiagramContext(type, o.Kind));
                if (behavior != null) owner = behavior;
                else type = owner.Kind is ElementKind.Package or ElementKind.Model ? DiagramType.Class : DiagramType.Package;
                if (!Metamodel.IsValidDiagramContext(type, owner.Kind)) { owner = model.DefaultOwnerFor(owner, ElementKind.Package); type = DiagramType.Class; }
            }
            var diagram = new Diagram(type);
            ModelRepository.SetFieldForLoad(diagram, FieldKeys.Name, S(d, "Name"));
            model.AttachForLoad(owner, diagram);
            foreach (var o in diagramObjects.GetValueOrDefault(I(d, "Diagram_ID")) ?? [])
            {
                if (!objById.TryGetValue(I(o, "Object_ID"), out var el) || diagram.Shows(el)) continue;
                double l = I(o, "RectLeft"), r = I(o, "RectRight"), top = -I(o, "RectTop"), bottom = -I(o, "RectBottom");
                model.AddNodeForLoad(diagram, new DiagramNode(el, new Bounds(l, Math.Min(top, bottom), Math.Max(10, r - l), Math.Max(10, Math.Abs(bottom - top)))));
            }
            foreach (var n in diagram.Nodes.Where(n => operandSizes.ContainsKey(n.Element)))
                PlaceSeparators(diagram, n, operandSizes[n.Element], warnings);
            // Messages: EA stacks them by sequence number; each gets a height below the lifeline heads in that order.
            var messageRank = 0;
            var lifelineTop = diagram.Nodes.Where(n => n.Element.Kind == ElementKind.Lifeline).Select(n => n.Bounds.Y).DefaultIfEmpty(0).Min();
            foreach (var link in (diagramLinks.GetValueOrDefault(I(d, "Diagram_ID")) ?? [])
                         .OrderBy(l => relById.TryGetValue(I(l, "ConnectorID"), out var r) ? messageOrder.GetValueOrDefault(r) : 0))
            {
                if (S(link, "Hidden") is "1" or "True" || !relById.TryGetValue(I(link, "ConnectorID"), out var rel)) continue;
                if (diagram.NodeFor(rel.Source) is not { } sn || diagram.NodeFor(rel.Target) is not { } tn) continue;
                var edge = new DiagramEdge(rel, sn, tn);
                if (rel.Kind == ElementKind.Message) edge.Waypoints = [new Pt(sn.Bounds.Center.X, lifelineTop + 80 + 40 * messageRank++)];
                model.AddEdgeForLoad(diagram, edge);
            }
        }

        model.History.Clear();
        return (model, new ImportReport(objById.Count, rels, warnings));
    }

    /// <summary>
    /// EA data models ("database engineering"): column lengths / precision and NOT NULL from t_attribute, primary keys,
    /// unique constraints and indexes from «PK» / «unique» / «index» operations of tables. Returns the columns of each
    /// «FK» operation by (table, name), for the foreign key connectors. Repositories without these columns are left as is.
    /// </summary>
    private static Dictionary<(int Table, string Name), List<string>> ReadDataModel(ModelRepository model, Func<string, List<Dictionary<string, object?>>> query,
        Dictionary<int, Element> columns, Dictionary<int, Element> objects)
    {
        static string S(Dictionary<string, object?> row, string col) => row.TryGetValue(col, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        static int I(Dictionary<string, object?> row, string col) => int.TryParse(S(row, col), out var v) ? v : 0;
        var fks = new Dictionary<(int, string), List<string>>();
        if (columns.Count == 0) return fks;
        var attrCols = query("pragma table_info('t_attribute')").Select(r => S(r, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (attrCols.Contains("Length") && attrCols.Contains("Precision") && attrCols.Contains("Scale") && attrCols.Contains("AllowDuplicates"))
            foreach (var a in query("select ID, Length, Precision, Scale, AllowDuplicates from t_attribute"))
            {
                if (!columns.TryGetValue(I(a, "ID"), out var c)) continue;
                var type = c.Get(FieldKeys.SqlType);
                if (!type.Contains('(') && type.Length > 0)
                {
                    if (I(a, "Length") > 0) type = $"{type}({I(a, "Length")})";
                    else if (I(a, "Precision") > 0) type = I(a, "Scale") > 0 ? $"{type}({I(a, "Precision")},{I(a, "Scale")})" : $"{type}({I(a, "Precision")})";
                    ModelRepository.SetFieldForLoad(c, FieldKeys.SqlType, type);
                }
                // EA stores NOT NULL in AllowDuplicates.
                ModelRepository.SetFieldForLoad(c, FieldKeys.Nullable, I(a, "AllowDuplicates") == 1 ? null : "true");
            }
        var opCols = query("pragma table_info('t_operation')").Select(r => S(r, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!opCols.Contains("Stereotype")) return fks;
        var parms = query("select OperationID, Name from t_operationparams order by Pos").GroupBy(p => I(p, "OperationID")).ToDictionary(g => g.Key, g => g.Select(p => S(p, "Name")).ToList());
        foreach (var op in query("select OperationID, Object_ID, Name, Stereotype from t_operation"))
        {
            if (!objects.TryGetValue(I(op, "Object_ID"), out var table) || table.Kind != ElementKind.Table) continue;
            var names = parms.GetValueOrDefault(I(op, "OperationID")) ?? [];
            switch (S(op, "Stereotype").ToLowerInvariant())
            {
                case "pk":
                    foreach (var c in table.OwnedElements.Where(x => x.Kind == ElementKind.Column && names.Contains(x.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        ModelRepository.SetFieldForLoad(c, FieldKeys.PrimaryKey, "true");
                        ModelRepository.SetFieldForLoad(c, FieldKeys.Nullable, null);
                    }
                    ModelRepository.SetFieldForLoad(table, Core.DataModeling.DbFields.PkName, S(op, "Name"));
                    break;
                case "fk":
                    fks[(I(op, "Object_ID"), S(op, "Name"))] = names;
                    break;
                case "unique" or "index":
                {
                    var e = new Element(S(op, "Stereotype").Equals("index", StringComparison.OrdinalIgnoreCase) ? ElementKind.DbIndex : ElementKind.DbConstraint);
                    ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(op, "Name"));
                    ModelRepository.SetFieldForLoad(e, Core.DataModeling.DbFields.Columns, string.Join(", ", names));
                    if (e.Kind == ElementKind.DbConstraint) ModelRepository.SetFieldForLoad(e, FieldKeys.Subtype, Core.DataModeling.DbFields.UniqueConstraint);
                    model.AttachForLoad(table, e);
                    break;
                }
            }
        }
        return fks;
    }

    /// <summary>
    /// Separators of a fragment shape from its EA partition heights: operand i starts below the heights of the operands above it.
    /// When the heights do not fit the shape (EA adds guard rows it does not store), the rest are spread evenly and reported.
    /// </summary>
    private static void PlaceSeparators(Diagram d, DiagramNode n, List<double> sizes, List<string> warnings)
    {
        var ops = InteractionOperands.Of(n.Element);
        var top = 0.0;
        var guessed = 0;
        for (var i = 1; i < ops.Count; i++)
        {
            top += i - 1 < sizes.Count ? sizes[i - 1] : double.NaN;
            if (double.IsNaN(top) || top <= 0 || top >= n.Bounds.Height) { guessed++; continue; }
            ModelRepository.SetNodeStyleForLoad(n, InteractionOperands.StyleKey(ops[i]), InteractionOperands.Format(top));
        }
        if (guessed > 0)
            warnings.Add($"Approximated: diagram '{d.Name}', {n.Element.Get(FieldKeys.Operator)} fragment: {guessed} operand separator(s) did not fit the stored heights and were spread evenly");
    }

    /// <summary>The (guard, height) pairs of an EA "Partitions" description: <c>@PAR;Name=x &gt; 0;Size=80;GUID={…};@ENDPAR;</c>….</summary>
    internal static List<(string? Guard, double Size)> ParsePartitions(string description)
    {
        var list = new List<(string?, double)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(description, "@PAR;(.*?)@ENDPAR;", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string? name = null; var size = double.NaN;
            foreach (var part in m.Groups[1].Value.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var (k, v) = (part[..eq], part[(eq + 1)..]);
                if (k == "Name") name = v.Trim();
                else if (k == "Size" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sz)) size = sz;
            }
            // EA shows guards in brackets and users often type them: "[x > 0]" is the guard "x > 0".
            if (name is { Length: >= 2 } && name[0] == '[' && name[^1] == ']') name = name[1..^1].Trim();
            list.Add((string.IsNullOrEmpty(name) ? null : name, size));
        }
        return list;
    }

    // Stereotypes EA uses to mark what Meridian expresses through the element kind or subtype.
    private static bool IsKindStereo(string s) => s.ToLowerInvariant() is "datastore" or "centralbuffer" or "fork" or "join";

    private static bool IsSysmlStereo(string s) => s is "block" or "interfaceBlock" or "valueType" or "constraintBlock" or "requirement" or "testCase" or "table";

    /// <summary>EA combined fragment operators by t_object.NType (EA's InteractionFragment subtype).</summary>
    internal static readonly string[] FragmentOperators = ["alt", "opt", "break", "par", "loop", "critical", "neg", "assert", "strict", "seq", "ignore", "consider"];

    /// <summary>
    /// Element kind of an EA object (t_object.Object_Type, Stereotype, NType). <paramref name="subtype"/> is the Meridian subtype
    /// (pseudostate kind, object node kind) or, for a combined fragment, its operator. <paramref name="approx"/> says what was
    /// guessed, when anything was. See docs-site/docs/import-export.md for which codes are verified.
    /// </summary>
    internal static ElementKind? MapObject(string type, string stereo, int ntype, bool inStateMachine, int outgoing, out string? subtype, out string? approx)
    {
        subtype = null; approx = null;
        var st = stereo.ToLowerInvariant();
        switch (type)
        {
            case "StateNode":
                // EA "StateNode" NType: 3 initial, 4 final, 5 shallow history, 11 choice, 12 terminate, 13 entry point, 14 exit point,
                // 15 deep history, 100 activity initial, 101 activity final, 102 flow final.
                switch (ntype)
                {
                    case 3: return ElementKind.InitialState;
                    case 4: return ElementKind.FinalState;
                    case 100: return ElementKind.InitialNode;
                    case 101 or 102: return ElementKind.ActivityFinalNode;
                }
                (subtype, approx) = ntype switch
                {
                    5 => (Pseudostates.ShallowHistory, null),
                    15 => (Pseudostates.DeepHistory, null),
                    11 => (Pseudostates.Choice, null),
                    12 => (Pseudostates.Terminate, null),
                    13 => (Pseudostates.EntryPoint, null),
                    14 => (Pseudostates.ExitPoint, null),
                    10 => (Pseudostates.Junction, "NType 10 read as a junction; this code is not confirmed by Sparx documentation"),
                    _ => (Pseudostates.Choice, $"unknown pseudostate NType {ntype} imported as a choice"),
                };
                return ElementKind.Pseudostate;
            case "EntryPoint": subtype = Pseudostates.EntryPoint; return ElementKind.Pseudostate;
            case "ExitPoint": subtype = Pseudostates.ExitPoint; return ElementKind.Pseudostate;
            case "Synchronization" when inStateMachine:
                subtype = st == "join" ? Pseudostates.Join : st == "fork" ? Pseudostates.Fork : outgoing > 1 ? Pseudostates.Fork : Pseudostates.Join;
                if (st is not ("join" or "fork")) approx = $"fork / join bar read as a {subtype} from its {outgoing} outgoing transition(s)";
                return ElementKind.Pseudostate;
            case "CentralBufferNode": subtype = Subtypes.CentralBuffer; return ElementKind.ObjectNode;
            case "DataStore" or "DataStoreNode": subtype = Subtypes.DataStore; return ElementKind.ObjectNode;
            case "ObjectNode": return ElementKind.ObjectNode;
            case "Object" when st == "datastore": subtype = Subtypes.DataStore; return ElementKind.ObjectNode;
            case "Object" when st == "centralbuffer": subtype = Subtypes.CentralBuffer; return ElementKind.ObjectNode;
            case "Interaction": return ElementKind.Interaction;
            case "Sequence": return ElementKind.Lifeline;
            case "InteractionFragment":
                // NType 0 alt, 1 opt, 3 par, 4 loop are confirmed; the rest follow EA's toolbox order.
                if (ntype >= 0 && ntype < FragmentOperators.Length) subtype = FragmentOperators[ntype];
                else { subtype = "alt"; approx = $"unknown fragment operator NType {ntype} imported as alt"; }
                if (ntype is 2 or >= 5 and < 12) approx = $"operator NType {ntype} read as '{subtype}' (not confirmed by Sparx documentation)";
                return ElementKind.CombinedFragment;
        }
        return MapObject(type, stereo, ntype);
    }

    private static ElementKind? MapObject(string type, string stereo, int ntype) => type switch
    {
        "Class" => stereo switch
        {
            "block" => ElementKind.Block, "interfaceBlock" => ElementKind.InterfaceBlock, "valueType" => ElementKind.ValueType,
            "constraintBlock" or "ConstraintBlock" => ElementKind.ConstraintBlock, "requirement" => ElementKind.Requirement, "testCase" => ElementKind.TestCase,
            "table" => ElementKind.Table, _ => ElementKind.Class,
        },
        "Interface" => ElementKind.Interface,
        "Requirement" or "Feature" => ElementKind.Requirement,
        "Actor" => ElementKind.Actor,
        "UseCase" => ElementKind.UseCase,
        "Component" => ElementKind.Component,
        "Enumeration" => ElementKind.Enumeration,
        "DataType" or "PrimitiveType" => stereo == "valueType" ? ElementKind.ValueType : ElementKind.DataType,
        "Activity" => ElementKind.Activity,
        "Action" => ElementKind.Action,
        "StateMachine" => ElementKind.StateMachine,
        "State" => ElementKind.State,
        "Decision" or "MergeNode" => ElementKind.DecisionNode,
        "Synchronization" => ElementKind.ForkNode,
        "ActivityPartition" => ElementKind.Partition,
        "Port" => ElementKind.Port,
        "Part" or "Property" => ElementKind.Property,
        "Note" => ElementKind.Comment,
        "Object" => ElementKind.InstanceSpecification,
        "Node" => stereo.ToLowerInvariant() switch
        {
            "device" => ElementKind.Device, "executionenvironment" or "execution environment" => ElementKind.ExecutionEnvironment, _ => ElementKind.Node,
        },
        "Device" => ElementKind.Device,
        "ExecutionEnvironment" => ElementKind.ExecutionEnvironment,
        "Artifact" => stereo.Equals("deployment spec", StringComparison.OrdinalIgnoreCase) ? ElementKind.DeploymentSpecification : ElementKind.Artifact,
        "DeploymentSpecification" => ElementKind.DeploymentSpecification,
        "Signal" => ElementKind.Class,
        "Constraint" => ElementKind.Comment,
        _ => null,
    };

    private static ElementKind? MapConnector(string type, string subType, string stereo) => stereo.ToLowerInvariant() switch
    {
        "satisfy" => ElementKind.Satisfy,
        "verify" => ElementKind.Verify,
        "refine" => ElementKind.Refine,
        "trace" => ElementKind.Trace,
        "derivereqt" or "derive" => ElementKind.DeriveReqt,
        "copy" => ElementKind.Copy,
        "allocate" => ElementKind.Allocate,
        "include" => ElementKind.Include,
        "extend" => ElementKind.Extend,
        "fk" => ElementKind.ForeignKey,
        "deploy" => ElementKind.Deployment,
        "manifest" => ElementKind.Manifestation,
        "communicationpath" => ElementKind.CommunicationPath,
        _ => type switch
        {
            "Deployment" => ElementKind.Deployment,
            "Manifest" or "Manifestation" => ElementKind.Manifestation,
            "CommunicationPath" => ElementKind.CommunicationPath,
            "Association" => ElementKind.Association,
            "Aggregation" => subType.Equals("Strong", StringComparison.OrdinalIgnoreCase) ? ElementKind.Composition : ElementKind.Aggregation,
            "Generalization" => ElementKind.Generalization,
            "Realisation" or "Realization" => ElementKind.Realization,
            "Dependency" or "Abstraction" or "Usage" => ElementKind.Dependency,
            "ControlFlow" => ElementKind.ControlFlow,
            "ObjectFlow" => ElementKind.ObjectFlow,
            "StateFlow" => ElementKind.Transition,
            "Connector" or "Assembly" => ElementKind.Connector,
            "InformationFlow" => ElementKind.Trace,
            "Sequence" => ElementKind.Message,
            _ => null,
        },
    };

    private static DiagramType MapDiagram(string type, string styleEx)
    {
        if (styleEx.Contains("BlockDefinition", StringComparison.OrdinalIgnoreCase)) return DiagramType.BlockDefinition;
        if (styleEx.Contains("InternalBlock", StringComparison.OrdinalIgnoreCase)) return DiagramType.InternalBlock;
        if (styleEx.Contains("Requirement", StringComparison.OrdinalIgnoreCase)) return DiagramType.Requirement;
        if (styleEx.Contains("Parametric", StringComparison.OrdinalIgnoreCase)) return DiagramType.Parametric;
        return type switch
        {
            "Use Case" => DiagramType.UseCase,
            "Activity" => DiagramType.Activity,
            "Statechart" => DiagramType.StateMachine,
            "Sequence" => DiagramType.Sequence,
            "Package" => DiagramType.Package,
            "Requirements" => DiagramType.Requirement,
            "Deployment" => DiagramType.Deployment,
            _ => DiagramType.Class,
        };
    }

    private static string PrimitiveName(string t) => t.ToLowerInvariant() switch
    {
        "int" or "integer" or "long" or "short" => "Integer",
        "double" or "float" or "real" or "decimal" => "Real",
        "bool" or "boolean" => "Boolean",
        "string" or "char" => "String",
        _ => t,
    };

    /// <summary>EA notes may contain simple HTML-ish markup.</summary>
    private static string Clean(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace("<br/>", "\n").Replace("<br>", "\n"), "<[^>]+>", "").Trim();
}
