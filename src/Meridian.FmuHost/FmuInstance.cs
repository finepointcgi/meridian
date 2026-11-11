using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Meridian.FmuHost;

/// <summary>An error reported by (or about) the FMU; sent back to Meridian as the response's error text.</summary>
public sealed class FmuException(string message) : Exception(message);

public enum ValueKind { Float, Signed, Unsigned, Bool, String }

/// <summary>An FMI scalar type: its C size and how values are converted to and from JSON.</summary>
public sealed record ValueType(string Name, int Size, ValueKind Kind)
{
    public static ValueType Resolve(int fmiMajor, string type) => fmiMajor >= 3
        ? type switch
        {
            "Float64" => new("Float64", 8, ValueKind.Float),
            "Float32" => new("Float32", 4, ValueKind.Float),
            "Int8" => new("Int8", 1, ValueKind.Signed),
            "UInt8" => new("UInt8", 1, ValueKind.Unsigned),
            "Int16" => new("Int16", 2, ValueKind.Signed),
            "UInt16" => new("UInt16", 2, ValueKind.Unsigned),
            "Int32" => new("Int32", 4, ValueKind.Signed),
            "UInt32" => new("UInt32", 4, ValueKind.Unsigned),
            "Int64" or "Enumeration" => new("Int64", 8, ValueKind.Signed),
            "UInt64" => new("UInt64", 8, ValueKind.Unsigned),
            "Boolean" => new("Boolean", 1, ValueKind.Bool),       // fmi3Boolean is C99 bool
            "String" => new("String", IntPtr.Size, ValueKind.String),
            _ => throw new FmuException($"Unsupported FMI 3.0 variable type '{type}'"),
        }
        : type switch
        {
            "Real" => new("Real", 8, ValueKind.Float),
            "Integer" or "Enumeration" => new("Integer", 4, ValueKind.Signed),
            "Boolean" => new("Boolean", 4, ValueKind.Bool),       // fmi2Boolean is int
            "String" => new("String", IntPtr.Size, ValueKind.String),
            _ => throw new FmuException($"Unsupported FMI 2.0 variable type '{type}'"),
        };
}

/// <summary>
/// One loaded FMU instance (FMI 2.0 or 3.0 Co-Simulation), bound through function pointers looked up with
/// <see cref="NativeLibrary"/>. Single-threaded: the host handles one request at a time.
/// </summary>
public abstract unsafe class FmuInstance : IDisposable
{
    /// <summary>Log lines from the FMU's logger callback, drained into each response.</summary>
    public static readonly List<string> Log = new();
    private static int _dropped;

    protected IntPtr Lib;
    protected IntPtr Handle;
    protected readonly List<IntPtr> KeepAlive = new();
    protected abstract string Prefix { get; }
    public abstract int Major { get; }
    public bool Instantiated => Handle != IntPtr.Zero;
    private readonly Dictionary<string, IntPtr> _symbols = new();

    public static FmuInstance Create(string version) =>
        version.StartsWith('3') ? new Fmi3Instance() : version.StartsWith('2') ? new Fmi2Instance()
            : throw new FmuException($"FMI version '{version}' is not supported (2.0 and 3.0 Co-Simulation are)");

    public void LoadLibrary(string path)
    {
        if (!File.Exists(path)) throw new FmuException($"FMU binary not found: {path}");
        try { Lib = NativeLibrary.Load(path); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new FmuException($"Could not load {Path.GetFileName(path)} (wrong architecture or missing dependency?): {ex.Message}");
        }
    }

    protected IntPtr Sym(string name)
    {
        if (_symbols.TryGetValue(name, out var p)) return p;
        if (Lib == IntPtr.Zero) throw new FmuException("The FMU library is not loaded");
        // Functions may be exported with or without a model-identifier prefix; FMI requires the plain names for binaries.
        if (!NativeLibrary.TryGetExport(Lib, name, out p) || p == IntPtr.Zero)
            throw new FmuException($"The FMU does not export {name}");
        return _symbols[name] = p;
    }

    protected static IntPtr Utf8(string? s, List<IntPtr> keep)
    {
        if (s == null) return IntPtr.Zero;
        var p = Marshal.StringToCoTaskMemUTF8(s);
        keep.Add(p);
        return p;
    }

    public abstract void Instantiate(string modelIdentifier, string instanceName, string token, string resourceDir, bool logging);
    public abstract void Setup(double start, double? stop, double? tolerance);
    public abstract void EnterInitialization();
    public abstract void ExitInitialization();
    /// <summary>Advances one communication step. Returns true when the FMU asks to end the simulation.</summary>
    public abstract bool DoStep(double t, double h);
    public abstract void Terminate();
    protected abstract void FreeInstance();
    protected abstract int CallSet(IntPtr fn, uint* vr, nuint n, void* values);
    protected abstract int CallGet(IntPtr fn, uint* vr, nuint n, void* values);

    protected void Check(int status, string fn)
    {
        switch (status)
        {
            case 0: return;
            case 1: AddLog($"[warning] {fn} returned Warning"); return;
            case 2: throw new FmuException($"{fn} returned Discard (the FMU could not complete the call; try a smaller step size)");
            case 3: throw new FmuException($"{fn} returned Error");
            case 4: throw new FmuException($"{fn} returned Fatal (the FMU instance is unusable)");
            case 5: throw new FmuException($"{fn} returned Pending (asynchronous co-simulation is not supported)");
            default: throw new FmuException($"{fn} returned unknown status {status}");
        }
    }

    public static void AddLog(string line)
    {
        lock (Log)
        {
            if (Log.Count >= 2000) { _dropped++; return; }
            Log.Add(line);
        }
    }

    public static JsonArray DrainLog()
    {
        lock (Log)
        {
            var a = new JsonArray();
            foreach (var l in Log) a.Add(l);
            if (_dropped > 0) a.Add($"({_dropped} more log lines dropped)");
            Log.Clear();
            _dropped = 0;
            return a;
        }
    }

    protected static string StatusName(int s) => s switch { 0 => "ok", 1 => "warning", 2 => "discard", 3 => "error", 4 => "fatal", 5 => "pending", _ => s.ToString(CultureInfo.InvariantCulture) };

    public void Set(ValueType t, uint[] vrs, JsonArray values)
    {
        if (vrs.Length == 0) return;
        if (values.Count != vrs.Length) throw new FmuException($"Set{t.Name}: {vrs.Length} value references but {values.Count} values");
        var fn = Sym($"{Prefix}Set{t.Name}");
        var buf = (byte*)NativeMemory.AllocZeroed((nuint)vrs.Length, (nuint)t.Size);
        var strings = new List<IntPtr>();
        try
        {
            for (var i = 0; i < vrs.Length; i++) Write(buf + i * t.Size, t, values[i], strings);
            fixed (uint* vr = vrs) Check(CallSet(fn, vr, (nuint)vrs.Length, buf), $"{Prefix}Set{t.Name}");
        }
        finally
        {
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
            NativeMemory.Free(buf);
        }
    }

    public JsonArray Get(ValueType t, uint[] vrs)
    {
        var result = new JsonArray();
        if (vrs.Length == 0) return result;
        var fn = Sym($"{Prefix}Get{t.Name}");
        var buf = (byte*)NativeMemory.AllocZeroed((nuint)vrs.Length, (nuint)t.Size);
        try
        {
            fixed (uint* vr = vrs) Check(CallGet(fn, vr, (nuint)vrs.Length, buf), $"{Prefix}Get{t.Name}");
            for (var i = 0; i < vrs.Length; i++) result.Add(Read(buf + i * t.Size, t));
        }
        finally { NativeMemory.Free(buf); }
        return result;
    }

    public static double Num(JsonNode? v) => v switch
    {
        null => 0,
        JsonValue jv when jv.TryGetValue<double>(out var d) => d,
        JsonValue jv when jv.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue jv when jv.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new FmuException($"'{v.ToJsonString()}' is not a number"),
    };

    public static JsonNode Number(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));

    private static void Write(byte* p, ValueType t, JsonNode? v, List<IntPtr> strings)
    {
        switch (t.Kind)
        {
            case ValueKind.Float:
                var d = Num(v);
                if (t.Size == 8) *(double*)p = d; else *(float*)p = (float)d;
                break;
            case ValueKind.Signed:
                var l = checked((long)Math.Round(Num(v)));
                switch (t.Size) { case 1: *(sbyte*)p = checked((sbyte)l); break; case 2: *(short*)p = checked((short)l); break; case 4: *(int*)p = checked((int)l); break; default: *(long*)p = l; break; }
                break;
            case ValueKind.Unsigned:
                var u = checked((ulong)Math.Round(Num(v)));
                switch (t.Size) { case 1: *p = checked((byte)u); break; case 2: *(ushort*)p = checked((ushort)u); break; case 4: *(uint*)p = checked((uint)u); break; default: *(ulong*)p = u; break; }
                break;
            case ValueKind.Bool:
                var b = v is JsonValue bv && bv.TryGetValue<bool>(out var bb) ? bb : Num(v) != 0;
                if (t.Size == 1) *p = (byte)(b ? 1 : 0); else *(int*)p = b ? 1 : 0;
                break;
            case ValueKind.String:
                var s = v is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : v?.ToJsonString() ?? "";
                var ptr = Marshal.StringToCoTaskMemUTF8(s);
                strings.Add(ptr);
                *(IntPtr*)p = ptr;
                break;
        }
    }

    private static JsonNode? Read(byte* p, ValueType t) => t.Kind switch
    {
        ValueKind.Float => Number(t.Size == 8 ? *(double*)p : *(float*)p),
        ValueKind.Signed => JsonValue.Create(t.Size switch { 1 => *(sbyte*)p, 2 => *(short*)p, 4 => *(int*)p, _ => *(long*)p }),
        ValueKind.Unsigned => JsonValue.Create(t.Size switch { 1 => *p, 2 => *(ushort*)p, 4 => *(uint*)p, _ => *(ulong*)p }),
        ValueKind.Bool => JsonValue.Create(t.Size == 1 ? *p != 0 : *(int*)p != 0),
        ValueKind.String => JsonValue.Create(Marshal.PtrToStringUTF8(*(IntPtr*)p) ?? ""),
        _ => null,
    };

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            try { FreeInstance(); } catch (FmuException) { }
            Handle = IntPtr.Zero;
        }
        foreach (var p in KeepAlive) Marshal.FreeCoTaskMem(p);
        KeepAlive.Clear();
        // The library stays loaded: the process exits right after, and unloading native code that may still have threads is unsafe.
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- callbacks shared by both versions

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    protected static void* Calloc(nuint count, nuint size) => NativeMemory.AllocZeroed(count == 0 ? 1 : count, size == 0 ? 1 : size);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    protected static void Free(void* p) { if (p != null) NativeMemory.Free(p); }

    protected static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}

/// <summary>FMI 2.0 Co-Simulation.</summary>
public sealed unsafe class Fmi2Instance : FmuInstance
{
    protected override string Prefix => "fmi2";
    public override int Major => 2;
    private IntPtr _callbacks;

    // fmi2CallbackLogger is variadic; the fixed arguments arrive normally and the format arguments are ignored
    // (the message is logged unformatted).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Logger(IntPtr env, IntPtr instanceName, int status, IntPtr category, IntPtr message) =>
        AddLog($"[{StatusName(status)}] {Str(category)}: {Str(message)}");

    public override void Instantiate(string modelIdentifier, string instanceName, string token, string resourceDir, bool logging)
    {
        // fmi2CallbackFunctions { logger, allocateMemory, freeMemory, stepFinished, componentEnvironment }. The FMU may keep
        // a pointer to it, so it lives in native memory until the instance is freed.
        _callbacks = (IntPtr)NativeMemory.AllocZeroed(5, (nuint)IntPtr.Size);
        var cb = (IntPtr*)_callbacks;
        cb[0] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr, IntPtr, void>)&Logger;
        cb[1] = (IntPtr)(delegate* unmanaged[Cdecl]<nuint, nuint, void*>)&Calloc;
        cb[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void>)&Free;
        var uri = new Uri(Path.GetFullPath(resourceDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, IntPtr, int, int, IntPtr>)Sym("fmi2Instantiate");
        Handle = fn(Utf8(instanceName, KeepAlive), 1 /* fmi2CoSimulation */, Utf8(token, KeepAlive), Utf8(uri, KeepAlive), _callbacks, 0, logging ? 1 : 0);
        if (Handle == IntPtr.Zero) throw new FmuException("fmi2Instantiate failed (returned NULL)");
    }

    public override void Setup(double start, double? stop, double? tolerance)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, int, double, double, int, double, int>)Sym("fmi2SetupExperiment");
        Check(fn(Handle, tolerance.HasValue ? 1 : 0, tolerance ?? 0, start, stop.HasValue ? 1 : 0, stop ?? 0), "fmi2SetupExperiment");
    }

    private void Simple(string name) => Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Sym(name))(Handle), name);

    public override void EnterInitialization() => Simple("fmi2EnterInitializationMode");
    public override void ExitInitialization() => Simple("fmi2ExitInitializationMode");
    public override void Terminate() => Simple("fmi2Terminate");

    public override bool DoStep(double t, double h)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, double, double, int, int>)Sym("fmi2DoStep");
        var status = fn(Handle, t, h, 1);
        if (status == 2 /* fmi2Discard */)
        {
            // A slave that wants to end the simulation returns Discard and reports fmi2Terminated (= 3) as true.
            var getBool = (delegate* unmanaged[Cdecl]<IntPtr, int, int*, int>)Sym("fmi2GetBooleanStatus");
            var terminated = 0;
            if (getBool(Handle, 3, &terminated) == 0 && terminated != 0)
            {
                AddLog("[info] fmi2DoStep: the FMU asked to end the simulation (fmi2Terminated)");
                return true;
            }
        }
        Check(status, "fmi2DoStep");
        return false;
    }

    protected override void FreeInstance()
    {
        ((delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("fmi2FreeInstance"))(Handle);
        if (_callbacks != IntPtr.Zero) { NativeMemory.Free((void*)_callbacks); _callbacks = IntPtr.Zero; }
    }

    protected override int CallSet(IntPtr fn, uint* vr, nuint n, void* values) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint*, nuint, void*, int>)fn)(Handle, vr, n, values);

    protected override int CallGet(IntPtr fn, uint* vr, nuint n, void* values) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint*, nuint, void*, int>)fn)(Handle, vr, n, values);
}

/// <summary>FMI 3.0 Co-Simulation (no event mode, no early return, no intermediate updates).</summary>
public sealed unsafe class Fmi3Instance : FmuInstance
{
    protected override string Prefix => "fmi3";
    public override int Major => 3;
    private double _start;
    private double? _stop, _tolerance;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Logger(IntPtr env, int status, IntPtr category, IntPtr message) =>
        AddLog($"[{StatusName(status)}] {Str(category)}: {Str(message)}");

    public override void Instantiate(string modelIdentifier, string instanceName, string token, string resourceDir, bool logging)
    {
        var resources = Path.GetFullPath(resourceDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte, byte, byte, byte, uint*, nuint, IntPtr, IntPtr, IntPtr, IntPtr>)Sym("fmi3InstantiateCoSimulation");
        var logger = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr, void>)&Logger;
        Handle = fn(Utf8(instanceName, KeepAlive), Utf8(token, KeepAlive), Utf8(resources, KeepAlive), 0, (byte)(logging ? 1 : 0), 0, 0, null, 0, IntPtr.Zero, logger, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new FmuException("fmi3InstantiateCoSimulation failed (returned NULL)");
    }

    public override void Setup(double start, double? stop, double? tolerance) { _start = start; _stop = stop; _tolerance = tolerance; }

    public override void EnterInitialization()
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte, double, double, byte, double, int>)Sym("fmi3EnterInitializationMode");
        Check(fn(Handle, (byte)(_tolerance.HasValue ? 1 : 0), _tolerance ?? 0, _start, (byte)(_stop.HasValue ? 1 : 0), _stop ?? 0), "fmi3EnterInitializationMode");
    }

    private void Simple(string name) => Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Sym(name))(Handle), name);
    public override void ExitInitialization() => Simple("fmi3ExitInitializationMode");
    public override void Terminate() => Simple("fmi3Terminate");

    public override bool DoStep(double t, double h)
    {
        var fn = (delegate* unmanaged[Cdecl]<IntPtr, double, double, byte, byte*, byte*, byte*, double*, int>)Sym("fmi3DoStep");
        byte eventNeeded = 0, terminate = 0, early = 0;
        double last = 0;
        Check(fn(Handle, t, h, 1, &eventNeeded, &terminate, &early, &last), "fmi3DoStep");
        if (eventNeeded != 0) AddLog("[warning] fmi3DoStep asked for event handling, which this master does not do (eventModeUsed = false)");
        return terminate != 0;
    }

    protected override void FreeInstance() => ((delegate* unmanaged[Cdecl]<IntPtr, void>)Sym("fmi3FreeInstance"))(Handle);

    protected override int CallSet(IntPtr fn, uint* vr, nuint n, void* values) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint*, nuint, void*, nuint, int>)fn)(Handle, vr, n, values, n);

    protected override int CallGet(IntPtr fn, uint* vr, nuint n, void* values) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint*, nuint, void*, nuint, int>)fn)(Handle, vr, n, values, n);
}
