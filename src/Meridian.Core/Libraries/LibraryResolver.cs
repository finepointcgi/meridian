namespace Meridian.Core.Libraries;

/// <summary>A usage to load: a package of a source project at a pin, shown below <see cref="MountId"/>.</summary>
/// <param name="ViaUsageId">For a library used by a library: the usage that brought it in.</param>
public sealed record LibraryRequest(string UsageId, Guid SourceProjectId, string RootPackageId, LibraryPinKind PinKind, string PinValue,
                                    string MountId, string? ViaUsageId = null);

/// <summary>A fetched library: its info and content, and the usages of its own source project (loaded transitively).</summary>
public sealed record LibraryFetch(LibraryInfo Info, string RootJson, IReadOnlyList<LibraryRequest> Uses);

/// <summary>Libraries to mount, in order, and what didn't load (cycles, conflicting pins, unavailable sources).</summary>
public sealed record LibraryResolution(IReadOnlyList<LibraryPayload> Payloads, IReadOnlyList<string> Warnings)
{
    public static readonly LibraryResolution Empty = new([], []);
}

/// <summary>
/// Resolves a project's usages transitively (libraries that use other libraries), breadth first so direct usages win:
/// <list type="bullet">
/// <item>Cycles: a library that (directly or through others) uses the project being loaded, or one of the libraries on its own
/// path, is skipped with a warning.</item>
/// <item>Diamonds: the same package of the same project reached twice at the same pin is loaded once.</item>
/// <item>Conflicts: the same package at two different pins is a warning; the first one reached wins.</item>
/// </list>
/// </summary>
public static class LibraryResolver
{
    public const int MaxDepth = 16;

    public static async Task<LibraryResolution> ResolveAsync(Guid usingProjectId, IEnumerable<LibraryRequest> direct,
                                                             Func<LibraryRequest, Task<LibraryFetch?>> fetch)
    {
        var payloads = new List<LibraryPayload>();
        var warnings = new List<string>();
        var loaded = new Dictionary<(Guid, string), LibraryInfo>();
        var queue = new Queue<(LibraryRequest Request, IReadOnlyList<Guid> Path, string? ViaName)>();
        foreach (var r in direct) queue.Enqueue((r, [usingProjectId], null));

        while (queue.Count > 0)
        {
            var (req, path, via) = queue.Dequeue();
            var key = (req.SourceProjectId, req.RootPackageId);
            if (path.Contains(req.SourceProjectId))
            {
                warnings.Add(req.SourceProjectId == usingProjectId
                    ? $"A used project{(via is null ? "" : $" (through {via})")} uses this project itself; that cycle was not loaded."
                    : $"Used projects form a cycle{(via is null ? "" : $" through {via}")}; the repeated library was not loaded.");
                continue;
            }
            if (loaded.TryGetValue(key, out var first))
            {
                if (first.PinKind != req.PinKind || first.PinValue != req.PinValue)
                    warnings.Add($"“{first.SourceProjectName}” is used at {first.PinLabel} and at {LibraryPins.Label(req.PinKind, req.PinValue)}" +
                                 $"{(via is null ? "" : $" (through {via})")}; {first.PinLabel} (the first) is used.");
                continue;   // a diamond at the same pin is simply loaded once
            }
            if (path.Count > MaxDepth) { warnings.Add($"Used projects nest more than {MaxDepth} levels deep; the rest were not loaded."); continue; }

            LibraryFetch? got;
            try { got = await fetch(req); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Text.Json.JsonException)
            {
                warnings.Add($"A used project could not be loaded: {ex.Message}");
                continue;
            }
            if (got is null)
            {
                warnings.Add($"A used project{(via is null ? "" : $" (through {via})")} is not available (it was deleted, its package or pin no longer exists, or you can't read it).");
                continue;
            }
            loaded[key] = got.Info;
            payloads.Add(new LibraryPayload(got.Info, got.RootJson));
            var nextPath = path.Append(req.SourceProjectId).ToList();
            foreach (var u in got.Uses)
                queue.Enqueue((u with { UsageId = $"{req.UsageId}/{u.UsageId}", ViaUsageId = req.UsageId }, nextPath, got.Info.SourceProjectName));
        }
        return new LibraryResolution(payloads, warnings);
    }
}
