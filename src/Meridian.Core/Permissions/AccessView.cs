using Meridian.Core.Model;

namespace Meridian.Core.Permissions;

/// <summary>What a user may do with a package and everything it contains (Teamwork Cloud style). Ordered: a higher level includes the lower ones.</summary>
public enum PackageAccess
{
    /// <summary>The package is listed, but its contents are hidden.</summary>
    None = 0,
    /// <summary>The package and its contents can be seen, not changed.</summary>
    Read = 1,
    /// <summary>The contents can be changed.</summary>
    Edit = 2,
    /// <summary>Edit, and change who may do what in the package.</summary>
    Manage = 3,
}

/// <summary>Who a rule is for: one user, or every member of a team.</summary>
public enum PrincipalKind { User, Team }

/// <summary>One explicit permission: <paramref name="Level"/> for <paramref name="PrincipalId"/> on the package <paramref name="ElementId"/> and below.</summary>
public sealed record PermissionRule(string ElementId, PrincipalKind PrincipalKind, Guid PrincipalId, PackageAccess Level);

/// <summary>
/// One user's effective package permissions in one project, resolved from the explicit rules:
/// <list type="bullet">
/// <item>Permissions are inherited down the containment tree; the nearest explicit rule wins.</item>
/// <item>At one package a rule for the user beats rules for their teams; among team rules the most permissive wins.</item>
/// <item>Without a rule an element has <see cref="Default"/> (the project role: editors edit, viewers read). No rule raises
/// a level above <see cref="Cap"/> (viewers never edit). Project owners always manage everything (<see cref="Full"/>).</item>
/// <item>"None" on a package hides what it contains (the package itself stays listed). Everything below a hidden package
/// stays hidden, whatever rules sit further down; a relationship with a hidden end is hidden too.</item>
/// </list>
/// Cheap to query: every question walks the element's ancestors once.
/// </summary>
public sealed class AccessView
{
    private readonly Dictionary<string, PackageAccess> _explicit;

    private AccessView(PackageAccess @default, PackageAccess cap, Dictionary<string, PackageAccess> rules, bool owner)
    {
        Default = (PackageAccess)Math.Min((int)@default, (int)cap);
        Cap = cap;
        _explicit = rules;
        IsOwner = owner;
        HidesAnything = rules.Values.Any(v => v == PackageAccess.None);
    }

    /// <summary>Everything, editable and manageable (project owners, the system, local mode).</summary>
    public static readonly AccessView Full = new(PackageAccess.Manage, PackageAccess.Manage, new(StringComparer.Ordinal), owner: true);

    /// <summary>Read-only everywhere, no rules (viewers of a project without package permissions).</summary>
    public static readonly AccessView ReadAll = new(PackageAccess.Read, PackageAccess.Read, new(StringComparer.Ordinal), owner: false);

    /// <summary>Resolves the rules that apply to <paramref name="userId"/> (directly or through <paramref name="teamIds"/>).</summary>
    /// <param name="default">The level where no rule applies (the project role: <see cref="PackageAccess.Edit"/> for editors, <see cref="PackageAccess.Read"/> for viewers).</param>
    /// <param name="cap">The highest level a rule may grant (<see cref="PackageAccess.Read"/> for viewers).</param>
    /// <param name="owner">Project owners always have <see cref="PackageAccess.Manage"/>; rules don't apply to them.</param>
    public static AccessView For(IEnumerable<PermissionRule> rules, Guid userId, IReadOnlyCollection<Guid> teamIds,
                                 PackageAccess @default, PackageAccess cap, bool owner = false)
    {
        if (owner) return Full;
        var mine = new Dictionary<string, PackageAccess>(StringComparer.Ordinal);
        var teams = new Dictionary<string, PackageAccess>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (r.PrincipalKind == PrincipalKind.User && r.PrincipalId == userId) mine[r.ElementId] = r.Level;
            else if (r.PrincipalKind == PrincipalKind.Team && teamIds.Contains(r.PrincipalId))
                teams[r.ElementId] = teams.TryGetValue(r.ElementId, out var t) && t > r.Level ? t : r.Level;
        }
        foreach (var (id, level) in teams) mine.TryAdd(id, level);   // a user rule beats the user's team rules
        var capped = mine.ToDictionary(kv => kv.Key, kv => (PackageAccess)Math.Min((int)kv.Value, (int)cap), StringComparer.Ordinal);
        return new AccessView(@default, cap, capped, owner: false);
    }

    public PackageAccess Default { get; }
    public PackageAccess Cap { get; }
    public bool IsOwner { get; }

    /// <summary>Some rule applies to this user (otherwise every element simply has <see cref="Default"/>).</summary>
    public bool HasRules => _explicit.Count > 0;

    /// <summary>Some package's contents are hidden from this user: they get a redacted copy of the model.</summary>
    public bool HidesAnything { get; }

    /// <summary>The explicit (capped) rule on exactly this element, if any.</summary>
    public PackageAccess? ExplicitAt(string elementId) => _explicit.TryGetValue(elementId, out var l) ? l : null;

    /// <summary>The effective level of an element: the nearest explicit rule on it or above it, else <see cref="Default"/>.</summary>
    public PackageAccess Level(Element e)
    {
        if (IsOwner) return PackageAccess.Manage;
        if (_explicit.Count == 0) return Default;
        for (var a = e; a != null; a = a.Owner)
            if (_explicit.TryGetValue(a.Id, out var l)) return l;
        return Default;
    }

    /// <summary>The level a new child of <paramref name="owner"/> with id <paramref name="childId"/> would have.</summary>
    public PackageAccess LevelUnder(Element owner, string childId) =>
        IsOwner ? PackageAccess.Manage : _explicit.TryGetValue(childId, out var l) ? l : Level(owner);

    /// <summary>True when the element must not be shown to this user (it is inside a package whose contents are hidden, or a relationship with a hidden end).</summary>
    public bool IsHidden(Element e) => HidesAnything && Hidden(e, 0);

    private bool Hidden(Element e, int depth)
    {
        for (var a = e.Owner; a != null; a = a.Owner)
            if (_explicit.TryGetValue(a.Id, out var l) && l == PackageAccess.None) return true;
        if (depth < 4)
            for (var a = e; a != null; a = a.Owner)
                if (a is Relationship r && (Hidden(r.Source, depth + 1) || Hidden(r.Target, depth + 1))) return true;
        return false;
    }

    /// <summary>True when the contents of <paramref name="container"/> can be seen.</summary>
    public bool CanSeeContents(Element container) => !IsHidden(container) && Level(container) >= PackageAccess.Read;

    /// <summary>True when this user may change the element (for a relationship: and both its ends).</summary>
    public bool CanEdit(Element e)
    {
        if (IsOwner) return true;
        if (!EditableEnd(e)) return false;
        return e is not Relationship r || (EditableEnd(r.Source) && EditableEnd(r.Target));
    }

    private bool EditableEnd(Element e) => Level(e) >= PackageAccess.Edit && !IsHidden(e);

    /// <summary>True when this user may change the permissions of the package (and below).</summary>
    public bool CanManage(Element e) => IsOwner || (Level(e) == PackageAccess.Manage && !IsHidden(e));

    /// <summary>
    /// The package to name when explaining why <paramref name="e"/> can't be changed or seen: the outermost package whose contents
    /// are hidden (it is itself visible), else the nearest element with an explicit rule, else null.
    /// </summary>
    public Element? ProtectingPackage(Element e)
    {
        Element? hiddenBy = null;
        for (var a = e.Owner; a != null; a = a.Owner)
            if (_explicit.TryGetValue(a.Id, out var l) && l == PackageAccess.None) hiddenBy = a;
        if (hiddenBy != null) return hiddenBy;
        for (var a = e; a != null; a = a.Owner)
            if (_explicit.ContainsKey(a.Id)) return a;
        return null;
    }

    /// <summary>A one-line reason this user may not change <paramref name="e"/> (never names hidden elements).</summary>
    public string DeniedMessage(Element e)
    {
        var target = e is Relationship r && CanEditSelfOnly(e) ? (!EditableEnd(r.Source) ? r.Source : r.Target) : e;
        var pkg = ProtectingPackage(target);
        if (IsHidden(target))
            return pkg != null ? $"You don't have access to the contents of “{pkg.DisplayName}” — your change was not applied."
                               : "You don't have access to that element — your change was not applied.";
        if (pkg != null) return $"“{pkg.DisplayName}” is read-only for you (package permissions) — your change was not applied.";
        return Default < PackageAccess.Edit ? "This project is view-only for you — your change was not applied."
                                            : $"“{target.DisplayName}” is read-only for you — your change was not applied.";
    }

    private bool CanEditSelfOnly(Element e) => EditableEnd(e);

    /// <summary>A one-line reason this user may not add to or remove from <paramref name="container"/>.</summary>
    public string ContentsDeniedMessage(Element container)
    {
        if (IsHidden(container)) return DeniedMessage(container);
        Element? pkg = null;
        for (var a = container; a != null; a = a.Owner)
            if (_explicit.TryGetValue(a.Id, out var l) && l == PackageAccess.None) pkg = a;
        if (pkg != null) return $"You don't have access to the contents of “{pkg.DisplayName}” — your change was not applied.";
        for (var a = container; a != null && pkg == null; a = a.Owner)
            if (_explicit.ContainsKey(a.Id)) pkg = a;
        if (pkg != null) return $"“{pkg.DisplayName}” is read-only for you (package permissions) — your change was not applied.";
        return Default < PackageAccess.Edit ? "This project is view-only for you — your change was not applied."
                                            : $"“{container.DisplayName}” is read-only for you — your change was not applied.";
    }
}
