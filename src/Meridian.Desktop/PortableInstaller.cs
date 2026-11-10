using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Meridian.Serialization;

namespace Meridian.Desktop;

/// <summary>
/// Where the running app is installed: the folder (Windows, Linux) or .app bundle (macOS) a portable update replaces.
/// Only a published, self-contained Meridian is recognised (System.Private.CoreLib.dll next to the executable), so a
/// development build under bin/ is never swapped.
/// </summary>
/// <param name="Root">The folder or bundle that is replaced as a whole.</param>
/// <param name="Executable">The app's executable, relative to <paramref name="Root"/>.</param>
/// <param name="HasInstaller">Windows: installed by the setup (an Inno Setup uninstaller sits next to it).</param>
public sealed record InstallLocation(string Root, string Executable, OSPlatform Os, bool HasInstaller = false)
{
    public string Parent => Path.GetDirectoryName(Root)!;
    public string Name => Path.GetFileName(Root);
    /// <summary>The new version is unpacked here (next to the install, so the swap is a rename on the same volume).</summary>
    public string StagingPath => Path.Combine(Parent, "." + Name + ".meridian-update");
    /// <summary>The previous version is moved here during the swap and deleted on the next start.</summary>
    public string OldPath => Path.Combine(Parent, "." + Name + ".meridian-old");
    public string ExecutablePath => Path.Combine(Root, Executable);

    public static InstallLocation? Detect(string baseDirectory, OSPlatform os)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!File.Exists(Path.Combine(dir, "System.Private.CoreLib.dll"))) return null;   // framework-dependent dev build
        if (os == OSPlatform.OSX)
        {
            // …/Meridian.app/Contents/MacOS
            var contents = Path.GetDirectoryName(dir);
            var bundle = contents is null ? null : Path.GetDirectoryName(contents);
            if (Path.GetFileName(dir) != "MacOS" || Path.GetFileName(contents) != "Contents" || bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(Path.Combine(dir, "Meridian")) ? new InstallLocation(bundle, "Contents/MacOS/Meridian", os) : null;
        }
        var exe = os == OSPlatform.Windows ? "Meridian.exe" : "Meridian";
        if (!File.Exists(Path.Combine(dir, exe)) || Path.GetDirectoryName(dir) is null) return null;
        var hasInstaller = os == OSPlatform.Windows && Directory.EnumerateFiles(dir, "unins*.exe").Any();
        return new InstallLocation(dir, exe, os, hasInstaller);
    }
}

/// <summary>Limits for unpacking an update archive (tests shrink them).</summary>
public sealed record ExtractLimits
{
    public int MaxEntries { get; init; } = 50_000;
    public long MaxFileBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public static readonly ExtractLimits Default = new();
}

/// <summary>
/// Portable updates: unpack the verified archive next to the install, then a small script (update.cmd on Windows,
/// update.sh elsewhere) waits for this process to quit, moves the old install aside, moves the new one in (restoring the
/// old one if that fails) and starts Meridian again. The user's data folder lives outside the install and is never touched.
/// </summary>
public static class PortableInstaller
{
    public const string NewFolder = "new";

    /// <summary>Why this install cannot replace itself (not writable, data inside it), or null when it can.</summary>
    public static string? CheckWritable(InstallLocation location, string dataFolder)
    {
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (data.Equals(location.Root, comparison) || data.StartsWith(location.Root + Path.DirectorySeparatorChar, comparison))
            return $"The data folder ({data}) is inside the app's folder, so Meridian will not replace that folder automatically.";
        if (!CanWriteIn(location.Parent) || !CanWriteIn(location.Root))
            return $"Meridian can't replace itself in {location.Parent} (no permission to write there).";
        return null;
    }

    private static bool CanWriteIn(string folder)
    {
        var probe = Path.Combine(folder, $".meridian-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        finally { try { File.Delete(probe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Deletes what an earlier update left behind (the staging folder and the previous version). Never the install itself.</summary>
    public static void CleanupLeftovers(InstallLocation location, Action<string>? log = null)
    {
        foreach (var path in new[] { location.StagingPath, location.OldPath })
        {
            if (string.Equals(path, location.Root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path)) continue;
            try { Directory.Delete(path, true); log?.Invoke($"Removed {path} (left over from an update)."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"Could not remove {path}: {e.Message}"); }
        }
    }

    /// <summary>
    /// Unpacks a verified archive into <c>StagingPath/new</c> and checks the result is a Meridian install for this OS.
    /// Returns the folder that will replace <see cref="InstallLocation.Root"/>.
    /// </summary>
    public static async Task<string> StageAsync(string archive, InstallLocation location, ExtractLimits? limits = null, CancellationToken ct = default)
    {
        if (Directory.Exists(location.StagingPath)) Directory.Delete(location.StagingPath, true);
        var target = Path.Combine(location.StagingPath, NewFolder);
        Directory.CreateDirectory(target);
        try
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ExtractZip(archive, target, limits ?? ExtractLimits.Default);
            else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                await ExtractTarGzAsync(archive, target, limits ?? ExtractLimits.Default, ct);
            else throw new InvalidDataException($"{Path.GetFileName(archive)} is not a .zip or .tar.gz.");
            var exe = Path.Combine(target, location.Executable);
            if (!File.Exists(exe)) throw new InvalidDataException($"The package has no {location.Executable.Replace('\\', '/')}; it is not a Meridian build for this computer.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return target;
        }
        catch
        {
            try { Directory.Delete(location.StagingPath, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// The path of an entry below the archive's single top-level folder (Meridian/, meridian-0.6.0/, Meridian.app/), or null for
    /// the top-level folder itself and for macOS metadata (__MACOSX/, ._*). Absolute paths, drive letters, ".." and NUL make the
    /// archive invalid (the same rules as SnapshotArchive.RelativePath).
    /// </summary>
    public static string? EntryPath(string name, ref string? top)
    {
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/') || n.Contains('\0') || (n.Length >= 2 && n[1] == ':'))
            throw new InvalidDataException($"The archive contains an unsafe path: {Printable(name)}");
        var segments = n.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        if (segments.Any(s => s == ".."))
            throw new InvalidDataException($"The archive contains an unsafe path: {Printable(name)}");
        if (segments.Length == 0 || segments[0] == "__MACOSX" || segments[^1].StartsWith("._", StringComparison.Ordinal)) return null;
        top ??= segments[0];
        if (segments[0] != top) throw new InvalidDataException("The archive must contain a single top-level folder.");
        return segments.Length < 2 ? null : string.Join('/', segments.Skip(1));
    }

    private static string Printable(string s) => new(s.Take(200).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>The full path for a relative entry path, which must stay inside <paramref name="root"/>.</summary>
    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The archive contains an unsafe path: {Printable(relative)}");
        return full;
    }

    private static void ExtractZip(string archive, string target, ExtractLimits limits)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > limits.MaxEntries) throw new InvalidDataException($"The archive has more than {limits.MaxEntries:N0} entries.");
        string? top = null;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (EntryPath(entry.FullName, ref top) is not { } rel) continue;
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var type = unixMode & 0xF000;
            if (type == 0xA000) throw new InvalidDataException($"The archive contains a symbolic link ({Printable(rel)}), which updates do not accept.");
            var path = Inside(target, rel);
            if (entry.FullName.EndsWith('/') || type == 0x4000) { Directory.CreateDirectory(path); continue; }
            if (entry.Length > limits.MaxFileBytes) throw new InvalidDataException($"{Printable(rel)} is larger than {limits.MaxFileBytes / (1024 * 1024)} MB when unpacked.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var src = SafeZip.Open(entry, limits.MaxFileBytes))
            using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                total += CopyLimited(src, dst, limits.MaxTotalBytes - total, rel);
            if (!OperatingSystem.IsWindows() && (unixMode & 0x1FF) is var perms and not 0)
                File.SetUnixFileMode(path, (UnixFileMode)perms);
        }
    }

    private static async Task ExtractTarGzAsync(string archive, string target, ExtractLimits limits, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var bounded = new SafeZip.LimitedStream(gzip, limits.MaxTotalBytes + 64L * 1024 * 1024, "The archive");
        await using var reader = new TarReader(bounded);
        string? top = null;
        var entries = 0;
        long total = 0;
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (++entries > limits.MaxEntries) throw new InvalidDataException($"The archive has more than {limits.MaxEntries:N0} entries.");
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;
            if (EntryPath(entry.Name, ref top) is not { } rel) continue;
            var path = Inside(target, rel);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (entry.Length > limits.MaxFileBytes) throw new InvalidDataException($"{Printable(rel)} is larger than {limits.MaxFileBytes / (1024 * 1024)} MB when unpacked.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        if (entry.DataStream is { } data) total += CopyLimited(data, dst, limits.MaxTotalBytes - total, rel);
                    if (!OperatingSystem.IsWindows() && (entry.Mode & (UnixFileMode)0x1FF) is var mode and not 0) File.SetUnixFileMode(path, mode);
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    throw new InvalidDataException($"The archive contains a link ({Printable(rel)}), which updates do not accept.");
                default:
                    break;   // devices, FIFOs: never part of a Meridian package
            }
        }
    }

    private static long CopyLimited(Stream src, Stream dst, long remaining, string name)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = src.Read(buffer)) > 0)
        {
            copied += read;
            if (copied > remaining) throw new InvalidDataException($"The archive is too large when unpacked (at {Printable(name)}).");
            dst.Write(buffer, 0, read);
        }
        return copied;
    }

    // ------------------------------------------------------------------ swap scripts

    /// <summary>Single-quotes a value for /bin/sh: every character is literal, including spaces, $, ` and ".</summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// The update.sh that swaps a Linux folder or macOS .app bundle once process <paramref name="pid"/> has quit, then runs
    /// <paramref name="relaunch"/> (detached). On any failure the old install is put back and relaunched.
    /// </summary>
    public static string ShellScript(int pid, InstallLocation location, string newRoot, string[] relaunch, string logFile, string dataFolder)
    {
        var start = "nohup " + string.Join(' ', relaunch.Select(ShQuote)) + " >/dev/null 2>&1 &";
        return $$"""
            #!/bin/sh
            # Written by Meridian's updater. Waits for Meridian (pid {{pid}}) to quit, moves the old version aside, moves the
            # new one in (putting the old one back if that fails) and starts Meridian again.
            # The data folder is not touched: {{dataFolder.Replace("\n", " ")}}
            PID={{pid}}
            APP={{ShQuote(location.Root)}}
            NEW={{ShQuote(newRoot)}}
            OLD={{ShQuote(location.OldPath)}}
            LOG={{ShQuote(logFile)}}
            log() { printf '[%s] update: %s\n' "$(date '+%Y-%m-%dT%H:%M:%S')" "$1" >> "$LOG" 2>/dev/null; }
            relaunch() { {{start}} }
            cd / || exit 1
            i=0
            while kill -0 "$PID" 2>/dev/null; do
              i=$((i + 1))
              if [ "$i" -gt 600 ]; then log "Meridian (pid $PID) did not quit; the update was cancelled"; exit 1; fi
              sleep 1
            done
            if [ ! -d "$NEW" ]; then log "the new version is missing; nothing was changed"; relaunch; exit 1; fi
            rm -rf "$OLD"
            if ! mv "$APP" "$OLD"; then log "could not move $APP aside; nothing was changed"; relaunch; exit 1; fi
            if ! mv "$NEW" "$APP"; then
              log "could not move the new version in; restoring the previous one"
              rm -rf "$APP"
              mv "$OLD" "$APP" || log "RESTORE FAILED: the previous version is in $OLD"
              relaunch
              exit 1
            fi
            log "installed"
            relaunch
            exit 0

            """.Replace("\r\n", "\n");
    }

    /// <summary>Escapes a value for <c>set "NAME=value"</c> in a batch file (no delayed expansion): only % needs doubling.</summary>
    public static string CmdValue(string s)
    {
        if (s.Contains('"') || s.Contains('\r') || s.Contains('\n')) throw new ArgumentException("A path for update.cmd cannot contain quotes or line breaks.");
        return s.Replace("%", "%%");
    }

    /// <summary>The update.cmd that swaps a Windows install folder once process <paramref name="pid"/> has quit.</summary>
    public static string CmdScript(int pid, InstallLocation location, string newRoot, string logFile, string dataFolder)
    {
        var lines = new[]
        {
            "@echo off",
            "rem Written by Meridian's updater. Waits for Meridian (pid " + pid + ") to quit, moves the old version aside,",
            "rem moves the new one in (putting the old one back if that fails) and starts Meridian again.",
            "rem The data folder is not touched: " + CmdValue(dataFolder),
            "setlocal DisableDelayedExpansion",
            "cd /d \"%TEMP%\"",
            "set \"PID=" + pid + "\"",
            "set \"APP=" + CmdValue(location.Root) + "\"",
            "set \"NEW=" + CmdValue(newRoot) + "\"",
            "set \"OLD=" + CmdValue(location.OldPath) + "\"",
            "set \"EXE=" + CmdValue(location.Executable) + "\"",
            "set \"LOG=" + CmdValue(logFile) + "\"",
            "set /a N=0",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" /NH 2>nul | find \" %PID% \" >nul",
            "if errorlevel 1 goto gone",
            "set /a N+=1",
            "if %N% GEQ 600 goto timeout",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":timeout",
            "echo [%DATE% %TIME%] update: Meridian (pid %PID%) did not quit; the update was cancelled>>\"%LOG%\"",
            "exit /b 1",
            ":gone",
            "if not exist \"%NEW%\\%EXE%\" goto missing",
            "if exist \"%OLD%\" rmdir /s /q \"%OLD%\"",
            "rem An install made by the setup keeps its uninstaller (Settings > Apps) across a portable update.",
            "if exist \"%APP%\\unins*.*\" copy /y \"%APP%\\unins*.*\" \"%NEW%\\\" >nul 2>&1",
            "set /a T=0",
            ":moveold",
            "move \"%APP%\" \"%OLD%\" >nul 2>&1",
            "if not errorlevel 1 goto movenew",
            "set /a T+=1",
            "if %T% GEQ 15 goto stuck",
            "ping -n 2 127.0.0.1 >nul",
            "goto moveold",
            ":movenew",
            "move \"%NEW%\" \"%APP%\" >nul 2>&1",
            "if errorlevel 1 goto restore",
            "echo [%DATE% %TIME%] update: installed>>\"%LOG%\"",
            "goto relaunch",
            ":restore",
            "echo [%DATE% %TIME%] update: could not move the new version in; restoring the previous one>>\"%LOG%\"",
            "if exist \"%APP%\" rmdir /s /q \"%APP%\"",
            "move \"%OLD%\" \"%APP%\" >nul 2>&1",
            "if errorlevel 1 echo [%DATE% %TIME%] update: RESTORE FAILED: the previous version is in %OLD%>>\"%LOG%\"",
            "goto relaunch",
            ":missing",
            "echo [%DATE% %TIME%] update: the new version is missing; nothing was changed>>\"%LOG%\"",
            "goto relaunch",
            ":stuck",
            "echo [%DATE% %TIME%] update: could not move the old version aside (a file is in use?); nothing was changed>>\"%LOG%\"",
            ":relaunch",
            "start \"\" \"%APP%\\%EXE%\"",
            "exit /b 0",
            "",
        };
        return string.Join("\r\n", lines);
    }

    /// <summary>What the script runs to start Meridian again: the bundle through `open` on macOS, the executable elsewhere.</summary>
    public static string[] RelaunchCommand(InstallLocation location) =>
        location.Os == OSPlatform.OSX ? ["/usr/bin/open", location.Root] : [location.ExecutablePath];

    /// <summary>Writes the swap script into the staging folder and returns its path.</summary>
    public static string WriteScript(InstallLocation location, string newRoot, int pid, string logFile, string dataFolder, string[]? relaunch = null)
    {
        Directory.CreateDirectory(location.StagingPath);
        if (location.Os == OSPlatform.Windows)
        {
            var path = Path.Combine(location.StagingPath, "update.cmd");
            File.WriteAllText(path, CmdScript(pid, location, newRoot, logFile, dataFolder), new UTF8Encoding(false));
            return path;
        }
        var sh = Path.Combine(location.StagingPath, "update.sh");
        File.WriteAllText(sh, ShellScript(pid, location, newRoot, relaunch ?? RelaunchCommand(location), logFile, dataFolder), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>The process that runs a swap script, detached from this one (the script waits for this process to quit).</summary>
    public static ProcessStartInfo ScriptStartInfo(string script, OSPlatform os)
    {
        if (os == OSPlatform.Windows)
            // cmd /s /c ""path"": /s strips the outer quotes, so a path with spaces or parentheses stays one quoted argument.
            return new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + script + "\"\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            };
        return new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false, WorkingDirectory = "/" };
    }
}
