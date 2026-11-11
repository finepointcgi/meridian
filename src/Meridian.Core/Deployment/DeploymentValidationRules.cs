using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.Deployment;

/// <summary>
/// Deployment checks: DEP-001 (warning) an artifact deployed to no node, itself or through the artifact it is nested in;
/// DEP-002 (info) a communication path between nodes neither of which hosts anything.
/// </summary>
public sealed class DeploymentValidationRules : IValidationRule
{
    public string Id => "DEP";
    public string Description => "Deployment: artifacts deployed nowhere, communication paths between empty nodes";

    public IEnumerable<ValidationIssue> Check(ModelRepository m)
    {
        foreach (var a in m.OfKind(ElementKind.Artifact).Where(a => !DeploymentModel.IsDeployed(m, a)))
            yield return new(Severity.Warning, "DEP-001", $"Artifact '{a.Name}' is not deployed to any node", a);
        foreach (var p in m.OfKind(ElementKind.CommunicationPath).OfType<Relationship>())
            if (!DeploymentModel.HostsAnything(m, p.Source) && !DeploymentModel.HostsAnything(m, p.Target))
                yield return new(Severity.Info, "DEP-002", $"Communication path between '{p.Source.Name}' and '{p.Target.Name}' connects nodes with nothing deployed on them", p);
    }
}
