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

        var objects = Query("select Object_ID, Object_Type, Name, Alias, Note, Package_ID, Stereotype, ParentID, Classifier, Abstract, NType from t_object order by Object_ID");
        var props = Query("select Object_ID, Property, Value, Notes from t_objectproperties")
            .GroupBy(r => I(r, "Object_ID")).ToDictionary(g => g.Key, g => g.ToList());
        var objById = new Dictionary<int, Element>();
        var typeRefs = new List<(Element e, int classifier)>();
        var helperContainers = new Dictionary<(Element pkg, ElementKind kind), Element>();

        Element ContainerFor(Element pkg, ElementKind childKind)
        {
            // EA lets actions/states live directly in packages; UML needs an owning activity / state machine.
            var behaviorKind = Metamodel.IsActivityNode(childKind) || childKind == ElementKind.Partition ? ElementKind.Activity : ElementKind.StateMachine;
            if (!helperContainers.TryGetValue((pkg, behaviorKind), out var c))
            {
                c = new Element(behaviorKind);
                ModelRepository.SetFieldForLoad(c, FieldKeys.Name, $"{pkg.Name} {(behaviorKind == ElementKind.Activity ? "activity" : "states")}");
                model.AttachForLoad(pkg, c);
                helperContainers[(pkg, behaviorKind)] = c;
            }
            return c;
        }

        // Pass 1: create every element; pass 2: nest by ParentID.
        var pendingParents = new List<(Element e, int parentId, Element pkg)>();
        foreach (var o in objects)
        {
            var kind = MapObject(S(o, "Object_Type"), S(o, "Stereotype"), I(o, "NType"));
            if (kind is null) { if (S(o, "Object_Type") is not ("Package" or "Boundary" or "Text" or "NoteLink")) warnings.Add($"Skipped {S(o, "Object_Type")} '{S(o, "Name")}'"); continue; }
            var e = new Element(kind.Value);
            ModelRepository.SetFieldForLoad(e, FieldKeys.Name, S(o, "Name"));
            var note = Clean(S(o, "Note"));
            if (kind == ElementKind.Comment) ModelRepository.SetFieldForLoad(e, FieldKeys.Body, note);
            else if (kind == ElementKind.Requirement) ModelRepository.SetFieldForLoad(e, FieldKeys.Text, note);
            else ModelRepository.SetFieldForLoad(e, FieldKeys.Documentation, note);
            if (S(o, "Abstract") == "1") ModelRepository.SetFieldForLoad(e, FieldKeys.IsAbstract, "true");
            var stereo = S(o, "Stereotype");
            if (stereo.Length > 0 && Metamodel.Keyword(kind.Value) != stereo && !IsSysmlStereo(stereo)) ModelRepository.SetFieldForLoad(e, FieldKeys.Stereotypes, stereo);
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
            objById[I(o, "Object_ID")] = e;
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
                owner = Metamodel.IsActivityNode(item.e.Kind) || Metamodel.IsVertex(item.e.Kind) || item.e.Kind == ElementKind.Partition ? ContainerFor(item.pkg, item.e.Kind) : model.DefaultOwnerFor(owner, item.e.Kind);
            if (!Metamodel.CanOwn(owner.Kind, item.e.Kind)) { warnings.Add($"Could not place {item.e.Kind} '{item.e.Name}'"); attached.Add(item.e); return; }
            model.AttachForLoad(owner, item.e);
            attached.Add(item.e);
        }
        foreach (var item in pendingParents) Attach(item);

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
        foreach (var c in Query("select Connector_ID, Connector_Type, SubType, Stereotype, Name, Start_Object_ID, End_Object_ID, DestCard, PDATA2 from t_connector"))
        {
            if (!objById.TryGetValue(I(c, "Start_Object_ID"), out var s) || !objById.TryGetValue(I(c, "End_Object_ID"), out var t)) continue;
            var kind = MapConnector(S(c, "Connector_Type"), S(c, "SubType"), S(c, "Stereotype"));
            if (kind is null) continue;
            var k = kind.Value;
            // EA draws composition from part to whole; Meridian's composition goes whole → part.
            if (k is ElementKind.Composition or ElementKind.Aggregation) (s, t) = (t, s);
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
            if (k is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation) ModelRepository.SetFieldForLoad(r, FieldKeys.Multiplicity, S(c, "DestCard"));
            if (k is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow)
            {
                var guard = S(c, "PDATA2").Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("guard=", StringComparison.OrdinalIgnoreCase));
                if (guard != null) ModelRepository.SetFieldForLoad(r, FieldKeys.Guard, guard[6..]);
            }
            var owner = k is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Connector ? s.Owner ?? model.Root : model.DefaultOwnerFor(s.Owner ?? model.Root, k);
            if (!Metamodel.CanOwn(owner.Kind, k)) owner = model.DefaultOwnerFor(owner, k);
            model.AttachForLoad(owner, r);
            relById[I(c, "Connector_ID")] = r;
            rels++;
        }

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
            foreach (var link in diagramLinks.GetValueOrDefault(I(d, "Diagram_ID")) ?? [])
            {
                if (S(link, "Hidden") is "1" or "True" || !relById.TryGetValue(I(link, "ConnectorID"), out var rel)) continue;
                if (diagram.NodeFor(rel.Source) is { } sn && diagram.NodeFor(rel.Target) is { } tn) model.AddEdgeForLoad(diagram, new DiagramEdge(rel, sn, tn));
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

    private static bool IsSysmlStereo(string s) => s is "block" or "interfaceBlock" or "valueType" or "constraintBlock" or "requirement" or "testCase" or "table";

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
        "StateNode" => ntype switch { 3 or 100 => ntype == 100 ? ElementKind.InitialNode : ElementKind.InitialState, 4 or 101 or 102 => ntype == 4 ? ElementKind.FinalState : ElementKind.ActivityFinalNode, _ => null },
        "Decision" or "MergeNode" => ElementKind.DecisionNode,
        "Synchronization" => ElementKind.ForkNode,
        "ActivityPartition" => ElementKind.Partition,
        "Port" => ElementKind.Port,
        "Part" or "Property" => ElementKind.Property,
        "Note" => ElementKind.Comment,
        "Signal" or "Object" or "Artifact" => ElementKind.Class,
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
        _ => type switch
        {
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
            "Package" => DiagramType.Package,
            "Requirements" => DiagramType.Requirement,
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
