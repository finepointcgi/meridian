using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Core.CodeSync.Conformance;

/// <summary>
/// Compact storage of a <see cref="CodeModel"/> (so an architecture check can run again later without re-reading the
/// repository): JSON without defaults or indentation, gzip-compressed and base64-encoded.
/// </summary>
public static class CodeModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver { Modifiers = { Compact } },
    };

    /// <summary>Method bodies and the newer optional lists are written without empty lists and default values, so they
    /// stay small; reading fills them back in from the property defaults. Older JSON (without them) reads unchanged.</summary>
    private static void Compact(System.Text.Json.Serialization.Metadata.JsonTypeInfo info)
    {
        var body = info.Type == typeof(CodeStatement) || info.Type == typeof(CodeBranch) || info.Type == typeof(CodeBody);
        foreach (var p in info.Properties)
        {
            if (p.PropertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) &&(body || p.Name is "Attributes" or "ExternalBases"))
                p.ShouldSerialize = (_, v) => v is System.Collections.ICollection { Count: > 0 };
            else if (body && p.PropertyType == typeof(bool)) p.ShouldSerialize = (_, v) => v is true;
            else if (body && p.PropertyType == typeof(int) && p.Name is "ArgumentCount" or "Omitted") p.ShouldSerialize = (_, v) => v is not 0;
            else if (body && p.PropertyType == typeof(CodeLoopKind?)) p.ShouldSerialize = (_, v) => v is not null;
        }
    }

    public static string Serialize(CodeModel code) => JsonSerializer.Serialize(code, Options);

    public static CodeModel? Deserialize(string json)
    {
        try { return Normalize(JsonSerializer.Deserialize<CodeModel>(json, Options)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Gzip + base64 of <see cref="Serialize"/>.</summary>
    public static string Pack(CodeModel code)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(code, Options);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(bytes);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>The model packed by <see cref="Pack"/>; null when the text is not one.</summary>
    public static CodeModel? Unpack(string? packed)
    {
        if (string.IsNullOrEmpty(packed)) return null;
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(packed));
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            return Normalize(JsonSerializer.Deserialize<CodeModel>(gz, Options));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException) { return null; }
    }

    /// <summary>Lists left out of the JSON come back empty rather than null.</summary>
    private static CodeModel? Normalize(CodeModel? c) => c is null ? null : c with
    {
        Types = (c.Types ?? []).Select(t => t with
        {
            BaseTypes = t.BaseTypes ?? [],
            Interfaces = t.Interfaces ?? [],
            Members = t.Members ?? [],
            Dependencies = t.Dependencies ?? [],
            DependencyLocations = t.DependencyLocations ?? [],
            OmittedDependencyLocations = t.OmittedDependencyLocations ?? CodeDependencyUses.NoneOmitted,
        }).ToList(),
        Warnings = c.Warnings ?? [],
    };
}
