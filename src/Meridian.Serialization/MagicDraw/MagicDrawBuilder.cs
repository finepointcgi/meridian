using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class MagicDrawImporter
{
    /// <summary>A stereotype application: profile (XML prefix), stereotype name and the application element (tag values).</summary>
    internal sealed record StereoApp(string Profile, string Name, XElement X);

    internal sealed record PropInfo(string? Name, string? TypeId, string? Aggregation, string? OwnerId, string? Multiplicity);

    private sealed record PendingRel(ElementKind Kind, string? Id, string? Src, string? Tgt, Element Owner, XElement X, string Label);

    /// <summary>Builds a Meridian model from MagicDraw XMI in two streaming passes (stereotypes, then the model).</summary>
    private sealed partial class Builder
    {
        private readonly ProjectSource _src;
        private readonly MagicDrawImportReport _report = new();
        private readonly Dictionary<string, List<StereoApp>> _stereo = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Element> _map = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PropInfo> _props = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _occurrence = new(StringComparer.Ordinal);    // occurrence spec -> covered lifeline
        private readonly Dictionary<string, XElement> _events = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);         // any named element id -> name (incl. unmapped: signals, operations)
        private readonly List<PendingRel> _rels = new();
        private readonly List<(Element El, XElement X)> _typeRefs = new();
        private readonly List<(Element El, XElement Op)> _operations = new();
        private readonly List<(Element Lifeline, string PropId)> _represents = new();
        private readonly List<(Element El, string Field, string RefId, bool AsName)> _fieldRefs = new();
        private readonly List<(string Id, List<string> Conveyed, List<string> Connectors)> _infoFlows = new();
        private readonly List<Element> _properties = new();
        private readonly List<DiagramSpec> _diagrams = new();
        private readonly HashSet<string> _warnedOnce = new(StringComparer.Ordinal);
        private ModelRepository _model = null!;
        private string? _rootXmiId;
        private string? _rootName;

        public Builder(ProjectSource src) => _src = src;

        public (ModelRepository, MagicDrawImportReport) Run()
        {
            // Pass 1: stereotype applications (siblings of uml:Model, after it) and the model's id/name.
            foreach (var open in _src.ModelParts) ScanProfiles(open);
            _model = new ModelRepository(NewRoot());
            if (_rootXmiId != null) _map[_rootXmiId] = _model.Root;

            // Pass 2: the model itself.
            foreach (var open in _src.ModelParts) ReadModel(open);

            ResolveTypes();
            ResolveRelationships();
            ResolveFieldRefs();
            ClassifyProperties();
            BuildDiagrams();

            var ignored = _src.OtherEntries.Count(e => !_usedBinaries.Contains(e) && !e.EndsWith(".properties", StringComparison.OrdinalIgnoreCase));
            if (ignored > 0) _report.Skip("Project resources (options, proxies, used-project backups)", ignored);
            _report.Elements = _model.AllElements.Count(e => e is not Relationship && e is not Diagram) - 1;
            _report.Relationships = _model.Relationships.Count();
            _report.Finish();
            _model.History.Clear();
            return (_model, _report);
        }

        private Element NewRoot()
        {
            var root = new Element(ElementKind.Model, _rootXmiId);
            ModelRepository.SetFieldForLoad(root, FieldKeys.Name, _rootName ?? "Imported Model");
            return root;
        }

        private void WarnOnce(string key, string message)
        {
            if (_warnedOnce.Add(key)) _report.Warn(message);
        }

        // ------------------------------------------------------------------ pass 1

        private void ScanProfiles(Func<Stream> open)
        {
            using var r = DepthLimitedXmlReader.Create(open(), ReaderSettings);
            MoveToFirstElement(r);
            if (r.NodeType != XmlNodeType.Element) throw new FormatException("The model part is empty.");
            if (IsUmlModel(r)) { CaptureRoot(r); return; }
            if (r.IsEmptyElement) return;
            var depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                if (IsUmlModel(r)) { CaptureRoot(r); r.Skip(); continue; }
                if (r.LocalName is "Documentation" or "Extension" || IsUmlNamespace(r.NamespaceURI)) { r.Skip(); continue; }
                var prefix = r.Prefix;
                var x = (XElement)XNode.ReadFrom(r);
                foreach (var baseId in BaseIds(x))
                {
                    if (!_stereo.TryGetValue(baseId, out var l)) _stereo[baseId] = l = new();
                    l.Add(new StereoApp(prefix, x.Name.LocalName, x));
                }
            }
        }

        private static void MoveToFirstElement(XmlReader r)
        {
            while (r.NodeType != XmlNodeType.Element && r.Read()) { }
        }

        private void CaptureRoot(XmlReader r)
        {
            if (_rootXmiId != null) return;
            _rootXmiId = ReaderAttr(r, "id", xmi: true);
            _rootName = r.GetAttribute("name");
        }

        private static IEnumerable<string> BaseIds(XElement x)
        {
            foreach (var a in x.Attributes().Where(a => a.Name.LocalName.StartsWith("base_", StringComparison.Ordinal)))
                foreach (var id in a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) yield return id;
            foreach (var c in x.Elements().Where(c => c.Name.LocalName.StartsWith("base_", StringComparison.Ordinal)))
                if (IdRef(c) is { } id) yield return id;
        }

        private static bool IsUmlNamespace(string ns) => ns.Contains("/UML/", StringComparison.OrdinalIgnoreCase) && !ns.Contains("Profile", StringComparison.OrdinalIgnoreCase);

        private static bool IsUmlModel(XmlReader r) => r.LocalName == "Model" && r.NamespaceURI.Contains("UML", StringComparison.OrdinalIgnoreCase);

        private static string? ReaderAttr(XmlReader r, string local, bool xmi)
        {
            if (!r.MoveToFirstAttribute()) return null;
            try
            {
                do
                {
                    if (r.LocalName == local && (!xmi || r.NamespaceURI.Contains("XMI", StringComparison.OrdinalIgnoreCase))) return r.Value;
                } while (r.MoveToNextAttribute());
                return null;
            }
            finally { r.MoveToElement(); }
        }

        private static string? ReaderXmiType(XmlReader r)
        {
            var t = ReaderAttr(r, "type", xmi: true);
            if (t == null) return null;
            var i = t.IndexOf(':');
            return i >= 0 ? t[(i + 1)..] : t;
        }

        /// <summary>The start tag only (attributes, no content) as an XElement.</summary>
        private static XElement HeadOf(XmlReader r)
        {
            var x = new XElement(XName.Get(r.LocalName, r.NamespaceURI));
            if (r.MoveToFirstAttribute())
            {
                do
                {
                    if (r.Prefix == "xmlns" || r.LocalName == "xmlns") continue;
                    x.SetAttributeValue(XName.Get(r.LocalName, r.NamespaceURI), r.Value);
                } while (r.MoveToNextAttribute());
                r.MoveToElement();
            }
            return x;
        }

        // ------------------------------------------------------------------ pass 2 (streaming)

        private void ReadModel(Func<Stream> open)
        {
            using var r = DepthLimitedXmlReader.Create(open(), ReaderSettings);
            MoveToFirstElement(r);
            if (r.NodeType != XmlNodeType.Element) return;
            if (IsUmlModel(r)) { StreamContainer(r, _model.Root, ReaderAttr(r, "id", true)); return; }
            if (r.IsEmptyElement) return;
            var depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                if (IsUmlModel(r))
                {
                    var id = ReaderAttr(r, "id", true);
                    var target = id != null && _map.TryGetValue(id, out var existing) ? existing : _model.Root;
                    StreamContainer(r, target, id);
                    continue;
                }
                if (r.LocalName == "Extension")
                {
                    // Top-level extensions hold project data; diagrams are only of interest here.
                    var x = (XElement)XNode.ReadFrom(r);
                    CollectDiagrams(x, _model.Root, _rootXmiId);
                    if (x.Descendants().Any(d => d.Name.LocalName == "mdOwnedDiagrams"))
                        WarnOnce("legacy", "Diagrams stored in the pre-17.0.1 format (mdOwnedDiagrams) are not imported; re-save the project in a newer MagicDraw/Cameo.");
                    continue;
                }
                r.Skip();   // documentation, stereotype applications (read in pass 1)
            }
        }

        /// <summary>Reads the children of a package-like element incrementally; other elements are materialised one subtree at a time.</summary>
        private void StreamContainer(XmlReader r, Element owner, string? ownerXmiId)
        {
            if (r.IsEmptyElement) { r.Read(); return; }
            var depth = r.Depth;
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) { r.Read(); return; }
                if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                if (ReaderXmiType(r) is "Package" or "Model" or "Profile")
                {
                    var head = HeadOf(r);
                    var pkg = SafeCreatePackage(head, owner);
                    if (pkg == null) { r.Skip(); continue; }
                    StreamContainer(r, pkg, XmiId(head));
                    continue;
                }
                var x = (XElement)XNode.ReadFrom(r);
                SafeHandle(x, owner, ownerXmiId);
            }
        }

        private Element? SafeCreatePackage(XElement head, Element owner)
        {
            try { return CreatePackage(head, owner); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _report.Warn($"Skipped package '{Attr(head, "name")}': {ex.Message}");
                return null;
            }
        }

        private Element? CreatePackage(XElement head, Element owner)
        {
            var id = XmiId(head);
            if (id != null && _map.TryGetValue(id, out var existing)) return existing;   // same package in model + shared_model parts
            var kind = XmiType(head) == "Profile" ? ElementKind.Profile : ElementKind.Package;
            return Create(kind, head, owner, id);
        }

        private void SafeHandle(XElement x, Element owner, string? ownerXmiId)
        {
            try { Handle(x, owner, ownerXmiId); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _report.Warn($"Skipped {XmiType(x) ?? x.Name.LocalName} '{Attr(x, "name")}': {ex.Message}");
                _report.Skip("Elements that failed to import");
            }
        }

        private void HandleChildren(XElement x, Element owner, string? ownerXmiId)
        {
            foreach (var c in x.Elements()) SafeHandle(c, owner, ownerXmiId);
        }

        // Features handled by their parent, or carrying no model content of their own.
        private static readonly HashSet<string> IgnoredTags = new(StringComparer.Ordinal)
        {
            "lowerValue", "upperValue", "defaultValue", "type", "general", "memberEnd", "navigableOwnedEnd", "client", "supplier",
            "profileApplication", "packageImport", "elementImport", "appliedProfile", "nameExpression", "specification",
            "ownedParameter", "ownedTemplateSignature", "templateBinding", "clientDependency", "guard", "effect", "entry", "exit",
            "doActivity", "body", "language", "redefinedProperty", "subsettedProperty", "redefinedClassifier", "classifierBehavior",
            "covered", "sendEvent", "receiveEvent", "signature", "represents", "trigger", "deferrableTrigger", "event", "role",
            "partWithPort", "end", "annotatedElement", "conveyed", "realizingConnector", "informationSource", "informationTarget",
            "incoming", "outgoing", "inPartition", "inInterruptibleRegion", "node", "edge_ref", "behavior", "signal", "operation",
            "ownedReception", "interfaceRealization_ref", "redefinedElement", "upperBound", "selection", "transformation",
            "joinSpec", "weight", "decisionInput", "precondition", "postcondition", "ownedEnd", "extensionPoint", "condition",
            "contract", "addition", "extendedCase", "extension", "base_Class", "instance", "classifier", "slot", "submachine",
            "isConjugated", "interactionOperator", "operand_ref", "ownedAttribute_ref",
        };

        private void Handle(XElement c, Element owner, string? ownerXmiId)
        {
            var tag = c.Name.LocalName;
            var type = XmiType(c);
            var id = XmiId(c);
            if (id != null && Attr(c, "name") is { } nm) _names.TryAdd(id, nm);

            switch (tag)
            {
                case "Extension": CollectDiagrams(c, owner, ownerXmiId); return;
                case "ownedComment": HandleComment(c, owner, ownerXmiId); return;
                case "generalization": AddRel(ElementKind.Generalization, id, ownerXmiId, Ref(c, "general"), owner, c, "Generalization"); return;
                case "interfaceRealization": AddRel(ElementKind.Realization, id, ownerXmiId, Ref(c, "contract") ?? Ref(c, "supplier"), owner, c, "Realization"); return;
                case "include": AddRel(ElementKind.Include, id, Ref(c, "includingCase") ?? ownerXmiId, Ref(c, "addition"), owner, c, "Include"); return;
                case "extend": AddRel(ElementKind.Extend, id, Ref(c, "extension") ?? ownerXmiId, Ref(c, "extendedCase"), owner, c, "Extend"); return;
                case "ownedRule": HandleConstraint(c, owner); return;
                case "region":
                    if (id != null) _map.TryAdd(id, owner);
                    if (owner.Kind == ElementKind.State && c.Parent?.Elements().Count(e => e.Name.LocalName == "region") > 1)
                        WarnOnce("regions:" + owner.Id, $"State '{owner.Name}' has orthogonal regions; they were flattened into one.");
                    HandleChildren(c, owner, ownerXmiId);
                    return;
                case "operand":
                    HandleChildren(c, owner, ownerXmiId);   // fragments of an operand belong to the interaction
                    return;
                case "subpartition":
                    break;  // handled below by type (ActivityPartition)
            }
            if (type == null)
            {
                if (!IgnoredTags.Contains(tag) && !tag.StartsWith("_", StringComparison.Ordinal)) _report.Skip($"Unknown XML <{tag}>");
                return;
            }

            switch (type)
            {
                case "Package" or "Model" or "Profile":
                {
                    var pkg = CreatePackage(c, owner);
                    if (pkg != null) HandleChildren(c, pkg, id);
                    return;
                }
                case "Collaboration":
                    // MagicDraw wraps interactions in a collaboration; its attributes are the lifelines' parts.
                    HandleChildren(c, owner, ownerXmiId);
                    return;
                case "Property": HandleProperty(c, owner, ownerXmiId); return;
                case "Port": HandlePort(c, owner); return;
                case "Operation": HandleOperation(c, owner); return;
                case "EnumerationLiteral": Create(ElementKind.EnumerationLiteral, c, owner, id); return;
                case "Connector": HandleConnector(c, owner, id); return;
                case "Association" or "AssociationClass":
                    if (type == "AssociationClass") WarnOnce("assocClass", "Association classes are imported as plain associations.");
                    foreach (var end in c.Elements().Where(e => e.Name.LocalName == "ownedEnd")) RecordProperty(end, id);
                    AddRel(ElementKind.Association, id, null, null, owner, c, "Association");
                    return;
                case "Dependency" or "Abstraction" or "Realization" or "Usage" or "Substitution" or "Refine" or "Trace" or "Manifestation":
                    HandleDependency(c, owner, id, type);
                    return;
                case "InformationFlow":
                    if (id != null) _infoFlows.Add((id, Refs(c, "conveyed"), Refs(c, "realizingConnector")));
                    return;
                case "Transition": HandleTransition(c, owner, id); return;
                case "ControlFlow" or "ObjectFlow":
                {
                    var kind = type == "ObjectFlow" ? ElementKind.ObjectFlow : ElementKind.ControlFlow;
                    AddRel(kind, id, Ref(c, "source"), Ref(c, "target"), owner, c, Metamodel.DisplayName(kind));
                    return;
                }
                case "Message":
                    AddRel(ElementKind.Message, id, Ref(c, "sendEvent"), Ref(c, "receiveEvent"), owner, c, "Message");
                    return;
                case "MessageOccurrenceSpecification" or "OccurrenceSpecification" or "ExecutionOccurrenceSpecification" or "DestructionOccurrenceSpecification":
                    if (id != null && Ref(c, "covered") is { } cov) _occurrence[id] = cov;
                    return;
                case "BehaviorExecutionSpecification" or "ActionExecutionSpecification":
                    _report.Skip("Execution specifications (derived from messages)");
                    return;
                case "SignalEvent" or "CallEvent" or "TimeEvent" or "ChangeEvent" or "AnyReceiveEvent":
                    if (id != null) _events[id] = Detach(c);
                    return;
                case "Extension" or "ProfileApplication" or "PackageImport" or "ElementImport" or "Diagram":
                    return;
                case "Pseudostate": HandlePseudostate(c, owner, id); return;
                case "Lifeline":
                {
                    var ll = Create(ElementKind.Lifeline, c, owner, id);
                    if (ll != null && Ref(c, "represents") is { } rep) _represents.Add((ll, rep));
                    return;
                }
                case "CombinedFragment":
                {
                    var cf = Create(ElementKind.CombinedFragment, c, owner, id);
                    if (cf == null) return;
                    ModelRepository.SetFieldForLoad(cf, FieldKeys.Operator, Attr(c, "interactionOperator") ?? "alt");
                    var guards = c.Elements().Where(e => e.Name.LocalName == "operand")
                        .Select(o => ValueText(Child(o, "guard")))
                        .Where(g => !string.IsNullOrEmpty(g)).ToList();
                    if (guards.Count > 0) ModelRepository.SetFieldForLoad(cf, FieldKeys.Guard, string.Join(" | ", guards));
                    HandleChildren(c, cf, id);
                    return;
                }
                case "InteractionOperand":
                    HandleChildren(c, owner, ownerXmiId);
                    return;
            }

            var ek = MapKind(type, id);
            if (ek == null)
            {
                _report.Skip(SkipLabel(type));
                return;
            }
            var el = Create(ek.Value, c, owner, id);
            if (el == null) return;
            FillSpecific(el, c, type);
            HandleChildren(c, el, id);
        }

        private static string SkipLabel(string type) => type switch
        {
            "InstanceSpecification" => "Instance specifications",
            "OpaqueBehavior" or "FunctionBehavior" => "Opaque / function behaviors",
            "StructuredActivityNode" or "ConditionalNode" or "LoopNode" or "SequenceNode" or "ExpansionRegion" => "Structured activity nodes (and their contents)",
            "InterruptibleActivityRegion" => "Interruptible regions",
            "InteractionUse" or "Gate" or "StateInvariant" or "Continuation" or "GeneralOrdering" => "Interaction uses, gates, invariants",
            "ConnectionPointReference" => "Connection point references",
            "Reception" => "Receptions",
            "Node" or "Device" or "ExecutionEnvironment" or "Artifact" or "Deployment" or "CommunicationPath" => "Deployment elements",
            "Constraint" => "Constraints",
            "Slot" => "Slots",
            "ExtensionPoint" => "Extension points",
            "TimeObservation" or "DurationObservation" or "TimeConstraint" or "DurationConstraint" or "Interval" or "TimeInterval" or "DurationInterval" => "Timing constraints / observations",
            _ => $"uml:{type}",
        };

        private ElementKind? MapKind(string type, string? id)
        {
            bool Has(params string[] names) => id != null && _stereo.TryGetValue(id, out var l) && l.Any(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            return type switch
            {
                "Class" => Has(RequirementStereotypes) ? ElementKind.Requirement
                         : Has("InterfaceBlock") ? ElementKind.InterfaceBlock
                         : Has("ConstraintBlock") ? ElementKind.ConstraintBlock
                         : Has("Block") ? ElementKind.Block
                         : ElementKind.Class,
                "Signal" => ElementKind.Class,
                "Interface" => ElementKind.Interface,
                "DataType" or "PrimitiveType" => Has("ValueType") ? ElementKind.ValueType : ElementKind.DataType,
                "Enumeration" => ElementKind.Enumeration,
                "Component" => ElementKind.Component,
                "Actor" => ElementKind.Actor,
                "UseCase" => ElementKind.UseCase,
                "Activity" => Has("TestCase") ? ElementKind.TestCase : ElementKind.Activity,
                "StateMachine" or "ProtocolStateMachine" => ElementKind.StateMachine,
                "Interaction" => Has("TestCase") ? ElementKind.TestCase : ElementKind.Interaction,
                "Stereotype" => ElementKind.Stereotype,
                "ActivityPartition" => ElementKind.Partition,
                "InputPin" or "ValuePin" or "ActionInputPin" => ElementKind.InputPin,
                "OutputPin" => ElementKind.OutputPin,
                "CentralBufferNode" or "DataStoreNode" or "ActivityParameterNode" => ElementKind.ObjectNode,
                "InitialNode" => ElementKind.InitialNode,
                "ActivityFinalNode" or "FlowFinalNode" => ElementKind.ActivityFinalNode,
                "DecisionNode" or "MergeNode" => ElementKind.DecisionNode,
                "ForkNode" or "JoinNode" => ElementKind.ForkNode,
                "State" => ElementKind.State,
                "FinalState" => ElementKind.FinalState,
                _ when type.EndsWith("Action", StringComparison.Ordinal) => ElementKind.Action,
                _ => null,
            };
        }

        internal static readonly string[] RequirementStereotypes =
        [
            "Requirement", "AbstractRequirement", "extendedRequirement", "functionalRequirement", "performanceRequirement",
            "interfaceRequirement", "physicalRequirement", "designConstraint", "businessRequirement", "usabilityRequirement",
        ];

        /// <summary>Kind-specific fields read from the element itself.</summary>
        private void FillSpecific(Element el, XElement c, string type)
        {
            switch (el.Kind)
            {
                case ElementKind.Action:
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Body, Child(c, "body")?.Value ?? Attr(c, "body"));
                    if (Ref(c, "behavior") is { } b) _fieldRefs.Add((el, FieldKeys.Behavior, b, false));
                    if (type == "SendSignalAction" && Ref(c, "signal") is { } sig) _fieldRefs.Add((el, ExtFieldKeys.SendSignal, sig, true));
                    if (type is "AcceptEventAction" or "AcceptCallAction")
                        foreach (var t in c.Elements().Where(e => e.Name.LocalName == "trigger"))
                            if (Ref(t, "event") is { } ev) { _fieldRefs.Add((el, ExtFieldKeys.AcceptEvent, ev, true)); break; }
                    if (type is not ("OpaqueAction" or "CallBehaviorAction")) ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + "actionKind", type);
                    break;
                case ElementKind.Partition:
                    if (Ref(c, "represents") is { } rep) _fieldRefs.Add((el, FieldKeys.Represents, rep, false));
                    break;
                case ElementKind.State:
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Entry, BehaviorText(Child(c, "entry")));
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Exit, BehaviorText(Child(c, "exit")));
                    ModelRepository.SetFieldForLoad(el, FieldKeys.DoActivity, BehaviorText(Child(c, "doActivity")));
                    break;
                case ElementKind.InputPin or ElementKind.OutputPin or ElementKind.ObjectNode:
                    _typeRefs.Add((el, TypeHolder(c)));
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Multiplicity, Multiplicity(c, null));
                    if (type == "ValuePin") ModelRepository.SetFieldForLoad(el, FieldKeys.DefaultValue, ValueText(Child(c, "value")));
                    break;
                case ElementKind.Class when type == "Signal":
                    AppendStereotype(el, "signal");
                    break;
                case ElementKind.ConstraintBlock:
                    break;
            }
        }

        private static string? BehaviorText(XElement? b)
        {
            if (b == null) return null;
            var body = Child(b, "body")?.Value ?? Attr(b, "body");
            return string.IsNullOrEmpty(body) ? Attr(b, "name") ?? XmiType(b) : body;
        }

        // ------------------------------------------------------------------ element creation

        private Element? Create(ElementKind kind, XElement x, Element owner, string? id)
        {
            var parent = OwnerFor(owner, kind);
            if (parent == null)
            {
                _report.Warn($"Could not place {Metamodel.DisplayName(kind)} '{Attr(x, "name")}' under {Metamodel.DisplayName(owner.Kind)} '{owner.Name}'");
                _report.Skip("Elements with no valid owner");
                return null;
            }
            var useId = id;
            if (id != null && (_map.ContainsKey(id) || _model.Find(id) != null))
            {
                _report.Warn($"Duplicate id {id}: {Metamodel.DisplayName(kind)} '{Attr(x, "name")}' got a new id");
                useId = null;
            }
            var el = new Element(kind, useId);
            ModelRepository.SetFieldForLoad(el, FieldKeys.Name, Attr(x, "name"));
            if (Attr(x, "visibility") is { } vis && vis != "public" && (Metamodel.IsClassifier(kind) || kind is ElementKind.Property or ElementKind.Port or ElementKind.Operation))
                ModelRepository.SetFieldForLoad(el, FieldKeys.Visibility, vis);
            if (Attr(x, "isAbstract") == "true") ModelRepository.SetFieldForLoad(el, FieldKeys.IsAbstract, "true");
            _model.AttachForLoad(parent, el);
            if (id != null) _map.TryAdd(id, el);
            if (id != null) ApplyStereotypes(el, id);
            if (kind is not (ElementKind.Property or ElementKind.Port)) _report.Count(CountLabel(el));
            return el;
        }

        private static string CountLabel(Element e) => e.Kind switch
        {
            ElementKind.Package => "Package",
            _ => Metamodel.DisplayName(e.Kind),
        };

        private Element? OwnerFor(Element owner, ElementKind kind)
        {
            if (Metamodel.CanOwn(owner.Kind, kind)) return owner;
            if (owner.Kind == ElementKind.TestCase && (Metamodel.IsActivityNode(kind) || kind == ElementKind.Partition))
            {
                // A test case activity: its nodes live in a nested activity (Meridian test cases own activities).
                var inner = owner.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Activity);
                if (inner == null)
                {
                    inner = new Element(ElementKind.Activity, owner.Id + "_activity");
                    ModelRepository.SetFieldForLoad(inner, FieldKeys.Name, owner.Name);
                    _model.AttachForLoad(owner, inner);
                }
                return inner;
            }
            var d = _model.DefaultOwnerFor(owner, kind);
            return Metamodel.CanOwn(d.Kind, kind) ? d : null;
        }

        private static void AppendStereotype(Element el, string name)
        {
            var list = el.Stereotypes.ToList();
            if (list.Contains(name, StringComparer.Ordinal)) return;
            list.Add(name);
            ModelRepository.SetFieldForLoad(el, FieldKeys.Stereotypes, string.Join(", ", list));
        }

        // Profiles MagicDraw applies for its own bookkeeping.
        private static bool IsToolProfile(string profile) =>
            profile.StartsWith("MagicDraw_Profile", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("DSL_Customization", StringComparison.OrdinalIgnoreCase)
            || profile.StartsWith("Validation_Profile", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("MD_Customization", StringComparison.OrdinalIgnoreCase)
            || profile.StartsWith("UML_Standard_Profile__MagicDraw", StringComparison.OrdinalIgnoreCase);

        // SysML stereotypes Meridian expresses through element kinds and fields, or that carry no user information.
        private static readonly HashSet<string> ConsumedStereotypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Block", "InterfaceBlock", "ConstraintBlock", "ValueType", "TestCase", "ProxyPort", "FullPort", "FlowPort", "BindingConnector",
            "Satisfy", "Verify", "Refine", "Trace", "DeriveReqt", "Copy", "Allocate", "ItemFlow", "PartProperty", "ReferenceProperty",
            "ValueProperty", "ConstraintProperty", "ParticipantProperty", "ConstraintParameter", "ClassifierBehaviorProperty",
            "AllocateActivityPartition", "NestedConnectorEnd", "ElementPropertyPath", "DirectedRelationshipPropertyPath", "DirectedFeature",
            "SharedProperty", "BoundReference", "EndPathMultiplicity", "Requirement", "AbstractRequirement", "auxiliaryResource", "DiagramInfo",
        };

        private void ApplyStereotypes(Element el, string id)
        {
            if (!_stereo.TryGetValue(id, out var apps)) return;
            foreach (var app in apps)
            {
                var name = app.Name;
                // Requirement Id / Text (SysML 1.x: "Id"/"Text"; some exports use lowercase).
                if (el.Kind == ElementKind.Requirement && RequirementStereotypes.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    ModelRepository.SetFieldForLoad(el, FieldKeys.RequirementId, TagValue(app.X, "Id") ?? TagValue(app.X, "id"));
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Text, PlainText(TagValue(app.X, "Text") ?? TagValue(app.X, "text")));
                    if (!name.Equals("Requirement", StringComparison.OrdinalIgnoreCase)) AppendStereotype(el, name);
                    CopyTags(el, app.X, "Id", "id", "Text", "text");
                    continue;
                }
                if (el.Kind == ElementKind.Port)
                {
                    if (name.Equals("ProxyPort", StringComparison.OrdinalIgnoreCase)) ModelRepository.SetFieldForLoad(el, FieldKeys.PortKind, "proxy");
                    else if (name.Equals("FullPort", StringComparison.OrdinalIgnoreCase)) ModelRepository.SetFieldForLoad(el, FieldKeys.PortKind, "full");
                    else if (name.Equals("FlowPort", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendStereotype(el, "flowPort");
                        ModelRepository.SetFieldForLoad(el, FieldKeys.Direction, TagValue(app.X, "direction") ?? "inout");
                    }
                }
                if (name.Equals("FlowProperty", StringComparison.OrdinalIgnoreCase))
                {
                    AppendStereotype(el, "flowProperty");
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Direction, TagValue(app.X, "direction") ?? "inout");
                    continue;
                }
                if (ConsumedStereotypes.Contains(name) || IsToolProfile(app.Profile)) continue;
                AppendStereotype(el, name);
                CopyTags(el, app.X);
            }
        }

        private static string? TagValue(XElement app, string tag)
        {
            if (app.Attribute(tag)?.Value is { } a) return a;
            var children = app.Elements().Where(e => e.Name.LocalName == tag).Select(e => IdRef(e) is { } r && !e.HasElements && e.Value.Length == 0 ? r : e.Value).ToList();
            return children.Count > 0 ? string.Join(", ", children) : null;
        }

        private static void CopyTags(Element el, XElement app, params string[] except)
        {
            foreach (var a in app.Attributes())
            {
                if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) continue;
                var n = a.Name.LocalName;
                if (n.StartsWith("base_", StringComparison.Ordinal) || except.Contains(n)) continue;
                ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + n, PlainText(a.Value));
            }
            foreach (var g in app.Elements().GroupBy(e => e.Name.LocalName))
            {
                if (g.Key.StartsWith("base_", StringComparison.Ordinal) || g.Key == "Extension" || except.Contains(g.Key)) continue;
                var values = g.Select(e => e.HasElements ? null : e.Value.Length > 0 ? e.Value : IdRef(e)).OfType<string>().ToList();
                if (values.Count > 0) ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + g.Key, PlainText(string.Join(", ", values)));
            }
        }

        private bool HasStereo(string? id, string name) =>
            id != null && _stereo.TryGetValue(id, out var l) && l.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ features

        private static string? Multiplicity(XElement x, string? fallback)
        {
            var lo = Child(x, "lowerValue");
            var hi = Child(x, "upperValue");
            if (lo == null && hi == null) return fallback;
            var l = lo == null ? "1" : Attr(lo, "value") ?? "0";
            var h = hi == null ? "1" : Attr(hi, "value") ?? "0";
            return XmiExchange.FormatMultiplicity(l, h);
        }

        private void RecordProperty(XElement x, string? ownerId)
        {
            if (XmiId(x) is not { } id) return;
            var typeId = Ref(x, "type");
            _props[id] = new PropInfo(Attr(x, "name"), typeId, Attr(x, "aggregation"), ownerId, Multiplicity(x, "1"));
            if (Attr(x, "name") is { } n) _names.TryAdd(id, n);
        }

        private void HandleProperty(XElement c, Element owner, string? ownerXmiId)
        {
            var id = XmiId(c);
            RecordProperty(c, ownerXmiId);
            if (owner.Kind == ElementKind.Stereotype)
            {
                if ((Attr(c, "name") ?? "").StartsWith("base_", StringComparison.Ordinal)) return;
                var td = Create(ElementKind.TagDefinition, c, owner, id);
                if (td != null) _typeRefs.Add((td, TypeHolder(c)));
                return;
            }
            if (!Metamodel.CanOwn(owner.Kind, ElementKind.Property))
            {
                // Interaction / collaboration attributes are the parts lifelines represent; others have no home.
                if (owner.Kind is not (ElementKind.Interaction or ElementKind.Package or ElementKind.Model or ElementKind.TestCase))
                    _report.Skip($"Properties owned by {Metamodel.DisplayName(owner.Kind)}");
                return;
            }
            var el = Create(ElementKind.Property, c, owner, id);
            if (el == null) return;
            ModelRepository.SetFieldForLoad(el, FieldKeys.Multiplicity, Multiplicity(c, "1"));
            var dv = Child(c, "defaultValue");
            ModelRepository.SetFieldForLoad(el, FieldKeys.DefaultValue, dv == null ? null : Attr(dv, "instance") is { } inst && _names.TryGetValue(inst, out var instName) ? instName : ValueText(dv));
            ModelRepository.SetFieldForLoad(el, AggregationField, Attr(c, "aggregation") ?? "none");
            if (Attr(c, "isReadOnly") == "true") ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + "readOnly", "true");
            _typeRefs.Add((el, TypeHolder(c)));
            _properties.Add(el);
            foreach (var cm in c.Elements().Where(e => e.Name.LocalName == "ownedComment")) HandleComment(cm, el, id);
            CollectDiagramsIn(c, el, id);
        }

        private void HandlePort(XElement c, Element owner)
        {
            var id = XmiId(c);
            RecordProperty(c, XmiId(c.Parent ?? c));
            var el = Create(ElementKind.Port, c, owner, id);
            if (el == null) return;
            ModelRepository.SetFieldForLoad(el, FieldKeys.Multiplicity, Multiplicity(c, "1"));
            if (Attr(c, "isConjugated") == "true") ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + "conjugated", "true");
            if (Attr(c, "isBehavior") == "true") ModelRepository.SetFieldForLoad(el, FieldKeys.TagPrefix + "behavior", "true");
            _typeRefs.Add((el, TypeHolder(c)));
            foreach (var cm in c.Elements().Where(e => e.Name.LocalName == "ownedComment")) HandleComment(cm, el, id);
            _report.Count(el.Get(FieldKeys.PortKind) switch
            {
                "proxy" => "Proxy port",
                "full" => "Full port",
                _ => el.Stereotypes.Contains("flowPort") ? "Flow port" : "Port",
            });
        }

        private void HandleOperation(XElement c, Element owner)
        {
            var el = Create(ElementKind.Operation, c, owner, XmiId(c));
            if (el == null) return;
            _operations.Add((el, Detach(c)));
            foreach (var cm in c.Elements().Where(e => e.Name.LocalName == "ownedComment")) HandleComment(cm, el, XmiId(c));
        }

        private void HandleConstraint(XElement c, Element owner)
        {
            var spec = ValueText(Child(c, "specification"));
            if (owner.Kind == ElementKind.ConstraintBlock)
            {
                var old = owner.Get(FieldKeys.Body);
                ModelRepository.SetFieldForLoad(owner, FieldKeys.Body, string.IsNullOrEmpty(old) ? spec : old + "\n" + spec);
                _report.Count("Constraint (on constraint block)");
                return;
            }
            if (string.IsNullOrEmpty(spec)) { _report.Skip("Constraints"); return; }
            var cm = new Element(ElementKind.Comment, XmiId(c) is { } id && !_map.ContainsKey(id) ? id : null);
            ModelRepository.SetFieldForLoad(cm, FieldKeys.Body, $"{{{(Attr(c, "name") is { } n ? n + ": " : "")}{spec}}}");
            ModelRepository.SetFieldForLoad(cm, FieldKeys.Stereotypes, "constraint");
            _model.AttachForLoad(owner, cm);
            if (XmiId(c) is { } cid) _map.TryAdd(cid, cm);
            _report.Count("Constraint (as comment)");
        }

        private void HandleComment(XElement c, Element owner, string? ownerXmiId)
        {
            var body = PlainText(Attr(c, "body") ?? Child(c, "body")?.Value) ?? "";
            var annotated = Refs(c, "annotatedElement");
            var id = XmiId(c);
            // MagicDraw stores documentation as a comment owned by, and annotating, the element.
            if (ownerXmiId != null && annotated.Contains(ownerXmiId) && owner.Kind != ElementKind.Comment)
            {
                var old = owner.Documentation;
                ModelRepository.SetFieldForLoad(owner, FieldKeys.Documentation, string.IsNullOrEmpty(old) ? body : old + "\n\n" + body);
                _report.Count("Documentation");
                return;
            }
            var cm = new Element(ElementKind.Comment, id != null && !_map.ContainsKey(id) && _model.Find(id) == null ? id : null);
            ModelRepository.SetFieldForLoad(cm, FieldKeys.Body, body);
            if (annotated.Count > 0) ModelRepository.SetFieldForLoad(cm, FieldKeys.TagPrefix + "annotates", string.Join(" ", annotated));
            var parent = Metamodel.CanOwn(owner.Kind, ElementKind.Comment) ? owner : _model.Root;
            _model.AttachForLoad(parent, cm);
            if (id != null) { _map.TryAdd(id, cm); ApplyStereotypes(cm, id); }
            _report.Count("Comment");
        }

        private void HandleConnector(XElement c, Element owner, string? id)
        {
            var ends = c.Elements().Where(e => e.Name.LocalName == "end").ToList();
            if (ends.Count != 2) { _report.Skip("Connectors without two ends"); return; }
            var binding = HasStereo(id, "BindingConnector");
            var label = binding ? "Binding connector" : "Connector";
            AddRel(binding ? ElementKind.Binding : ElementKind.Connector, id, Ref(ends[0], "role"), Ref(ends[1], "role"), owner, c, label);
        }

        private void HandleDependency(XElement c, Element owner, string? id, string type)
        {
            var kind = HasStereo(id, "Satisfy") ? ElementKind.Satisfy
                : HasStereo(id, "Verify") ? ElementKind.Verify
                : HasStereo(id, "DeriveReqt") ? ElementKind.DeriveReqt
                : HasStereo(id, "Refine") || type == "Refine" ? ElementKind.Refine
                : HasStereo(id, "Copy") ? ElementKind.Copy
                : HasStereo(id, "Allocate") ? ElementKind.Allocate
                : HasStereo(id, "Trace") || type == "Trace" ? ElementKind.Trace
                : type == "Realization" ? ElementKind.Realization
                : ElementKind.Dependency;
            var clients = Refs(c, "client");
            var suppliers = Refs(c, "supplier");
            if (clients.Count > 1 || suppliers.Count > 1)
                _report.Warn($"{Metamodel.DisplayName(kind)} {id} has several clients/suppliers; only the first pair was imported");
            AddRel(kind, id, clients.FirstOrDefault(), suppliers.FirstOrDefault(), owner, c, Metamodel.DisplayName(kind));
        }

        private void HandleTransition(XElement c, Element owner, string? id) =>
            AddRel(ElementKind.Transition, id, Ref(c, "source"), Ref(c, "target"), owner, c, "Transition");

        private void HandlePseudostate(XElement c, Element owner, string? id)
        {
            var kind = Attr(c, "kind") ?? "initial";
            switch (kind)
            {
                case "initial":
                    Create(ElementKind.InitialState, c, owner, id);
                    return;
                case "terminate":
                    Create(ElementKind.FinalState, c, owner, id);
                    return;
                default:
                {
                    // choice / junction / fork / join / history / entry & exit points: kept as states marked with their kind.
                    var el = Create(ElementKind.State, c, owner, id);
                    if (el == null) return;
                    AppendStereotype(el, kind);
                    if (string.IsNullOrEmpty(el.Name)) ModelRepository.SetFieldForLoad(el, FieldKeys.Name, kind);
                    WarnOnce("pseudo:" + kind, $"'{kind}' pseudostates have no Meridian equivalent; imported as states stereotyped «{kind}».");
                    return;
                }
            }
        }

        /// <summary>A parent-less copy, so that keeping it for later resolution does not keep the whole surrounding subtree alive.</summary>
        private static XElement Detach(XElement x) => x.Parent == null ? x : new XElement(x);

        /// <summary>Just the typing information of a typed element (type attribute / child).</summary>
        private static XElement TypeHolder(XElement x) => new(x.Name, x.Attribute("type"), Child(x, "type"));

        private void AddRel(ElementKind kind, string? id, string? src, string? tgt, Element owner, XElement x, string label)
        {
            _rels.Add(new PendingRel(kind, id, src, tgt, owner, Detach(x), label));
            if (id != null && Attr(x, "name") is { } n) _names.TryAdd(id, n);
        }
    }
}
