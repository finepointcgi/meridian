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

    public static readonly IReadOnlyList<string> All = [Symbol, File, Line, Repo, Commit];
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
    IReadOnlyList<CodeFile> Files, bool IsFullSnapshot);

public enum CodeSyncStatus { Pending, NoChanges, AwaitingReview, Accepted, Failed }

/// <summary>Result of running a sync, used to post a commit status back to the git host.</summary>
public sealed record CodeSyncOutcome(CodeSyncStatus Status, string Summary, string? ReviewUrl, Guid? RunId);

/// <summary>Runs a sync (implemented in Meridian.Web by the review-branch service). Hosts call this after fetching files.</summary>
public interface ICodeSyncRunner
{
    Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default);
}
