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
    /// <summary>Methods: the body as a small control-flow tree (CodeBodyContracts.cs); null when the reader did not read
    /// bodies or the method has none.</summary>
    public CodeBody? Body { get; init; }
    /// <summary>Attribute / annotation / decorator names on the member, without "Attribute" ("HttpGet", "Fact").</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
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
    /// <summary>Attribute / annotation / decorator names on the type, without "Attribute" ("ApiController").</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];
    /// <summary>Base types and interfaces that are not scanned types, by simple name ("BackgroundService", "IHostedService").</summary>
    public IReadOnlyList<string> ExternalBases { get; init; } = [];
    /// <summary>Declared accessibility: "public" (the default, also for readers that do not record it), "internal",
    /// "protected" or "private". Nested types that are not public are implementation detail and skipped unless
    /// <see cref="CodeSyncOptions.IncludeNonPublic"/> is set.</summary>
    public string Visibility { get; init; } = "public";
    /// <summary>The <see cref="CodeModule.Name"/> of the project the type's file belongs to, or null when the reader found no
    /// project files.</summary>
    public string? Module { get; init; }

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
public sealed record CodeModel(string Language, IReadOnlyList<CodeType> Types, IReadOnlyList<string> Warnings)
{
    /// <summary>The projects found next to the code (C# .csproj files), sorted by name; empty when there are none.
    /// Code sync makes one subsystem per non-test project.</summary>
    public IReadOnlyList<CodeModule> Modules { get; init; } = [];
}

/// <summary>
/// A project (a C# .csproj): a unit the code is built and shipped in, which code sync shows as a subsystem.
/// <see cref="Path"/> is the repository-relative folder of the project file ("" for the root), forward slashes.
/// </summary>
public sealed record CodeModule(string Name, string Path)
{
    /// <summary>The project's &lt;Description&gt;, when it has one.</summary>
    public string? Description { get; init; }
    /// <summary>A test project (test SDK, xUnit, NUnit or MSTest reference, or &lt;IsTestProject&gt;).</summary>
    public bool IsTest { get; init; }
    /// <summary>Names of the projects it references (&lt;ProjectReference&gt;) that were read too.</summary>
    public IReadOnlyList<string> References { get; init; } = [];
    /// <summary>The repository-relative path of the project file.</summary>
    public string File { get; init; } = "";

    /// <summary>The module of the deepest project folder that holds <paramref name="file"/>, or null.</summary>
    public static CodeModule? Containing(IReadOnlyList<CodeModule> modules, string file)
    {
        CodeModule? best = null;
        foreach (var m in modules)
            if ((m.Path.Length == 0 || file.StartsWith(m.Path + "/", StringComparison.Ordinal)) && (best is null || m.Path.Length > best.Path.Length)) best = m;
        return best;
    }
}

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
    /// <summary>
    /// Also keep a model Dependency for every scanned type used in a method body, parameter or local (not just members
    /// and inheritance). Off by default: on real code they outnumber the blocks several times over and say little on their
    /// own. The package and subsystem dependencies are derived from that usage either way.
    /// </summary>
    public bool IncludeUsageDependencies { get; init; }
    /// <summary>Members that are not public, and nested types that are not public, are skipped unless this is set.</summary>
    public bool IncludeNonPublic { get; init; }
    /// <summary>
    /// Also sync test code: types in test projects (<see cref="CodeModule.IsTest"/>), in test folders and files
    /// (<see cref="CodeSyncNoise.IsTestPath"/>) and classes with test methods. Off by default: tests explain how the code is
    /// checked, not how the system is built.
    /// </summary>
    public bool IncludeTests { get; init; }
    /// <summary>
    /// Name packages, blocks, interfaces, enumerations and subsystems in words ("Code sync runner" for CodeSyncRunner) instead
    /// of the code identifier (<see cref="CodeNames"/>). Members keep their code names. A name written by the optional AI
    /// pass (<see cref="CodeModelMapper.LabelKey"/>) wins over both. On by default.
    /// </summary>
    public bool ReadableNames { get; init; } = true;
    /// <summary>
    /// A subsystem per project (<see cref="CodeModel.Modules"/>), with the system architecture: a system block whose parts
    /// are the subsystems, connected where one uses another, through ports typed by the interfaces between them. On by
    /// default; nothing is made when the code has fewer than two non-test projects.
    /// </summary>
    public bool Subsystems { get; init; } = true;
    /// <summary>When a type disappears from code: true removes its element; false only marks it (field "code.removed").</summary>
    public bool RemoveDeleted { get; init; } = true;
    /// <summary>Submodules whose code is in the snapshot: elements from files under one get <see cref="CodeFieldKeys.Submodule"/>.</summary>
    public IReadOnlyList<CodeSubmodulePin> Submodules { get; init; } = [];
    /// <summary>Deployment artifacts that «manifest» the synced code, so it can be deployed to nodes. Off by default.</summary>
    public CodeArtifactMode Artifacts { get; init; } = CodeArtifactMode.None;
    /// <summary>Which methods get activity and sequence diagrams from their bodies (CodeBehaviourMapper).</summary>
    public CodeBehaviourMode Behaviour { get; init; } = CodeBehaviourMode.OnDemand;
    /// <summary>Limits of the behaviour diagrams (depth, messages, caps).</summary>
    public CodeBehaviourLimits BehaviourLimits { get; init; } = new();
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
    /// <summary>
    /// Ids of the diagrams earlier syncs of this repository created (from their run records). With the layout stamp code
    /// sync now puts on the diagrams it makes, they tell its "internal structure" diagrams from ones people made with
    /// Auto-diagram, so only code sync's own trivial ones are cleaned up (<see cref="CodeSyncDiagrams.RemoveTrivialIbds"/>).
    /// </summary>
    public IReadOnlySet<string> SyncedDiagrams { get; init; } = new HashSet<string>();
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

    /// <summary>Diagrams made by earlier syncs that no longer meet the rules and that nobody touched, removed by this sync
    /// ("internal structure" diagrams of blocks with fewer than two parts, behaviours "Entry points" no longer picks).
    /// Their ids are in <see cref="Removed"/> like any other removal.</summary>
    public int TrivialDiagramsRemoved { get; init; }
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

    /// <summary>The Meridian user the sync is done for: who pressed "Sync now", else who connected the repository
    /// (webhooks). Null when unknown. A first import goes into main only when this user may edit main.</summary>
    public Guid? TriggeredBy { get; init; }
}

public enum CodeSyncStatus { Pending, NoChanges, AwaitingReview, Accepted, Failed }

/// <summary>Result of running a sync, used to post a commit status back to the git host.</summary>
public sealed record CodeSyncOutcome(CodeSyncStatus Status, string Summary, string? ReviewUrl, Guid? RunId);

/// <summary>Runs a sync (implemented in Meridian.Web by the review-branch service). Hosts call this after fetching files.</summary>
public interface ICodeSyncRunner
{
    Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default);
}
