namespace Meridian.Core.CodeSync;

/// <summary>
/// What code sync leaves out of the model by default because it does not describe the system: test code (test projects,
/// test folders and files, classes with test methods) and nested types that are not public (implementation detail of
/// their outer type). See <see cref="CodeSyncOptions.IncludeTests"/> and <see cref="CodeSyncOptions.IncludeNonPublic"/>.
/// </summary>
public static class CodeSyncNoise
{
    /// <summary>Attributes / annotations / decorators that mark a test method or test class.</summary>
    public static readonly IReadOnlySet<string> TestAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestMethod", "TestCase", "TestCaseSource", "TestFixture", "TestClass", "SkippableFact",
        "SkippableTheory", "ParameterizedTest", "RepeatedTest", "DataTestMethod",
    };

    /// <summary>Folders that hold tests ("tests/", "test/", "__tests__/", "spec/", Java's "src/test/").</summary>
    private static readonly string[] TestFolders = ["/tests/", "/test/", "/__tests__/", "/spec/", "/specs/", "/testing/"];

    /// <summary>
    /// A test file by its path: under a test folder, or named like a test ("Foo.test.ts", "foo.spec.js", "test_foo.py",
    /// "foo_test.py", "FooTests.cs", "FooTest.java"). Project folders named "*.Tests" / "*.Test" count as test folders.
    /// </summary>
    public static bool IsTestPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = "/" + path.Replace('\\', '/').TrimStart('/');
        var lower = p.ToLowerInvariant();
        if (TestFolders.Any(f => lower.Contains(f, StringComparison.Ordinal))) return true;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in segments.Take(segments.Length - 1))
            if (folder.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || folder.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
                || folder.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) || folder.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
                return true;
        var file = segments.Length > 0 ? segments[^1] : "";
        var name = file.Contains('.') ? file[..file.IndexOf('.')] : file;
        var lowerFile = file.ToLowerInvariant();
        return lowerFile.Contains(".test.") || lowerFile.Contains(".spec.")
               || lowerFile.StartsWith("test_", StringComparison.Ordinal) || name.EndsWith("_test", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Tests", StringComparison.Ordinal) || name.EndsWith("Test", StringComparison.Ordinal) && name.Length > 4;
    }

    /// <summary>A class with test methods (or marked as a test class).</summary>
    public static bool HasTests(CodeType t) =>
        t.Attributes.Any(TestAttributes.Contains) || t.Members.Any(m => m.Kind == CodeMemberKind.Method && m.Attributes.Any(TestAttributes.Contains));

    /// <summary>
    /// Whether a type is test code: in a test project, in a test file or folder, or a class with test methods.
    /// </summary>
    public static bool IsTest(CodeType t, IReadOnlyDictionary<string, CodeModule> modules) =>
        t.Module is { } m && modules.TryGetValue(m, out var module) && module.IsTest || IsTestPath(t.File) || HasTests(t);
}
