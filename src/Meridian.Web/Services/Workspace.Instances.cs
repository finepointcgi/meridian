using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>Instance specifications (Instances/InstanceModel.cs): what the workbench asks the user while editing them.</summary>
public sealed partial class Workspace
{
    /// <summary>
    /// Asks which association a new link is an instance of when several fit (set by the workbench). The link is created
    /// untyped; the answer types it in a separate undo step. Null: no dialog possible (the inspector offers the choice).
    /// </summary>
    public Func<Relationship, IReadOnlyList<Relationship>, Task>? AskLinkAssociation { get; set; }
}
