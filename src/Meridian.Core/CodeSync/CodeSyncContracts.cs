namespace Meridian.Core.CodeSync;

// Shared contracts for "code sync": a push to a GitHub / GitLab repository updates a review branch of the model,
// the diagrams that show changed elements are refreshed, and the differences are highlighted for review.
//
//   Git host (Meridian.Web/CodeSync/Hosts)  ->  ICodeReader (C#, TypeScript, Java, Python)  ->  CodeModelMapper (model elements)
//        webhook + fetch + commit status         CodeFile[] -> CodeModel          applies to a branch's ModelRepository
//
// Owned pieces: hosts/webhooks (Meridian.Web/CodeSync/Hosts), reader + mapper (Meridian.Serialization/CodeSync and
// Meridian.Core/CodeSync/CodeModelMapper.cs), runner + review branch + highlights (Meridian.Web/CodeSync, UI).
// Change these types only together with every user of them.

/// <summary>One source file at a commit. <see cref="Path"/> is repository-relative with forward slashes.</summary>
public sealed record CodeFile(string Path, string Content);

public enum CodeTypeKind { Class, Struct, Record, Interface, Enum }

public enum CodeMemberKind { Field, Property, Method, EnumValue }

/// <summary>A member of a type. <see cref="TypeSymbol"/> is the fully qualified symbol of its type when that type is
/// declared in the scanned code (so it can become a part / reference), otherwise null; <see cref="TypeName"/> is the
/// display type ("int", "List&lt;Battery&gt;").</summary>
/// <remarks>For a method, <see cref="TypeName"/> / <see cref="TypeSymbol"/> describe the return type ("void" or "" for none)
/// and <see cref="Parameters"/> lists its parameters in order. Each overload is its own member.</remarks>
public sealed record CodeMember(string Name, CodeMemberKind Kind, string TypeName, string? TypeSymbol, bool IsCollection, string Visibility)
{
    /// <summary>Method parameters in declaration order; empty for other members.</summary>
    public IReadOnlyList<CodeParameter> Parameters { get; init; } = [];
    /// <summary>Declared abstract (an abstract method, or one without a body in an abstract class).</summary>
    public bool IsAbstract { get; init; }
    /// <summary>A static (class-level) member.</summary>
    public bool IsStatic { get; init; }
    /// <summary>The value may be absent (C# <c>T?</c> is also recognised from <see cref="TypeName"/>; TypeScript <c>x?: T</c>,
    /// <c>T | null</c>, Python <c>Optional[T]</c>, Java <c>Optional&lt;T&gt;</c>).</summary>
    public bool IsOptional { get; init; }
    /// <summary>Documentation comment text (summary / first paragraph), whitespace collapsed; null when there is none.</summary>
    public string? Doc { get; init; }
    /// <summary>1-based line of the member's name; 0 when unknown (the type's line is used).</summary>
    public int Line { get; init; }
}

public enum CodeParameterDirection { In, Out, InOut, Return }

/// <summary>A method parameter. <see cref="TypeName"/> is the type as written ("" when the code gives none, as in untyped
/// Python); <see cref="TypeSymbol"/> is the scanned type it holds (directly, optional or as a collection element), else
/// null.</summary>
public sealed record CodeParameter(
    string Name, string TypeName, string? TypeSymbol = null, bool IsCollection = false,
    CodeParameterDirection Direction = CodeParameterDirection.In);

/// <summary>A declared type. <see cref="Symbol"/> is the stable identity across syncs (fully qualified, e.g.
/// "Sat.Power.Battery"; generic arity as "`1"). <see cref="Dependencies"/> lists symbols of other scanned types it
/// uses in method bodies, parameters or locals (not counting members already listed or base types).</summary>
public sealed record CodeType(
    string Symbol, string Name, string Namespace, CodeTypeKind Kind,
    string File, int Line, string? Doc,
    IReadOnlyList<string> BaseTypes, IReadOnlyList<string> Interfaces,
    IReadOnlyList<CodeMember> Members, IReadOnlyList<string> Dependencies)
{
    /// <summary>An abstract class (interfaces are not marked).</summary>
    public bool IsAbstract { get; init; }
    /// <summary>A static class (C#) or a static nested class (Java).</summary>
    public bool IsStatic { get; init; }
    /// <summary>Where the type uses each scanned type it depends on (bases, interfaces, member types and
    /// <see cref="Dependencies"/>): one entry per (target, file, line), sorted by target symbol, file and line, at most
    /// <see cref="CodeDependencyUses.DefaultMaxPerTarget"/> (or the reader's cap) per target. Empty when the reader does
    /// not record uses (older stored code, other readers).</summary>
    public IReadOnlyList<CodeDependencyUse> DependencyLocations { get; init; } = [];
    /// <summary>Uses left out of <see cref="DependencyLocations"/> by the per-target cap, by target symbol (only the
    /// targets that were capped).</summary>
    public IReadOnlyDictionary<string, int> OmittedDependencyLocations { get; init; } = CodeDependencyUses.NoneOmitted;

    /// <summary>The recorded uses of <paramref name="target"/>, sorted by file and line.</summary>
    public IEnumerable<CodeDependencyUse> LocationsOf(string target) =>
        DependencyLocations.Where(u => string.Equals(u.Symbol, target, StringComparison.Ordinal));

    /// <summary>How many uses of <paramref name="target"/> there are, counting the ones the cap left out.</summary>
    public int LocationCountOf(string target) =>
        LocationsOf(target).Count() + (OmittedDependencyLocations.TryGetValue(target, out var n) ? n : 0);
}

/// <summary>How a type uses another one at a <see cref="CodeDependencyUse"/>. The numbers are stored: never renumber.</summary>
public enum CodeDependencyKind
{
    /// <summary>Any other mention: a local's type, a cast, <c>typeof</c>, a generic argument, a static member read.</summary>
    Use = 0,
    /// <summary>A static call (<c>Battery.Create()</c>).</summary>
    Call = 1,
    /// <summary>An instance creation (<c>new Battery()</c>, Python <c>Battery()</c>).</summary>
    New = 2,
    /// <summary>The type of a field or property.</summary>
    Field = 3,
    /// <summary>The type of a method parameter.</summary>
    Param = 4,
    /// <summary>A method's return type.</summary>
    Return = 5,
    /// <summary>A base class or an implemented interface.</summary>
    Inherit = 6,
    /// <summary>The import that brings the target into the file (recorded only when the type also uses the target).</summary>
    Import = 7,
}

/// <summary>One place where a type uses another scanned type: the target's symbol, the repository-relative file, the
/// 1-based line and how it is used.</summary>
public sealed record CodeDependencyUse(string Symbol, string File, int Line, CodeDependencyKind Kind)
{
    /// <summary>"call", "new", "field", "param", "return", "inherit", "import" or "use".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string KindName => CodeDependencyUses.Name(Kind);

    /// <summary>"src/Thermal/ThermalControl.cs:42".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Location => Line > 0 ? $"{File}:{Line}" : File;
}

/// <summary>Builds the capped, deterministic <see cref="CodeType.DependencyLocations"/> of a type.</summary>
public static class CodeDependencyUses
{
    /// <summary>Uses kept per (type, target): enough to point at every line in the usual case, bounded for very large code.</summary>
    public const int DefaultMaxPerTarget = 8;

    public static readonly IReadOnlyDictionary<string, int> NoneOmitted = new Dictionary<string, int>(StringComparer.Ordinal);

    public static string Name(CodeDependencyKind kind) => kind switch
    {
        CodeDependencyKind.Call => "call",
        CodeDependencyKind.New => "new",
        CodeDependencyKind.Field => "field",
        CodeDependencyKind.Param => "param",
        CodeDependencyKind.Return => "return",
        CodeDependencyKind.Inherit => "inherit",
        CodeDependencyKind.Import => "import",
        _ => "use",
    };

    /// <summary>When one line uses a target several ways, the most telling kind is kept.</summary>
    private static int Rank(CodeDependencyKind kind) => kind switch
    {
        CodeDependencyKind.Inherit => 0,
        CodeDependencyKind.New => 1,
        CodeDependencyKind.Call => 2,
        CodeDependencyKind.Field => 3,
        CodeDependencyKind.Param => 4,
        CodeDependencyKind.Return => 5,
        CodeDependencyKind.Import => 6,
        _ => 7,
    };

    /// <summary>
    /// One use per (target, file, line) (the most telling kind wins), sorted by target, file and line, at most
    /// <paramref name="maxPerTarget"/> per target (the first lines are kept), and the count of the rest per capped target.
    /// The result does not depend on the order of <paramref name="uses"/>.
    /// </summary>
    public static (IReadOnlyList<CodeDependencyUse> Uses, IReadOnlyDictionary<string, int> Omitted) Cap(
        IEnumerable<CodeDependencyUse> uses, int maxPerTarget = DefaultMaxPerTarget)
    {
        var best = new Dictionary<(string, string, int), CodeDependencyUse>();
        foreach (var u in uses)
        {
            if (string.IsNullOrEmpty(u.Symbol) || string.IsNullOrEmpty(u.File) || u.Line <= 0) continue;
            var key = (u.Symbol, u.File, u.Line);
            if (!best.TryGetValue(key, out var have) || Rank(u.Kind) < Rank(have.Kind)) best[key] = u;
        }
        if (best.Count == 0) return ([], NoneOmitted);
        var take = Math.Max(0, maxPerTarget);
        var kept = new List<CodeDependencyUse>();
        Dictionary<string, int>? omitted = null;
        foreach (var group in best.Values.GroupBy(u => u.Symbol, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sorted = group.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).ToList();
            kept.AddRange(sorted.Take(take));
            if (sorted.Count > take) (omitted ??= new Dictionary<string, int>(StringComparer.Ordinal))[group.Key] = sorted.Count - take;
        }
        return (kept, omitted ?? NoneOmitted);
    }
}

/// <summary>Language-neutral result of reading a set of files.</summary>
public sealed record CodeModel(string Language, IReadOnlyList<CodeType> Types, IReadOnlyList<string> Warnings);

public interface ICodeReader
{
    string Language { get; }
    bool Handles(string path);
    CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default);
}

/// <summary>Field keys stamped on elements created from code, so a re-sync updates rather than duplicates them and
/// the UI can link back to the source.</summary>
public static class CodeFieldKeys
{
    /// <summary>Stable code symbol ("Sat.Power.Battery", "Sat.Power.Battery.Capacity"); "ns:Sat.Power" for packages.</summary>
    public const string Symbol = "code.symbol";
    /// <summary>Repository-relative file path.</summary>
    public const string File = "code.file";
    public const string Line = "code.line";
    /// <summary>Id of the code-repo integration connection the element came from.</summary>
    public const string Repo = "code.repo";
    /// <summary>Commit sha of the sync that last created or changed the element.</summary>
    public const string Commit = "code.commit";
    /// <summary>"path@sha" of the git submodule the element's file is in (absent for the repository's own code).
    /// Kept current like <see cref="File"/>; a submodule bump alone does not count as a change of the element.</summary>
    public const string Submodule = "code.submodule";
    /// <summary>Prefix of the model-root field that pins a connection's submodules ("code.submodules:&lt;connection id&gt;");
    /// its value is <see cref="CodeSubmodulePins.Format"/>. A change of it is a submodule bump in "What changed".</summary>
    public const string SubmodulePinsPrefix = "code.submodules:";

    public static readonly IReadOnlyList<string> All = [Symbol, File, Line, Repo, Commit, Submodule];
}

/// <summary>Options for applying a <see cref="CodeModel"/> to a model.</summary>
public sealed record CodeSyncOptions
{
    /// <summary>Qualified name of the package that holds everything from this repository (created if missing).</summary>
    public string RootPackage { get; init; } = "Code";
    public string RepoId { get; init; } = "";
    public string Commit { get; init; } = "";
    /// <summary>Also emit dependencies from method-body usage (not just members and inheritance).</summary>
    public bool IncludeUsageDependencies { get; init; } = true;
    /// <summary>Members that are not public are skipped unless this is set.</summary>
    public bool IncludeNonPublic { get; init; }
    /// <summary>When a type disappears from code: true removes its element; false only marks it (field "code.removed").</summary>
    public bool RemoveDeleted { get; init; } = true;
    /// <summary>Submodules whose code is in the snapshot: elements from files under one get <see cref="CodeFieldKeys.Submodule"/>.</summary>
    public IReadOnlyList<CodeSubmodulePin> Submodules { get; init; } = [];
    /// <summary>Deployment artifacts that «manifest» the synced code, so it can be deployed to nodes. Off by default.</summary>
    public CodeArtifactMode Artifacts { get; init; } = CodeArtifactMode.None;
    /// <summary>
    /// Leave out the package of the namespace every type shares, so "Meridian ▸ Meridian ▸ Core" reads "Meridian ▸ Core":
    /// when all namespaces start with one segment and either it equals <see cref="RootPackage"/>'s name (case-insensitive)
    /// or no type is outside it, that segment's children go straight under the root package. Namespace symbols
    /// ("ns:Meridian.Core") do not change, so turning it on or off moves packages rather than replacing them. On by default.
    /// </summary>
    public bool CollapseRootNamespace { get; init; } = true;
    /// <summary>
    /// Keep «use» dependencies between the namespace packages the package overview shows (see
    /// <see cref="CodeModelMapper.OverviewPackages"/>), aggregated from the code dependencies between their types. They carry
    /// the symbol "pkgdep:A->B" (A, B namespaces) so re-syncs update them. On by default.
    /// </summary>
    public bool PackageDependencies { get; init; } = true;
}

/// <summary>Which deployment artifacts code sync keeps for a repository (each «manifest»s the package it stands for).</summary>
public enum CodeArtifactMode
{
    /// <summary>No artifacts (the default).</summary>
    None,
    /// <summary>One artifact for the whole repository, manifesting its root package.</summary>
    Repository,
    /// <summary>One artifact per top-level namespace package.</summary>
    TopLevelPackages,
}

/// <summary>What one sync did to the model: ids of elements added / changed / removed (relationships included).</summary>
public sealed record CodeSyncChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed, IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;
}

/// <summary>A sync request, from a webhook, the "Sync now" button or a test. Files are already fetched.</summary>
public sealed record CodeSyncRequest(
    Guid ProjectId, Guid ConnectionId, string RepoDisplay, string Branch, string Commit,
    string? CommitUrl, string? Author, string? Message,
    IReadOnlyList<CodeFile> Files, bool IsFullSnapshot)
{
    /// <summary>The repository's submodules at <see cref="Commit"/> (their code is in <see cref="Files"/> under their paths
    /// when fetched). Empty when the repository has none, or the caller does not look for them.</summary>
    public IReadOnlyList<CodeSubmodulePin> Submodules { get; init; } = [];
}

public enum CodeSyncStatus { Pending, NoChanges, AwaitingReview, Accepted, Failed }

/// <summary>Result of running a sync, used to post a commit status back to the git host.</summary>
public sealed record CodeSyncOutcome(CodeSyncStatus Status, string Summary, string? ReviewUrl, Guid? RunId);

/// <summary>Runs a sync (implemented in Meridian.Web by the review-branch service). Hosts call this after fetching files.</summary>
public interface ICodeSyncRunner
{
    Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default);
}
