using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class MagicDrawImporter
{
    private sealed partial class Builder
    {
        // ------------------------------------------------------------------ types

        private static readonly string[] Primitives = ["UnlimitedNatural", "Integer", "Boolean", "String", "Real"];

        private static string? PrimitiveName(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return null;
            foreach (var p in Primitives)
                if (candidate == p || candidate.EndsWith("_" + p, StringComparison.Ordinal) || candidate.EndsWith("::" + p, StringComparison.Ordinal)
                    || candidate.EndsWith("#" + p, StringComparison.Ordinal) || candidate.EndsWith("." + p, StringComparison.Ordinal))
                    return p;
            return null;
        }

        /// <summary>Type of a typed element: an imported classifier's id, a UML primitive, or the name of an external library type.</summary>
        private string? ResolveTypeOf(XElement x, out bool external)
        {
            external = false;
            var typeId = Ref(x, "type");
            if (typeId != null && _map.TryGetValue(typeId, out var t)) return t.Id;
            var te = Child(x, "type");
            var path = te?.Descendants().FirstOrDefault(d => d.Name.LocalName == "referenceExtension")?.Attribute("referentPath")?.Value;
            if ((PrimitiveName(typeId) ?? PrimitiveName(path) ?? PrimitiveName(te?.Attribute("href")?.Value)) is { } prim) return prim;
            if (path != null) { external = true; return path[(path.LastIndexOf(':') + 1)..]; }
            if (typeId != null) { external = true; return _names.GetValueOrDefault(typeId); }
            return null;
        }

        private void ResolveTypes()
        {
            var external = 0;
            foreach (var (el, x) in _typeRefs)
            {
                var t = ResolveTypeOf(x, out var ext);
                if (ext) { external++; NoteExternalType(el, x); }   // a used project's type (MagicDrawUsedProjects.cs)
                ModelRepository.SetFieldForLoad(el, FieldKeys.Type, t);
            }
            foreach (var (ll, propId) in _represents)
            {
                if (!_props.TryGetValue(propId, out var p)) continue;
                if (p.TypeId != null && _map.TryGetValue(p.TypeId, out var t)) ModelRepository.SetFieldForLoad(ll, FieldKeys.Type, t.Id);
                if (string.IsNullOrEmpty(ll.Name) && p.Name != null) ModelRepository.SetFieldForLoad(ll, FieldKeys.Name, p.Name);
            }
            foreach (var (op, x) in _operations) ModelRepository.SetFieldForLoad(op, FieldKeys.Signature, Signature(x));
            if (external > 0) _report.Skip("Types from used projects / libraries (kept as type names)", external);
        }

        private string TypeName(XElement p)
        {
            var t = ResolveTypeOf(p, out _);
            if (t == null) return "";
            return _model.Find(t)?.Name ?? t;
        }

        private string? Signature(XElement op)
        {
            var ps = op.Elements().Where(e => e.Name.LocalName == "ownedParameter").ToList();
            var ins = ps.Where(p => Attr(p, "direction") != "return")
                .Select(p => TypeName(p) is { Length: > 0 } tn ? $"{Attr(p, "name")} : {tn}" : Attr(p, "name") ?? "arg");
            var ret = ps.FirstOrDefault(p => Attr(p, "direction") == "return");
            return $"({string.Join(", ", ins)})" + (ret != null && TypeName(ret) is { Length: > 0 } rt ? $" : {rt}" : "");
        }

        // ------------------------------------------------------------------ events (transition triggers, accept event actions)

        private string? EventText(string eventId)
        {
            if (!_events.TryGetValue(eventId, out var ev)) return _names.GetValueOrDefault(eventId);
            switch (XmiType(ev))
            {
                case "SignalEvent" when Ref(ev, "signal") is { } s && _names.TryGetValue(s, out var sn): return sn;
                case "CallEvent" when Ref(ev, "operation") is { } o && _names.TryGetValue(o, out var on): return on;
                case "TimeEvent":
                {
                    var when = ValueText(Child(ev, "when"));
                    return when == null ? Attr(ev, "name") : Attr(ev, "isRelative") == "true" ? $"after({when})" : $"at({when})";
                }
                case "ChangeEvent":
                    return ValueText(Child(ev, "changeExpression")) is { } ce ? $"when({ce})" : Attr(ev, "name");
                case "AnyReceiveEvent":
                    return "all";
            }
            return Attr(ev, "name");
        }

        // ------------------------------------------------------------------ relationships

        private void ResolveRelationships()
        {
            foreach (var p in _rels)
            {
                try { ResolveRelationship(p); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _report.Warn($"Skipped {p.Label} {p.Id}: {ex.Message}");
                    _report.Skip("Relationships that failed to import");
                }
            }
        }

        private void ResolveRelationship(PendingRel p)
        {
            var kind = p.Kind;
            var src = p.Src;
            var tgt = p.Tgt;
            string? mult = null, srcMult = null, srcPart = null, tgtPart = null;
            var x = p.X;

            if (kind == ElementKind.Association)
            {
                var ends = Refs(x, "memberEnd").Select(id => _props.GetValueOrDefault(id)).ToList();
                if (ends.Count != 2 || ends.Any(e => e == null)) { _report.Warn($"Association {p.Id} skipped: it needs two resolvable member ends"); _report.Skip("Associations with unresolved ends"); return; }
                var whole = ends.FindIndex(e => e!.Aggregation is "composite" or "shared");
                // Plain associations read in memberEnd order; aggregations go from the whole to the part.
                var partEnd = ends[whole >= 0 ? whole : 1]!;
                var otherEnd = ends[whole >= 0 ? 1 - whole : 0]!;
                kind = whole < 0 ? ElementKind.Association : partEnd.Aggregation == "composite" ? ElementKind.Composition : ElementKind.Aggregation;
                src = otherEnd.TypeId;
                tgt = partEnd.TypeId;
                if (whole < 0 && ends.FirstOrDefault(e => e!.OwnerId != p.Id) is { } classOwned)
                {
                    // An end owned by a class (a reference property) is navigable from that class.
                    src = classOwned.OwnerId;
                    tgt = classOwned.TypeId;
                    otherEnd = ends.First(e => !ReferenceEquals(e, classOwned))!;
                    partEnd = classOwned;
                }
                mult = partEnd.Multiplicity;
                // The other member end's multiplicity is drawn at the source end (only when the file states one).
                srcMult = otherEnd.ExplicitMultiplicity;
            }
            else if (kind == ElementKind.Message)
            {
                src = src != null ? _occurrence.GetValueOrDefault(src) : null;
                tgt = tgt != null ? _occurrence.GetValueOrDefault(tgt) : null;
                if (src == null || tgt == null) { _report.Skip("Lost / found messages and messages to gates"); return; }
            }
            else if (kind is ElementKind.Connector or ElementKind.Binding)
            {
                var ends = x.Elements().Where(e => e.Name.LocalName == "end").ToList();
                srcPart = Ref(ends[0], "partWithPort");
                tgtPart = Ref(ends[1], "partWithPort");
            }

            if (src == null || tgt == null || !_map.TryGetValue(src, out var s) || !_map.TryGetValue(tgt, out var t))
            {
                // An end in a used project: kept aside, so a usage of that project can link it (MagicDrawUsedProjects.cs).
                if (NoteExternalRelationship(p, kind, src, tgt)) { _report.Skip("Relationships to used-project elements"); return; }
                _report.Warn($"{p.Label} {p.Id} skipped: an end is not in the project");
                _report.Skip("Relationships with unresolved ends");
                return;
            }
            if (s is Relationship || t is Relationship) { _report.Skip("Relationships between relationships"); return; }

            if (Metamodel.CheckEnds(kind, s.Kind, t.Kind) is { } problem)
            {
                var fallback = kind switch
                {
                    ElementKind.Binding when Metamodel.CheckEnds(ElementKind.Connector, s.Kind, t.Kind) == null => ElementKind.Connector,
                    ElementKind.Satisfy or ElementKind.Verify or ElementKind.DeriveReqt or ElementKind.Copy => ElementKind.Trace,
                    ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation or ElementKind.Connector
                        or ElementKind.Binding or ElementKind.Message or ElementKind.Transition => (ElementKind?)null,
                    ElementKind.ControlFlow or ElementKind.ObjectFlow => null,
                    _ => ElementKind.Dependency,
                };
                if (fallback == null)
                {
                    _report.Warn($"{p.Label} {p.Id} skipped: {problem}");
                    _report.Skip($"{p.Label} with invalid ends");
                    return;
                }
                _report.Warn($"{p.Label} {p.Id}: {problem}; imported as {Metamodel.DisplayName(fallback.Value)}");
                kind = fallback.Value;
            }

            var r = new Relationship(kind, s, t, p.Id != null && !_map.ContainsKey(p.Id) && _model.Find(p.Id) == null ? p.Id : null);
            ModelRepository.SetFieldForLoad(r, FieldKeys.Name, Attr(x, "name"));
            ModelRepository.SetFieldForLoad(r, FieldKeys.Multiplicity, mult);
            ModelRepository.SetFieldForLoad(r, FieldKeys.SourceMultiplicity, srcMult);
            ModelRepository.SetFieldForLoad(r, FieldKeys.SourcePart, srcPart != null && _map.TryGetValue(srcPart, out var sp) ? sp.Id : null);
            ModelRepository.SetFieldForLoad(r, FieldKeys.TargetPart, tgtPart != null && _map.TryGetValue(tgtPart, out var tp) ? tp.Id : null);
            switch (kind)
            {
                case ElementKind.Transition:
                {
                    ModelRepository.SetFieldForLoad(r, FieldKeys.Guard, ValueText(Child(x, "guard")));
                    ModelRepository.SetFieldForLoad(r, FieldKeys.Effect, BehaviorText(Child(x, "effect")));
                    var triggers = x.Elements().Where(e => e.Name.LocalName == "trigger")
                        .Select(tr => Ref(tr, "event") is { } ev ? EventText(ev) : Attr(tr, "name")).OfType<string>().ToList();
                    if (triggers.Count > 0)
                    {
                        ModelRepository.SetFieldForLoad(r, FieldKeys.TagPrefix + "trigger", string.Join(", ", triggers));
                        // Meridian reads a transition's trigger from its name (signal, after(…), when(…)).
                        if (string.IsNullOrEmpty(r.Name)) ModelRepository.SetFieldForLoad(r, FieldKeys.Name, string.Join(", ", triggers));
                    }
                    break;
                }
                case ElementKind.ControlFlow or ElementKind.ObjectFlow:
                {
                    var g = ValueText(Child(x, "guard"));
                    if (g is not (null or "true")) ModelRepository.SetFieldForLoad(r, FieldKeys.Guard, g);
                    break;
                }
                case ElementKind.Message:
                    ModelRepository.SetFieldForLoad(r, FieldKeys.MessageSort, Attr(x, "messageSort") ?? "synchCall");
                    if (Ref(x, "signature") is { } sig && _names.TryGetValue(sig, out var sigName))
                    {
                        if (string.IsNullOrEmpty(r.Name)) ModelRepository.SetFieldForLoad(r, FieldKeys.Name, sigName);
                        if (_map.TryGetValue(sig, out var op) && op.Kind == ElementKind.Operation) ModelRepository.SetFieldForLoad(r, FieldKeys.Signature, op.Get(FieldKeys.Signature));
                    }
                    break;
            }
            if (p.Id != null) ApplyStereotypes(r, p.Id);

            var owner = kind switch
            {
                ElementKind.Generalization or ElementKind.Include or ElementKind.Extend or ElementKind.Realization when x.Name.LocalName is "generalization" or "include" or "extend" or "interfaceRealization"
                    => _model.DefaultOwnerFor(s.Owner ?? _model.Root, kind),
                _ => p.Owner,
            };
            if (!Metamodel.CanOwn(owner.Kind, kind)) owner = _model.DefaultOwnerFor(owner, kind);
            if (!_model.Contains(owner)) owner = _model.Root;
            _model.AttachForLoad(owner, r);
            if (p.Id != null) _map.TryAdd(p.Id, r);
            _report.Count(kind == p.Kind ? p.Label : Metamodel.DisplayName(kind));
        }

        // ------------------------------------------------------------------ references stored in fields

        private void ResolveFieldRefs()
        {
            foreach (var (el, field, refId, asName) in _fieldRefs)
            {
                if (asName)
                {
                    var name = field == ExtFieldKeys.AcceptEvent ? EventText(refId) : _names.GetValueOrDefault(refId);
                    ModelRepository.SetFieldForLoad(el, field, name);
                }
                else if (_map.TryGetValue(refId, out var target)) ModelRepository.SetFieldForLoad(el, field, target.Id);
            }
            foreach (var (id, conveyed, connectors) in _infoFlows)
            {
                var item = conveyed.Select(c => _map.TryGetValue(c, out var e) ? e.Id : _names.GetValueOrDefault(c)).FirstOrDefault(v => v != null);
                var hit = false;
                foreach (var cid in connectors)
                    if (_map.TryGetValue(cid, out var conn) && conn.Kind is ElementKind.Connector or ElementKind.Binding && item != null)
                    {
                        ModelRepository.SetFieldForLoad(conn, FieldKeys.ItemFlow, item);
                        hit = true;
                    }
                if (hit) _report.Count("Item flow (on connector)");
                else _report.Skip("Information / item flows not realized by a connector");
            }
        }

        private void ClassifyProperties()
        {
            foreach (var p in _properties)
            {
                var type = _model.Find(p.TypeRef);
                string label;
                if (p.Owner?.Kind == ElementKind.ConstraintBlock) label = "Constraint parameter";
                else if (p.Stereotypes.Contains("flowProperty")) label = "Flow property";
                else if (type?.Kind == ElementKind.ConstraintBlock) label = "Constraint property";
                else if (type != null && type.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock or ElementKind.Actor or ElementKind.Interface)
                    label = p.Get(AggregationField) == "composite" ? "Part property" : "Reference property";
                else if (type?.Kind is ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration || Metamodel.PrimitiveTypes.Contains(p.TypeRef))
                    label = "Value property";
                else label = "Property";
                if (label != "Part property" && label != "Reference property" && p.Get(AggregationField) == "none") ModelRepository.SetFieldForLoad(p, AggregationField, null);
                _report.Count(label);
            }
        }
    }
}
