using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Analysis;

namespace Meridian.Serialization.Fmi;

public sealed class FmuRuntimeOptions
{
    /// <summary>Path to Meridian.FmuHost (apphost or .dll); null finds it next to the running application.</summary>
    public string? HostPath { get; set; }
    /// <summary>Longest time one call into the FMU (instantiate, initialize, one step) may take before the host is killed.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Upper bound on the extracted size of an .fmu (zip bomb guard).</summary>
    public long MaxExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Turns on the FMU's own debug logging (fmi2Instantiate / fmi3Instantiate loggingOn).</summary>
    public bool FmuLogging { get; set; }
}

/// <summary>Unpacks .fmu archives and picks the binary for this platform.</summary>
public static class FmuArchive
{
    /// <summary>Extracts <paramref name="fmuPath"/> into a new temporary folder (zip-slip and size checked). The caller deletes it.</summary>
    public static string Extract(string fmuPath, long maxBytes = 2L * 1024 * 1024 * 1024)
    {
        var dir = Path.Combine(Path.GetTempPath(), "meridian-fmu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(fmuPath);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('\\', '/')));
                if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"The FMU contains an unsafe path: {e.FullName}");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                total += e.Length;
                if (total > maxBytes) throw new InvalidDataException($"The FMU expands to more than {maxBytes / (1024 * 1024)} MB");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Don't trust the header's size: stop at the remaining budget while inflating.
                using (var src = new SafeZip.LimitedStream(e.Open(), maxBytes - (total - e.Length), e.FullName))
                using (var dst = File.Create(target))
                    src.CopyTo(dst);
            }
            return dir;
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>binaries/ folder names to try on this machine, FMI 3.0 names and FMI 2.0 names.</summary>
    public static IReadOnlyList<string> PlatformFolders()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsMacOS()) return arm ? ["aarch64-darwin", "darwin64", "darwin-aarch64"] : ["x86_64-darwin", "darwin64"];
        if (OperatingSystem.IsWindows()) return arm ? ["aarch64-windows"] : ["x86_64-windows", "win64"];
        return arm ? ["aarch64-linux", "linux-aarch64"] : ["x86_64-linux", "linux64"];
    }

    public static bool IsCIdentifier(string? s) =>
        s is { Length: > 0 and <= 256 } && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string LibraryExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>The shared library implementing <paramref name="modelIdentifier"/> for this platform.</summary>
    public static string Binary(string dir, string modelIdentifier)
    {
        // The FMI standard makes modelIdentifier a C identifier (it prefixes the exported functions); anything else could
        // point outside binaries/ ("../../usr/lib/…").
        if (!IsCIdentifier(modelIdentifier))
            throw new CoSimException($"The FMU's modelIdentifier '{modelIdentifier}' is not a valid C identifier");
        foreach (var folder in PlatformFolders())
        {
            var p = Path.Combine(dir, "binaries", folder, modelIdentifier + LibraryExtension);
            if (File.Exists(p)) return p;
        }
        var have = Directory.Exists(Path.Combine(dir, "binaries"))
            ? string.Join(", ", Directory.GetDirectories(Path.Combine(dir, "binaries")).Select(Path.GetFileName)) : "none";
        throw new CoSimException($"The FMU has no binary for this platform ({string.Join(" / ", PlatformFolders())}, {modelIdentifier}{LibraryExtension}); it has: {have}");
    }
}

/// <summary>Finds the Meridian.FmuHost executable.</summary>
public static class FmuHostLocator
{
    public const string Name = "Meridian.FmuHost";

    /// <summary>Returns the program to start and the arguments that precede the host's own.</summary>
    public static (string FileName, string[] Args) Find(string? configured = null)
    {
        foreach (var candidate in new[] { configured, Environment.GetEnvironmentVariable("MERIDIAN_FMUHOST") })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!File.Exists(candidate)) throw new CoSimException($"FMU host not found at {candidate}");
            return candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? (Muxer(), [candidate]) : (candidate, []);
        }
        var dir = AppContext.BaseDirectory;
        var dll = Path.Combine(dir, Name + ".dll");
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? Name + ".exe" : Name);
        var selfContained = File.Exists(Path.Combine(dir, Name + ".runtimeconfig.json"))
                            && File.ReadAllText(Path.Combine(dir, Name + ".runtimeconfig.json")).Contains("includedFrameworks", StringComparison.Ordinal);
        if (File.Exists(apphost) && (selfContained || !File.Exists(dll))) return (apphost, []);
        if (File.Exists(dll)) return (Muxer(), [dll]);
        throw new CoSimException($"{Name} is not installed next to Meridian ({dir}); co-simulation needs it");
    }

    private static string Muxer()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hp && File.Exists(hp)) return hp;
        if (Environment.ProcessPath is { } pp && Path.GetFileName(pp).Equals(exe, StringComparison.OrdinalIgnoreCase)) return pp;
        // <root>/shared/Microsoft.NETCore.App/<version>/ → <root>/dotnet
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var candidate = Path.Combine(root, exe);
        return File.Exists(candidate) ? candidate : exe;
    }
}

/// <summary>
/// A running Meridian.FmuHost child process with one FMU loaded. Requests and responses are single JSON lines; an FMU that
/// crashes or hangs ends only this process (the host is killed after <see cref="FmuRuntimeOptions.CallTimeout"/>).
/// </summary>
public sealed class FmuHostProcess : IDisposable
{
    private const string Marker = "@@meridian-fmuhost@@ ";
    private readonly Process _process;
    private readonly BlockingCollection<string> _responses = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Queue<string> _stderr = new();
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _ct;
    private bool _disposed;

    public FmuHostProcess(string workingDirectory, FmuRuntimeOptions options, CancellationToken ct = default)
    {
        _timeout = options.CallTimeout;
        _ct = ct;
        var (file, args) = FmuHostLocator.Find(options.HostPath);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Defence in depth: the FMU does not inherit the server's environment (connection strings, API keys).
        psi.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "USERPROFILE", "TMPDIR", "TEMP", "TMP", "SystemRoot", "windir", "SystemDrive", "LANG", "LC_ALL",
                     "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" })
            if (Environment.GetEnvironmentVariable(key) is { } v) psi.Environment[key] = v;
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        try { _process = Process.Start(psi) ?? throw new CoSimException("Could not start the FMU host"); }
        catch (Win32Exception ex) { throw new CoSimException($"Could not start the FMU host ({file}): {ex.Message}", ex); }

        var stdout = _process.StandardOutput;
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = ReadBoundedLine(stdout, MaxLineChars)) != null)
                {
                    if (line.StartsWith(Marker, StringComparison.Ordinal)) _responses.Add(line[Marker.Length..]);
                    else if (line.Length > 0 && _log.Count < 5000) _log.Enqueue("[stdout] " + Clip(line));
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { _responses.CompleteAdding(); }
        }) { IsBackground = true, Name = "FmuHost stdout" }.Start();
        var stderr = _process.StandardError;
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = ReadBoundedLine(stderr, 4096)) != null)
                    lock (_stderr) { _stderr.Enqueue(line); if (_stderr.Count > 40) _stderr.Dequeue(); }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "FmuHost stderr" }.Start();
    }

    /// <summary>Longest line read from the host; FMU code can print anything, so longer lines are cut instead of buffered.</summary>
    public const int MaxLineChars = 4 * 1024 * 1024;

    /// <summary><see cref="TextReader.ReadLine"/> that keeps at most <paramref name="max"/> characters of a line (the rest is skipped).</summary>
    internal static string? ReadBoundedLine(TextReader reader, int max)
    {
        var sb = new StringBuilder();
        var any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            any = true;
            if (c == '\n') break;
            if (c == '\r') { if (reader.Peek() == '\n') reader.Read(); break; }
            if (sb.Length < max) sb.Append((char)c);
        }
        return any ? sb.ToString() : null;
    }

    private static string Clip(string s) => s.Length <= 2000 ? s : s[..2000] + "…";

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public IReadOnlyList<string> DrainLog()
    {
        var list = new List<string>();
        while (_log.TryDequeue(out var l)) list.Add(l);
        return list;
    }

    /// <summary>Sends one request and waits for its response. Throws <see cref="CoSimException"/> on FMU errors, crashes and timeouts.</summary>
    public JsonObject Call(JsonObject request, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _process.StandardInput.WriteLine(request.ToJsonString());
            _process.StandardInput.Flush();
        }
        catch (IOException) { throw new CoSimException(CrashMessage()); }

        string? line;
        try
        {
            if (!_responses.TryTake(out line, (int)Math.Min(int.MaxValue, (timeout ?? _timeout).TotalMilliseconds), _ct))
            {
                if (_responses.IsAddingCompleted || _process.HasExited) throw new CoSimException(CrashMessage());
                Kill();
                throw new CoSimException($"The FMU did not respond within {(timeout ?? _timeout).TotalSeconds:0.#} s to '{request["op"]}'; its host process was stopped");
            }
        }
        catch (OperationCanceledException) { Kill(); throw; }

        JsonObject resp;
        try { resp = JsonNode.Parse(line!)?.AsObject() ?? throw new CoSimException("Empty response from the FMU host"); }
        catch (JsonException ex) { throw new CoSimException($"Garbled response from the FMU host: {ex.Message}"); }
        foreach (var l in resp["log"]?.AsArray() ?? [])
            if (l != null && _log.Count < 5000) _log.Enqueue(Clip(l.GetValue<string>()));
        if (resp["ok"]?.GetValue<bool>() != true) throw new CoSimException(resp["error"]?.GetValue<string>() ?? "The FMU host reported an error");
        return resp;
    }

    private string CrashMessage()
    {
        try { _process.WaitForExit(3000); } catch (InvalidOperationException) { }
        var what = "ended unexpectedly";
        if (_process.HasExited)
        {
            var code = _process.ExitCode;
            var signal = !OperatingSystem.IsWindows() && code > 128 ? code - 128 : 0;
            what = signal switch
            {
                11 => "crashed (segmentation fault, SIGSEGV)",
                10 when OperatingSystem.IsMacOS() => "crashed (bus error, SIGBUS)",
                7 when OperatingSystem.IsLinux() => "crashed (bus error, SIGBUS)",
                6 => "crashed (aborted, SIGABRT)",
                8 => "crashed (floating point exception, SIGFPE)",
                4 => "crashed (illegal instruction, SIGILL)",
                9 => "was killed (SIGKILL)",
                _ when OperatingSystem.IsWindows() && unchecked((uint)code) == 0xC0000005 => "crashed (access violation)",
                0 => "exited",
                _ => $"crashed or exited (exit code {code})",
            };
        }
        string tail;
        lock (_stderr) tail = string.Join("\n", _stderr.TakeLast(8));
        return $"The FMU {what}. Its host process ended; Meridian is unaffected." + (tail.Length > 0 ? $"\n{tail}" : "");
    }

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_process.HasExited)
            {
                try { Call(new JsonObject { ["op"] = "terminate" }, TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is CoSimException or OperationCanceledException or ObjectDisposedException) { }
                if (!_process.WaitForExit(2000)) Kill();
            }
        }
        finally
        {
            _disposed = true;
            _process.Dispose();
            _responses.Dispose();
        }
    }
}

/// <summary>
/// An FMU part of a co-simulation, run out of process through <see cref="FmuHostProcess"/>. Ports and parameters are matched
/// to the FMU's variables by name (as imported by <see cref="FmiImport"/>).
/// </summary>
public sealed class FmuUnit : ICoSimUnit
{
    private readonly string _dir;
    private readonly FmuHostProcess _host;
    private readonly List<Group> _in, _out;
    private readonly JsonArray _params;
    private readonly int _outCount;
    private readonly List<string> _log = new();
    private int _stepCalls;

    private sealed record Group(string Type, uint[] Vrs, int[] Index);

    public FmiModelDescription Description { get; }
    public bool HasState => true;
    public bool TerminateRequested { get; private set; }
    public int HostProcessId => _host.ProcessId;

    public FmuUnit(string fmuPath, string instanceName, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs,
        IReadOnlyDictionary<string, double> parameters, FmuRuntimeOptions? options = null, CancellationToken ct = default)
    {
        options ??= new FmuRuntimeOptions();
        _dir = FmuArchive.Extract(fmuPath, options.MaxExtractedBytes);
        try
        {
            var mdPath = Path.Combine(_dir, "modelDescription.xml");
            if (!File.Exists(mdPath)) throw new CoSimException("The FMU has no modelDescription.xml");
            try { Description = FmiImport.Parse(XDocument.Load(mdPath)); }
            catch (Exception ex) when (ex is XmlException or FormatException) { throw new CoSimException($"Invalid modelDescription.xml: {ex.Message}"); }
            var md = Description;
            if (!md.CoSimulation) throw new CoSimException($"{md.ModelName} does not support Co-Simulation (Model Exchange FMUs need a solver Meridian does not have)");
            var major = md.FmiVersion.StartsWith('3') ? 3 : md.FmiVersion.StartsWith('2') ? 2 : 0;
            if (major == 0) throw new CoSimException($"FMI {md.FmiVersion} is not supported (2.0 and 3.0 are)");
            var identifier = md.ModelIdentifier ?? throw new CoSimException("The FMU's CoSimulation element has no modelIdentifier");
            var library = FmuArchive.Binary(_dir, identifier);

            var byName = new Dictionary<string, FmiVariable>(StringComparer.Ordinal);
            foreach (var v in md.Variables) byName.TryAdd(FmiImport.SimpleName(v.Name), v);
            FmiVariable Var(string name, params string[] causalities) =>
                byName.TryGetValue(name, out var v) && causalities.Contains(v.Causality) ? v
                    : throw new CoSimException($"{md.ModelName} has no {string.Join(" / ", causalities)} variable '{name}' (was the FMU replaced? re-import it)");
            _in = Groups(inputs.Select(n => Var(n, "input")).ToList());
            _out = Groups(outputs.Select(n => Var(n, "output")).ToList());
            _outCount = outputs.Count;
            _params = new JsonArray();
            var pars = parameters.Where(p => byName.TryGetValue(p.Key, out var v) && v.Causality is "parameter" && v.Variability != "constant")
                .Select(p => (Var: byName[p.Key], p.Value)).ToList();
            foreach (var g in pars.GroupBy(p => p.Var.Type))
                _params.Add(new JsonObject
                {
                    ["type"] = g.Key,
                    ["vr"] = new JsonArray(g.Select(p => (JsonNode)JsonValue.Create(Vr(p.Var))).ToArray()),
                    ["v"] = new JsonArray(g.Select(p => Value(p.Var.Type, p.Value)).ToArray()),
                });

            _host = new FmuHostProcess(_dir, options, ct);
            _host.Call(new JsonObject
            {
                ["op"] = "load", ["fmiVersion"] = md.FmiVersion, ["library"] = library, ["modelIdentifier"] = identifier,
                ["token"] = md.Guid ?? "", ["resources"] = Path.Combine(_dir, "resources"), ["instance"] = instanceName, ["logging"] = options.FmuLogging,
            });
        }
        catch
        {
            _host?.Dispose();
            FmuArchive.TryDelete(_dir);
            throw;
        }
    }

    /// <summary>
    /// Unit factory for <see cref="CoSimulation.Run"/>: each FMU part runs the .fmu that <paramref name="fmuPathOf"/> finds for
    /// its block (null: the block's FMU is not available).
    /// </summary>
    public static Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit> Factory(Func<Meridian.Core.Model.Element, string?> fmuPathOf,
        FmuRuntimeOptions? options = null, CancellationToken ct = default) =>
        (part, parameters) => new FmuUnit(
            fmuPathOf(part.Type) ?? throw new CoSimException($"{part.Name}: the .fmu file for {part.Type.Name} is not available; attach it in the Co-simulation tab"),
            part.Name, part.Inputs.Select(i => i.Name).ToList(), part.Outputs.Select(o => o.Name).ToList(), parameters, options, ct);

    private static uint Vr(FmiVariable v) =>
        uint.TryParse(v.ValueReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : throw new CoSimException($"Variable {v.Name} has an invalid valueReference '{v.ValueReference}'");

    private static List<Group> Groups(List<FmiVariable> vars) =>
        vars.Select((v, i) => (v, i)).GroupBy(x => x.v.Type).Select(g => new Group(g.Key, g.Select(x => Vr(x.v)).ToArray(), g.Select(x => x.i).ToArray())).ToList();

    private static JsonNode Value(string type, double v) => type switch
    {
        "Boolean" => JsonValue.Create(v != 0),
        "Real" or "Float64" or "Float32" => double.IsFinite(v) ? JsonValue.Create(v) : JsonValue.Create(v.ToString(CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Math.Round(v)),
    };

    private JsonArray Set(double[] values) => new(_in.Select(g => (JsonNode)new JsonObject
    {
        ["type"] = g.Type,
        ["vr"] = new JsonArray(g.Vrs.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
        ["v"] = new JsonArray(g.Index.Select(i => Value(g.Type, values[i])).ToArray()),
    }).ToArray());

    private JsonArray Get() => new(_out.Select(g => (JsonNode)new JsonObject
    {
        ["type"] = g.Type,
        ["vr"] = new JsonArray(g.Vrs.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
    }).ToArray());

    private double[] Outputs(JsonObject resp)
    {
        var result = new double[_outCount];
        var groups = resp["values"]?.AsArray() ?? [];
        for (var gi = 0; gi < _out.Count; gi++)
        {
            var vals = gi < groups.Count ? groups[gi]?.AsArray() : null;
            for (var k = 0; k < _out[gi].Index.Length; k++)
                result[_out[gi].Index[k]] = vals != null && k < vals.Count ? ToDouble(vals[k]) : double.NaN;
        }
        return result;
    }

    private static double ToDouble(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => double.NaN,
    };

    public double[] Initialize(double start, double stop, double[] inputs) =>
        Outputs(_host.Call(new JsonObject { ["op"] = "init", ["start"] = start, ["stop"] = stop, ["params"] = _params.DeepClone(), ["inputs"] = Set(inputs), ["get"] = Get() }));

    public double[] Step(double t, double h, double[] inputs)
    {
        _stepCalls++;
        var resp = _host.Call(new JsonObject { ["op"] = "step", ["t"] = t, ["h"] = h, ["set"] = Set(inputs), ["get"] = Get() });
        TerminateRequested = resp["terminate"]?.GetValue<bool>() == true;
        return Outputs(resp);
    }

    public double[] Evaluate(double t, double[] inputs) =>
        Outputs(_host.Call(new JsonObject { ["op"] = "get", ["get"] = Get() }));

    public IReadOnlyList<string> DrainLog() => _host.DrainLog();

    public void Dispose()
    {
        try { _host.Dispose(); }
        finally { FmuArchive.TryDelete(_dir); }
    }
}

/// <summary>
/// Content-addressed store of uploaded .fmu files (by SHA-256), so co-simulation can find an imported block's FMU later.
/// The block records the hash in its fmi.sha256 tagged value.
/// </summary>
public sealed class FmuStore(string directory)
{
    public string Directory { get; } = directory;

    public string Save(byte[] fmu)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(fmu));
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, hash + ".fmu");
        if (!File.Exists(path))
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, fmu);
            File.Move(tmp, path, overwrite: true);
        }
        return hash;
    }

    /// <summary>The stored file for a hash, or null (also for anything that is not a SHA-256 hex string).</summary>
    public string? Find(string? hash)
    {
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) return null;
        var path = Path.Combine(Directory, hash.ToLowerInvariant() + ".fmu");
        return File.Exists(path) ? path : null;
    }
}
