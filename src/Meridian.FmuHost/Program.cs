using System.Text;
using System.Text.Json.Nodes;
using Meridian.FmuHost;

// Meridian FMU host: runs ONE FMU instance for a Meridian co-simulation, in its own process, so that a crashing FMU
// cannot take down the server. Protocol: one JSON request per line on stdin, one JSON response per line on stdout,
// each response prefixed with a marker (FMUs sometimes print to stdout themselves; unmarked lines are passed back as log).
// This is crash isolation, not a sandbox: see docs/FMI.md.
//
// Requests (op):
//   hello                                        → { version }
//   load      { fmiVersion, library, modelIdentifier, token, resources, instance, logging }
//   init      { start, stop?, tolerance?, params: [Group], inputs: [Group], get: [GetGroup] }   → { values }
//   step      { t, h, set: [Group], get: [GetGroup] }                                         → { values, terminate }
//   get       { get: [GetGroup] }                                                             → { values }
//   terminate                                    → frees the instance and exits
// Group = { type, vr: [uint], v: [value] }, GetGroup = { type, vr: [uint] }; values are aligned with the get groups.

const string Marker = "@@meridian-fmuhost@@ ";
var stdout = Console.OpenStandardOutput();
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
FmuInstance? fmu = null;

if (args.Contains("--version")) { Console.WriteLine("Meridian.FmuHost 1"); return 0; }

string? line;
while ((line = stdin.ReadLine()) != null)
{
    if (line.Length == 0) continue;
    JsonObject resp;
    var exit = false;
    try
    {
        var req = JsonNode.Parse(line)?.AsObject() ?? throw new FmuException("Empty request");
        var op = req["op"]?.GetValue<string>() ?? "";
        resp = new JsonObject { ["ok"] = true };
        switch (op)
        {
            case "hello":
                resp["version"] = 1;
                break;
            case "load":
                if (fmu != null) throw new FmuException("An FMU is already loaded in this host");
                fmu = FmuInstance.Create(S(req, "fmiVersion"));
                fmu.LoadLibrary(S(req, "library"));
                fmu.Instantiate(S(req, "modelIdentifier"), S(req, "instance"), req["token"]?.GetValue<string>() ?? "", S(req, "resources"),
                    req["logging"]?.GetValue<bool>() ?? false);
                break;
            case "init":
                {
                    var f = Need(fmu);
                    f.Setup(D(req, "start") ?? 0, D(req, "stop"), D(req, "tolerance"));
                    Apply(f, req["params"]);
                    f.EnterInitialization();
                    Apply(f, req["inputs"]);
                    f.ExitInitialization();
                    resp["values"] = Read(f, req["get"]);
                    break;
                }
            case "step":
                {
                    var f = Need(fmu);
                    Apply(f, req["set"]);
                    resp["terminate"] = f.DoStep(D(req, "t") ?? 0, D(req, "h") ?? throw new FmuException("step needs h"));
                    resp["values"] = Read(f, req["get"]);
                    break;
                }
            case "get":
                resp["values"] = Read(Need(fmu), req["get"]);
                break;
            case "terminate":
                if (fmu is { Instantiated: true })
                {
                    try { fmu.Terminate(); } catch (FmuException ex) { FmuInstance.AddLog("[warning] " + ex.Message); }
                }
                fmu?.Dispose();
                fmu = null;
                exit = true;
                break;
            default:
                throw new FmuException($"Unknown op '{op}'");
        }
    }
    catch (Exception ex)
    {
        resp = new JsonObject { ["ok"] = false, ["error"] = ex is FmuException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}" };
    }
    resp["log"] = FmuInstance.DrainLog();
    // One write per response, so FMU output on the same stream cannot split it.
    var bytes = Encoding.UTF8.GetBytes("\n" + Marker + resp.ToJsonString() + "\n");
    stdout.Write(bytes);
    stdout.Flush();
    if (exit) break;
}
fmu?.Dispose();
return 0;

static FmuInstance Need(FmuInstance? f) => f ?? throw new FmuException("No FMU is loaded");

static string S(JsonObject o, string key) => o[key]?.GetValue<string>() ?? throw new FmuException($"Missing '{key}'");

static double? D(JsonObject o, string key) => o[key] is { } n ? FmuInstance.Num(n) : null;

static uint[] Vrs(JsonNode? g) => g?["vr"]?.AsArray().Select(v => (uint)FmuInstance.Num(v)).ToArray() ?? [];

static void Apply(FmuInstance f, JsonNode? groups)
{
    foreach (var g in groups?.AsArray() ?? [])
    {
        if (g == null) continue;
        var type = Meridian.FmuHost.ValueType.Resolve(f.Major, g["type"]?.GetValue<string>() ?? "");
        f.Set(type, Vrs(g), g["v"]?.AsArray() ?? []);
    }
}

static JsonArray Read(FmuInstance f, JsonNode? groups)
{
    var result = new JsonArray();
    foreach (var g in groups?.AsArray() ?? [])
    {
        if (g == null) { result.Add(new JsonArray()); continue; }
        var type = Meridian.FmuHost.ValueType.Resolve(f.Major, g["type"]?.GetValue<string>() ?? "");
        result.Add(f.Get(type, Vrs(g)));
    }
    return result;
}
