using System.Diagnostics;
using System.IO.Compression;
using Meridian.Serialization.Fmi;

namespace Meridian.Tests.Fmi;

/// <summary>
/// Builds the test FMUs (Fmi/native/test_fmu2.c, test_fmu3.c) with the system C compiler once per test run and packages
/// them as .fmu archives. <see cref="Get"/> returns null when no C compiler is available (the tests then skip).
/// </summary>
public static class TestFmus
{
    public const string LagGuid = "{8c4e810f-3df3-4a00-8276-176fa3c9f000}";
    public const string GainGuid = "{8c4e810f-3df3-4a00-8276-176fa3c9f001}";
    public const string Lag3Token = "{6a5f1d2e-0b7c-4c55-9a51-1b2f0d3e3000}";

    public sealed record Set(string Lag, string Gain, string Crash, string Hang, string Lag3, string GainSeconds);

    private static readonly Lazy<Set?> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Set? Get() => Built.Value;

    public static string SkipReason => "No C compiler (cc / clang / gcc, or $CC) found: the FMU runtime tests need one to build their test FMUs";

    public static string? Compiler()
    {
        if (OperatingSystem.IsWindows()) return null;   // the test FMUs are built with a Unix-style cc
        var fromEnv = Environment.GetEnvironmentVariable("CC");
        var names = new[] { fromEnv, "cc", "clang", "gcc" }.Where(n => !string.IsNullOrWhiteSpace(n));
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var n in names)
        {
            if (Path.IsPathRooted(n!) && File.Exists(n)) return n;
            foreach (var dir in path)
                if (File.Exists(Path.Combine(dir, n!))) return Path.Combine(dir, n!);
        }
        return null;
    }

    private static string Native => Path.Combine(AppContext.BaseDirectory, "Fmi", "native");

    private static Set? Build()
    {
        var cc = Compiler();
        if (cc == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "meridian-test-fmus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fmi2Folder = OperatingSystem.IsMacOS() ? "darwin64" : FmuArchive.PlatformFolders()[^1];
        var fmi3Folder = FmuArchive.PlatformFolders()[0];

        string Fmu2(string model, string id, string md) => Package(dir, id, md, Compile(cc, dir, "test_fmu2.c", "fmi2", id, $"-DMODEL_{model}"), fmi2Folder);
        return new Set(
            Lag: Fmu2("LAG", "lag", LagDescription("lag", "m")),
            Gain: Fmu2("GAIN", "gain", GainDescription("gain", "m")),
            Crash: Fmu2("CRASH", "crash", GainDescription("crash", "m")),
            Hang: Fmu2("HANG", "hang", GainDescription("hang", "m")),
            Lag3: Package(dir, "lag3", Lag3Description, Compile(cc, dir, "test_fmu3.c", "fmi3", "lag3"), fmi3Folder),
            GainSeconds: Package(dir, "gain_s", GainDescription("gain_s", "s"), Compile(cc, dir, "test_fmu2.c", "fmi2", "gain_s", "-DMODEL_GAIN"), fmi2Folder));
    }

    private static string Compile(string cc, string dir, string source, string headers, string id, params string[] defines)
    {
        var lib = Path.Combine(dir, id + FmuArchive.LibraryExtension);
        var psi = new ProcessStartInfo(cc) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-shared", "-fPIC", "-O1", "-I", Path.Combine(Native, headers) }.Concat(defines).Concat(["-o", lib, Path.Combine(Native, source)]))
            psi.ArgumentList.Add(a);
        if (!OperatingSystem.IsMacOS()) psi.ArgumentList.Add("-lm");
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"Compiling {source} ({id}) failed:\n{err}");
        return lib;
    }

    private static string Package(string dir, string id, string modelDescription, string lib, string platformFolder)
    {
        var fmu = Path.Combine(dir, id + ".fmu");
        using var zip = ZipFile.Open(fmu, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modelDescription.xml").Open())) w.Write(modelDescription);
        zip.CreateEntryFromFile(lib, $"binaries/{platformFolder}/{id}{FmuArchive.LibraryExtension}");
        zip.CreateEntry("resources/");
        return fmu;
    }

    public static string LagDescription(string id, string unit) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <fmiModelDescription fmiVersion="2.0" modelName="Lag" guid="{LagGuid}" description="First-order lag" variableNamingConvention="flat" numberOfEventIndicators="0">
          <CoSimulation modelIdentifier="{id}" canHandleVariableCommunicationStepSize="true"/>
          <UnitDefinitions><Unit name="s"/><Unit name="{unit}"/></UnitDefinitions>
          <ModelVariables>
            <ScalarVariable name="T" valueReference="0" causality="parameter" variability="fixed" initial="exact"><Real start="1" unit="s"/></ScalarVariable>
            <ScalarVariable name="x0" valueReference="1" causality="parameter" variability="fixed" initial="exact"><Real start="0" unit="{unit}"/></ScalarVariable>
            <ScalarVariable name="u" valueReference="2" causality="input" variability="continuous"><Real start="0" unit="{unit}"/></ScalarVariable>
            <ScalarVariable name="y" valueReference="3" causality="output" variability="continuous" initial="calculated"><Real unit="{unit}"/></ScalarVariable>
          </ModelVariables>
          <ModelStructure><Outputs><Unknown index="4"/></Outputs><InitialUnknowns><Unknown index="4"/></InitialUnknowns></ModelStructure>
        </fmiModelDescription>
        """;

    public static string GainDescription(string id, string unit) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <fmiModelDescription fmiVersion="2.0" modelName="{(id == "gain" ? "Gain" : id)}" guid="{GainGuid}" variableNamingConvention="flat" numberOfEventIndicators="0">
          <CoSimulation modelIdentifier="{id}"/>
          <ModelVariables>
            <ScalarVariable name="k" valueReference="0" causality="parameter" variability="fixed" initial="exact"><Real start="1"/></ScalarVariable>
            <ScalarVariable name="u" valueReference="1" causality="input" variability="continuous"><Real start="0" unit="{unit}"/></ScalarVariable>
            <ScalarVariable name="y" valueReference="2" causality="output" variability="continuous" initial="calculated"><Real unit="{unit}"/></ScalarVariable>
          </ModelVariables>
          <ModelStructure><Outputs><Unknown index="3" dependencies="2"/></Outputs><InitialUnknowns><Unknown index="3"/></InitialUnknowns></ModelStructure>
        </fmiModelDescription>
        """;

    public static string Lag3Description => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <fmiModelDescription fmiVersion="3.0" modelName="Lag3" instantiationToken="{Lag3Token}">
          <CoSimulation modelIdentifier="lag3" canHandleVariableCommunicationStepSize="true"/>
          <UnitDefinitions><Unit name="s"/><Unit name="m"/></UnitDefinitions>
          <ModelVariables>
            <Float64 name="time" valueReference="0" causality="independent" variability="continuous"/>
            <Float64 name="T" valueReference="1" causality="parameter" variability="fixed" start="1" unit="s"/>
            <Float64 name="x0" valueReference="2" causality="parameter" variability="fixed" start="0" unit="m"/>
            <Float64 name="u" valueReference="3" causality="input" start="0" unit="m"/>
            <Float64 name="y" valueReference="4" causality="output" unit="m"/>
            <Float64 name="stopAt" valueReference="5" causality="parameter" variability="fixed" start="0" unit="s"/>
          </ModelVariables>
          <ModelStructure><Output valueReference="4"/><InitialUnknown valueReference="4"/></ModelStructure>
        </fmiModelDescription>
        """;
}
