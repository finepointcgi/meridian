using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Deployment;

/// <summary>
/// UML deployment: nodes, devices and execution environments (which nest: a device holding an execution environment holding
/// another), artifacts deployed to them by «deploy» (<see cref="ElementKind.Deployment"/>, artifact → node), artifacts that
/// «manifest» model elements (<see cref="ElementKind.Manifestation"/>, artifact → block / component / package / code element),
/// and communication paths between nodes (with an optional <see cref="DeploymentFields.Protocol"/>). A database or schema
/// (a package with a db role) can be deployed to a node like an artifact.
/// <para>A deployment is always a <see cref="ElementKind.Deployment"/> relationship; drawing an artifact inside a node's shape is
/// only notation for it (the diagram hides the «deploy» line of an artifact drawn nested in its node).</para>
/// </summary>
public static class DeploymentModel
{
    /// <summary>Computed table key: the node (device or node) an artifact is deployed to, through any execution environment.</summary>
    public const string NodeKey = "deploymentNode";

    /// <summary>Computed table key: the execution environment an artifact is deployed to, if any.</summary>
    public const string EnvironmentKey = "deploymentEnvironment";

    public static bool IsNode(ElementKind k) => k is ElementKind.Node or ElementKind.Device or ElementKind.ExecutionEnvironment;

    public static bool IsArtifact(ElementKind k) => k is ElementKind.Artifact or ElementKind.DeploymentSpecification;

    /// <summary>Kinds that can be the source of a «deploy»: artifacts, deployment specifications and packages (databases, schemas).</summary>
    public static bool IsDeployable(ElementKind k) => IsArtifact(k) || k == ElementKind.Package;

    /// <summary>A database or schema package (data model) that can be deployed to a node.</summary>
    public static bool IsDatabase(Element e) => e.Kind == ElementKind.Package && (DataModeling.DbModel.IsDatabase(e) || DataModeling.DbModel.IsSchema(e));

    /// <summary>Elements "Deploy to…" applies to: artifacts, deployment specifications, databases and schemas.</summary>
    public static bool CanDeploy(Element e) => e is not Relationship && (IsArtifact(e.Kind) || IsDatabase(e));

    /// <summary>End check for the deployment relationships (null when valid).</summary>
    public static string? CheckEnds(ElementKind rel, ElementKind source, ElementKind target) => rel switch
    {
        ElementKind.Deployment => IsDeployable(source) && IsNode(target) ? null
            : "Deploy must go from an artifact (or a database / schema) to a node, device or execution environment",
        ElementKind.Manifestation => IsArtifact(source) && (target == ElementKind.Package || Metamodel.IsClassifier(target) && !IsNode(target) && !IsArtifact(target)) ? null
            : "Manifest must go from an artifact to the block, component, package or code element it manifests",
        ElementKind.CommunicationPath => IsNode(source) && IsNode(target) ? null : "Communication paths connect nodes, devices and execution environments",
        _ => null,
    };

    // ------------------------------------------------------------------ queries

    /// <summary>Nodes (devices, execution environments) the element is deployed to directly.</summary>
    public static IEnumerable<Element> DeployedTo(ModelRepository m, Element e) =>
        m.Outgoing(e, ElementKind.Deployment).Select(r => r.Target);

    /// <summary>Artifacts (databases …) deployed directly to the node.</summary>
    public static IEnumerable<Element> DeployedOn(ModelRepository m, Element node) =>
        m.Incoming(node, ElementKind.Deployment).Select(r => r.Source);

    /// <summary>Is the artifact deployed somewhere, itself or through an artifact it is nested in?</summary>
    public static bool IsDeployed(ModelRepository m, Element artifact) =>
        artifact.AncestorsAndSelf().TakeWhile(a => IsArtifact(a.Kind) || ReferenceEquals(a, artifact)).Any(a => DeployedTo(m, a).Any());

    /// <summary>Does anything run on the node, directly or on a node or environment nested in it?</summary>
    public static bool HostsAnything(ModelRepository m, Element node) =>
        node.Descendants().Prepend(node).Where(n => IsNode(n.Kind)).Any(n => DeployedOn(m, n).Any());

    /// <summary>The physical node of a deployment target: the nearest enclosing node or device (an environment's host).</summary>
    public static Element? HostOf(Element target) =>
        target.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Node or ElementKind.Device)
        ?? (IsNode(target.Kind) ? target : null);

    /// <summary>The execution environment of a deployment target (itself when it is one), or null on a bare node.</summary>
    public static Element? EnvironmentOf(Element target) =>
        target.AncestorsAndSelf().TakeWhile(a => IsNode(a.Kind)).FirstOrDefault(a => a.Kind == ElementKind.ExecutionEnvironment);

    /// <summary>Elements the artifact «manifest»s.</summary>
    public static IEnumerable<Element> Manifests(ModelRepository m, Element artifact) =>
        m.Outgoing(artifact, ElementKind.Manifestation).Select(r => r.Target);

    /// <summary>The table cell for <see cref="NodeKey"/> / <see cref="EnvironmentKey"/>.</summary>
    public static string Value(ModelRepository m, Element e, string key)
    {
        var targets = DeployedTo(m, e).ToList();
        var names = key == NodeKey
            ? targets.Select(HostOf).OfType<Element>()
            : targets.Select(EnvironmentOf).OfType<Element>();
        return string.Join(", ", names.Distinct().Select(n => n.DisplayName));
    }

    /// <summary>Candidate targets for "Deploy to…": every node, device and execution environment, by qualified name.</summary>
    public static IEnumerable<Element> Targets(ModelRepository m) =>
        m.AllElements.Where(e => IsNode(e.Kind) && e is not Relationship).OrderBy(e => e.QualifiedName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deploys each element to <paramref name="node"/> (skipping those already deployed there, and those that can't be deployed)
    /// in one undo step. Returns the deployments created.
    /// </summary>
    public static IReadOnlyList<Relationship> DeployTo(ModelRepository m, IEnumerable<Element> elements, Element node)
    {
        if (!IsNode(node.Kind)) throw new InvalidOperationException($"{node.DisplayName} is not a node, device or execution environment");
        var list = elements.Where(e => !ReferenceEquals(e, node) && CheckEnds(ElementKind.Deployment, e.Kind, node.Kind) == null)
            .Distinct().ToList();
        var created = new List<Relationship>();
        if (list.Count == 0) return created;
        using (m.History.Transaction($"Deploy {(list.Count == 1 ? list[0].DisplayName : $"{list.Count} elements")} to {node.DisplayName}"))
            foreach (var e in list)
                if (!DeployedTo(m, e).Contains(node)) created.Add(m.Connect(ElementKind.Deployment, e, node));
        return created;
    }

    /// <summary>A generic table of every artifact with the node and execution environment it is deployed to.</summary>
    public static TableDefinition DeploymentTable() => new()
    {
        Query = "kind:Artifact,DeploymentSpecification",
        Columns =
        [
            new TableViewColumn("name", "Artifact"), new TableViewColumn(NodeKey, "Node"), new TableViewColumn(EnvironmentKey, "Environment"),
            new TableViewColumn("field:" + DeploymentFields.FileName, "File"), new TableViewColumn("manifests", "Manifests"),
        ],
        SortKey = "name",
    };

    // ------------------------------------------------------------------ notation

    /// <summary>
    /// Is a «deploy» shown by nesting on this diagram? True when the deployed element's shape sits (at any depth) inside the
    /// shape of the node it is deployed to; the line is then left out.
    /// </summary>
    public static bool ShownByNesting(Diagram d, DiagramEdge e)
    {
        if (e.Relationship.Kind != ElementKind.Deployment) return false;
        for (var p = d.ParentOf(e.Source); p != null; p = d.ParentOf(p))
            if (ReferenceEquals(p, e.Target)) return true;
        return false;
    }

    /// <summary>Compartments of a node or artifact shape: value properties (cores, RAM …), file name and manifested elements.</summary>
    public static void AddCompartments(ModelRepository m, Element e, List<Compartment> comps)
    {
        if (!IsNode(e.Kind) && !IsArtifact(e.Kind)) return;
        var props = e.OwnedElements.Where(c => c.Kind == ElementKind.Property).Select(p => Notation.FeatureLine(m, p)).ToList();
        if (e.Get(DeploymentFields.FileName) is { Length: > 0 } file) props.Insert(0, $"file = {file}");
        if (props.Count > 0) comps.Add(new Compartment(null, props));
        if (IsArtifact(e.Kind))
        {
            var manifests = Manifests(m, e).Select(x => Metamodel.Keyword(x.Kind) is { } kw ? $"«{kw}» {x.Name}" : x.Name).ToList();
            if (manifests.Count > 0) comps.Add(new Compartment("manifests", manifests));
        }
    }
}
