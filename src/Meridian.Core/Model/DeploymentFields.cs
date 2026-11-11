namespace Meridian.Core.Model;

/// <summary>
/// Field keys of the deployment elements (UML deployment diagrams): kept apart from <see cref="FieldKeys"/> so the feature
/// stays self-contained. Node capacities (cores, RAM …) are ordinary value properties; «server», «container» … are stereotypes.
/// </summary>
public static class DeploymentFields
{
    /// <summary>Artifacts and deployment specifications: the file the artifact stands for ("orders-service.jar").</summary>
    public const string FileName = "fileName";

    /// <summary>Communication paths: the protocol spoken over the path ("HTTPS", "AMQP", "CAN").</summary>
    public const string Protocol = "protocol";

    /// <summary>Deployments: id of the deployment specification that parameterises this deployment.</summary>
    public const string Specification = "deploymentSpec";

    /// <summary>Deployment specifications: where the artifact is installed ("/opt/orders").</summary>
    public const string DeploymentLocation = "deploymentLocation";

    /// <summary>Deployment specifications: the execution environment it runs in ("JVM 21").</summary>
    public const string ExecutionLocation = "executionLocation";

    public static readonly IReadOnlyList<string> All = [FileName, Protocol, Specification, DeploymentLocation, ExecutionLocation];

    /// <summary>Inspector fields per kind (inserted before stereotypes / documentation).</summary>
    public static IEnumerable<string> EditableFields(ElementKind k)
    {
        if (k is ElementKind.Artifact or ElementKind.DeploymentSpecification) yield return FileName;
        if (k == ElementKind.DeploymentSpecification) { yield return DeploymentLocation; yield return ExecutionLocation; }
        if (k == ElementKind.CommunicationPath) yield return Protocol;
    }

    public static string? Label(string key) => key switch
    {
        FileName => "File name",
        Protocol => "Protocol",
        Specification => "Deployment specification",
        DeploymentLocation => "Deployment location",
        ExecutionLocation => "Execution location",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        FileName => "e.g. orders-service.jar, firmware.hex",
        Protocol => "e.g. HTTPS, gRPC, AMQP, CAN",
        DeploymentLocation => "Where the artifact is installed, e.g. /opt/orders",
        ExecutionLocation => "The environment it runs in, e.g. JVM 21",
        _ => null,
    };
}
