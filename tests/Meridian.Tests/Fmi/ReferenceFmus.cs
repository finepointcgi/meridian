using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Meridian.Serialization.Fmi;

namespace Meridian.Tests.Fmi;

/// <summary>
/// The Modelica Association's Reference FMUs (BouncingBall, Dahlquist, Feedthrough; FMI 2.0 and 3.0 Co-Simulation),
/// vendored under Fmi/reference with prebuilt binaries (see Fmi/reference/README.md). <see cref="Get"/> zips one into an
/// .fmu. When there is no prebuilt binary for this platform it is compiled from the vendored sources with the system C
/// compiler; without one the test is skipped.
/// </summary>
public static class ReferenceFmus
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fmi", "reference");

    private static readonly Dictionary<string, string?> Cache = new();
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "meridian-reference-fmus", Guid.NewGuid().ToString("N"));

    /// <summary>Path of the .fmu for <paramref name="model"/> in FMI <paramref name="version"/> ("2.0" / "3.0"); skips the test when none can be had.</summary>
    public static string Get(string model, string version)
    {
        string? path;
        lock (Cache)
        {
            var key = model + "/" + version;
            if (!Cache.TryGetValue(key, out path)) Cache[key] = path = Make(model, version);
        }
        Skip.If(path == null, $"No {model} FMI {version} binary for this platform ({string.Join(" / ", FmuArchive.PlatformFolders())}) and no C compiler to build one");
        return path!;
    }

    /// <summary>The reference result shipped with the model (time plus one column per output).</summary>
    public static (List<double> Time, Dictionary<string, List<double>> Columns) ReferenceResult(string model)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "src", model, model + "_out.csv"));
        var head = lines[0].Split(',');
        var time = new List<double>();
        var cols = head.Skip(1).ToDictionary(h => h, _ => new List<double>());
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var cells = line.Split(',');
            time.Add(double.Parse(cells[0], CultureInfo.InvariantCulture));
            for (var i = 1; i < head.Length; i++)
                cols[head[i]].Add(double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN);
        }
        return (time, cols);
    }

    private static string FmiPlatform(string version)
    {
        var folders = FmuArchive.PlatformFolders();
        // FMI 2.0 names Intel Macs / Linux "darwin64" / "linux64"; the FMI 3.0 style names cover the rest.
        if (version.StartsWith('2') && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            return OperatingSystem.IsMacOS() ? "darwin64" : OperatingSystem.IsLinux() ? "linux64" : "win64";
        return folders[0];
    }

    private static string? Make(string model, string version)
    {
        var source = Path.Combine(Root, "fmus", version, model);
        Directory.CreateDirectory(Temp);
        var hasBinary = FmuArchive.PlatformFolders().Any(f => File.Exists(Path.Combine(source, "binaries", f, model + FmuArchive.LibraryExtension)));
        string dir;
        if (hasBinary) dir = source;
        else
        {
            var cc = TestFmus.Compiler();
            if (cc == null) return null;
            dir = Path.Combine(Temp, "build", version, model);
            var bin = Path.Combine(dir, "binaries", FmiPlatform(version));
            Directory.CreateDirectory(bin);
            File.Copy(Path.Combine(Root, "src", model, $"FMI{version[0]}.xml"), Path.Combine(dir, "modelDescription.xml"), overwrite: true);
            var src = Path.Combine(Root, "src");
            var psi = new ProcessStartInfo(cc) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-shared", "-fPIC", "-O2", "-fvisibility=hidden", $"-DFMI_VERSION={version[0]}", "-DDISABLE_PREFIX",
                         "-I", Path.Combine(src, "include"), "-I", Path.Combine(src, model),
                         Path.Combine(src, "src", $"fmi{version[0]}Functions.c"), Path.Combine(src, "src", "common.c"), Path.Combine(src, "src", "cosimulation.c"),
                         Path.Combine(src, model, "model.c"), "-o", Path.Combine(bin, model + FmuArchive.LibraryExtension) })
                psi.ArgumentList.Add(a);
            if (!OperatingSystem.IsMacOS()) psi.ArgumentList.Add("-lm");
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"Building the {model} reference FMU failed:\n{err}");
        }
        var fmu = Path.Combine(Temp, $"{model}-fmi{version[0]}.fmu");
        if (File.Exists(fmu)) File.Delete(fmu);
        ZipFile.CreateFromDirectory(dir, fmu);
        return fmu;
    }
}
