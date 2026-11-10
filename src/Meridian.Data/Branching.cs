namespace Meridian.Data;

/// <summary>A named line of development in a project. "main" is the project row itself and has no branch row.</summary>
public sealed record BranchInfo(Guid Id, Guid ProjectId, string Name, long? BaseVersionId, int? BaseVersionNumber, string CreatedBy,
                                DateTime CreatedAt, DateTime UpdatedAt, long Revision);

/// <summary>A branch's current head and its merge base (the last state it shares with the line it came from).</summary>
public sealed record BranchModels(Guid ProjectId, string Name, string HeadJson, string BaseJson, long? BaseVersionId, long Revision);

/// <summary>An immutable, named tag on a committed version (e.g. "PDR 1.0").</summary>
public sealed record BaselineInfo(long Id, string Name, string Description, long VersionId, int VersionNumber, string VersionMessage,
                                  string BranchName, string CreatedBy, DateTime CreatedAt);

/// <summary>Naming rules shared by every store.</summary>
public static class Branching
{
    public const string MainBranch = "main";

    public static string NormalizeBranchName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 60) throw new InvalidOperationException("Branch names are 1–60 characters.");
        if (string.Equals(name, MainBranch, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("“main” is the project's main line.");
        if (!name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '/'))
            throw new InvalidOperationException("Branch names may use letters, digits, spaces and - _ . /");
        return name;
    }

    public static string NormalizeBaselineName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 80) throw new InvalidOperationException("Baseline names are 1–80 characters.");
        return name;
    }
}
