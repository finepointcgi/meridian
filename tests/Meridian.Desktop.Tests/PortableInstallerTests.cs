using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Meridian.Desktop.Tests;

/// <summary>Portable updates: install detection, writability, safe unpacking, and the swap scripts (run only inside a temp folder).</summary>
public sealed class PortableInstallerTests : IDisposable
{
    // A space and a quote in the path: the scripts must quote both.
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian portable 'test' ");

    public void Dispose()
    {
        foreach (var d in _dir.EnumerateDirectories("*", SearchOption.AllDirectories))
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (IOException) { }
        try { _dir.Delete(true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string P(params string[] parts) => Path.Combine([_dir.FullName, .. parts]);

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A published, self-contained install (the CoreLib marker) for an OS.</summary>
    private InstallLocation FakeInstall(OSPlatform os, string version = "old")
    {
        string exeDir, root;
        if (os == OSPlatform.OSX) { root = P("Applications", "Meridian.app"); exeDir = Path.Combine(root, "Contents", "MacOS"); Touch(Path.Combine(exeDir, "Meridian"), version); }
        else if (os == OSPlatform.Windows) { root = P("Programs", "Meridian"); exeDir = root; Touch(Path.Combine(exeDir, "Meridian.exe"), version); }
        else { root = P("opt", "meridian-0.5.0"); exeDir = root; Touch(Path.Combine(exeDir, "Meridian"), version); }
        Touch(Path.Combine(exeDir, "System.Private.CoreLib.dll"));
        Touch(Path.Combine(exeDir, "version.txt"), version);
        return InstallLocation.Detect(exeDir, os)!;
    }

    [Fact]
    public void Detects_each_install_layout_and_ignores_development_builds()
    {
        var mac = FakeInstall(OSPlatform.OSX);
        Assert.Equal(P("Applications", "Meridian.app"), mac.Root);
        Assert.Equal("Contents/MacOS/Meridian", mac.Executable);
        Assert.Equal(P("Applications", ".Meridian.app.meridian-update"), mac.StagingPath);
        Assert.Equal(P("Applications", ".Meridian.app.meridian-old"), mac.OldPath);

        var win = FakeInstall(OSPlatform.Windows);
        Assert.Equal(P("Programs", "Meridian"), win.Root);
        Assert.False(win.HasInstaller);
        Touch(Path.Combine(win.Root, "unins000.exe"));
        Assert.True(InstallLocation.Detect(win.Root, OSPlatform.Windows)!.HasInstaller);

        var linux = FakeInstall(OSPlatform.Linux);
        Assert.Equal("Meridian", linux.Executable);

        // dotnet run / bin/Debug: framework-dependent, no CoreLib next to the app.
        var dev = P("bin", "Debug", "net10.0");
        Touch(Path.Combine(dev, "Meridian"));
        Assert.Null(InstallLocation.Detect(dev, OSPlatform.Linux));
        // A self-contained build that is not inside an .app bundle on macOS.
        Touch(Path.Combine(dev, "System.Private.CoreLib.dll"));
        Assert.Null(InstallLocation.Detect(dev, OSPlatform.OSX));
    }

    [Fact]
    public void Writability_and_the_data_folder_decide_whether_a_swap_is_possible()
    {
        var install = FakeInstall(OSPlatform.Linux);
        Assert.Null(PortableInstaller.CheckWritable(install, P("data")));
        Assert.Contains("inside the app's folder", PortableInstaller.CheckWritable(install, Path.Combine(install.Root, "data")));
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(install.Parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // like /Applications or /opt without rights
        try
        {
            var problem = PortableInstaller.CheckWritable(install, P("data"));
            Assert.NotNull(problem);
            Assert.Contains("no permission", problem);
        }
        finally { File.SetUnixFileMode(install.Parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // ------------------------------------------------------------------ archives

    private string Zip(string name, params (string Path, string Content, int Mode)[] entries)
    {
        var path = P("downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content, mode) in entries)
        {
            var e = zip.CreateEntry(entryPath);
            if (mode != 0) e.ExternalAttributes = mode << 16;
            if (!entryPath.EndsWith('/')) using (var s = e.Open()) s.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private string TarGz(string name, Action<TarWriter> write)
    {
        var path = P("downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
            write(tar);
        return path;
    }

    private static void AddFile(TarWriter tar, string name, string content, UnixFileMode mode = (UnixFileMode)0b110_100_100)
    {
        var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };
        tar.WriteEntry(e);
    }

    private const int Exec = 0b1000_000_111_101_101;   // regular file, rwxr-xr-x
    private const int Plain = 0b1000_000_110_100_100;  // regular file, rw-r--r--

    [Fact]
    public async Task Stages_a_macos_zip_without_apple_metadata()
    {
        var install = FakeInstall(OSPlatform.OSX);
        var archive = Zip("Meridian-0.6.0-macos-arm64.zip",
            ("Meridian.app/", "", 0x4000 << 0 | 0b100_000_111_101_101),
            ("Meridian.app/Contents/Info.plist", "<plist/>", Plain),
            ("Meridian.app/Contents/MacOS/Meridian", "new", Exec),
            ("Meridian.app/Contents/MacOS/._Meridian", "resource fork", Plain),
            ("__MACOSX/Meridian.app/._Info.plist", "metadata", Plain));
        var staged = await PortableInstaller.StageAsync(archive, install);
        Assert.Equal(Path.Combine(install.StagingPath, "new"), staged);
        Assert.Equal("new", File.ReadAllText(Path.Combine(staged, "Contents", "MacOS", "Meridian")));
        Assert.False(File.Exists(Path.Combine(staged, "Contents", "MacOS", "._Meridian")));
        Assert.False(Directory.Exists(Path.Combine(install.StagingPath, "__MACOSX")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(staged, "Contents", "MacOS", "Meridian")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install.Root, "Contents", "MacOS", "version.txt")));   // the install is untouched
    }

    [Fact]
    public async Task Stages_a_windows_zip_and_a_linux_tar_gz()
    {
        var win = FakeInstall(OSPlatform.Windows);
        var zip = Zip("Meridian-0.6.0-windows-x64.zip", ("Meridian/Meridian.exe", "new exe", 0), ("Meridian/wwwroot/app.css", "css", 0), ("Meridian/README.txt", "readme", 0));
        var stagedWin = await PortableInstaller.StageAsync(zip, win);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(stagedWin, "Meridian.exe")));
        Assert.True(File.Exists(Path.Combine(stagedWin, "wwwroot", "app.css")));

        var linux = FakeInstall(OSPlatform.Linux);
        var tgz = TarGz("Meridian-0.6.0-linux-x64.tar.gz", tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "meridian-0.6.0/") { Mode = (UnixFileMode)0b111_101_101 });
            AddFile(tar, "meridian-0.6.0/Meridian", "new elf", (UnixFileMode)0b111_101_101);
            AddFile(tar, "meridian-0.6.0/install.sh", "#!/bin/sh", (UnixFileMode)0b111_101_101);
            AddFile(tar, "meridian-0.6.0/wwwroot/app.css", "css");
        });
        var stagedLinux = await PortableInstaller.StageAsync(tgz, linux);
        Assert.Equal("new elf", File.ReadAllText(Path.Combine(stagedLinux, "Meridian")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(stagedLinux, "install.sh")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(stagedLinux, "wwwroot", "app.css")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Theory]
    [InlineData("Meridian/../../escape.txt")]
    [InlineData("/etc/escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("Meridian/..\\..\\escape.txt")]
    public async Task Rejects_path_traversal_in_zips(string evil)
    {
        var win = FakeInstall(OSPlatform.Windows);
        var zip = Zip("evil.zip", ("Meridian/Meridian.exe", "exe", 0), (evil, "pwned", 0));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(zip, win));
        Assert.Contains("unsafe path", ex.Message);
        Assert.False(File.Exists(P("escape.txt")));
        Assert.False(File.Exists(P("Programs", "escape.txt")));
        Assert.False(Directory.Exists(win.StagingPath));   // a failed staging is removed
    }

    [Fact]
    public async Task Rejects_links_extra_top_level_folders_and_oversized_archives()
    {
        var linux = FakeInstall(OSPlatform.Linux);
        var link = TarGz("link.tar.gz", tar =>
        {
            AddFile(tar, "meridian-0.6.0/Meridian", "elf");
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "meridian-0.6.0/passwd") { LinkName = "/etc/passwd" });
        });
        Assert.Contains("link", (await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(link, linux))).Message);

        var traversal = TarGz("traversal.tar.gz", tar => AddFile(tar, "meridian-0.6.0/../../escape", "x"));
        Assert.Contains("unsafe path", (await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(traversal, linux))).Message);

        var two = TarGz("two.tar.gz", tar => { AddFile(tar, "meridian-0.6.0/Meridian", "elf"); AddFile(tar, "other/file", "x"); });
        Assert.Contains("single top-level folder", (await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(two, linux))).Message);

        var big = TarGz("big.tar.gz", tar => AddFile(tar, "meridian-0.6.0/Meridian", new string('a', 5000)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(big, linux, new ExtractLimits { MaxFileBytes = 1000 }));
        var many = TarGz("many.tar.gz", tar => { for (var i = 0; i < 20; i++) AddFile(tar, $"meridian-0.6.0/f{i}", "x"); });
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(many, linux, new ExtractLimits { MaxEntries = 10 }));
        var total = TarGz("total.tar.gz", tar => { for (var i = 0; i < 5; i++) AddFile(tar, $"meridian-0.6.0/f{i}", new string('b', 400)); });
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(total, linux, new ExtractLimits { MaxTotalBytes = 1000 }));

        var mac = FakeInstall(OSPlatform.OSX);
        var zipLink = Zip("link.zip", ("Meridian.app/Contents/MacOS/Meridian", "x", Exec), ("Meridian.app/Contents/evil", "/etc/passwd", 0b1010_000_111_111_111));
        Assert.Contains("symbolic link", (await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(zipLink, mac))).Message);

        Assert.Equal("old", File.ReadAllText(Path.Combine(linux.Root, "version.txt")));
    }

    [Fact]
    public async Task Rejects_a_package_for_another_os()
    {
        var mac = FakeInstall(OSPlatform.OSX);
        var windowsZip = Zip("Meridian-0.6.0-windows-x64.zip", ("Meridian/Meridian.exe", "exe", 0));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => PortableInstaller.StageAsync(windowsZip, mac));
        Assert.Contains("Contents/MacOS/Meridian", ex.Message);
        Assert.False(Directory.Exists(mac.StagingPath));
    }

    // ------------------------------------------------------------------ scripts

    [Fact]
    public void The_shell_script_quotes_every_path()
    {
        var mac = FakeInstall(OSPlatform.OSX);
        var script = PortableInstaller.ShellScript(4242, mac, Path.Combine(mac.StagingPath, "new"), PortableInstaller.RelaunchCommand(mac), P("data dir", "desktop.log"), P("data dir"));
        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.DoesNotContain("\r", script);
        Assert.Contains("PID=4242\n", script);
        Assert.Contains("APP=" + PortableInstaller.ShQuote(mac.Root) + "\n", script);
        Assert.Contains("OLD=" + PortableInstaller.ShQuote(mac.OldPath) + "\n", script);
        Assert.Contains("nohup '/usr/bin/open' " + PortableInstaller.ShQuote(mac.Root), script);
        Assert.Contains("'\\''test'\\''", script);   // the quote in the temp folder name
        Assert.Contains("kill -0 \"$PID\"", script);
        Assert.Contains("mv \"$OLD\" \"$APP\"", script);   // restore on failure
        Assert.Equal("'a b'", PortableInstaller.ShQuote("a b"));
        Assert.Equal("'$(rm -rf ~)'", PortableInstaller.ShQuote("$(rm -rf ~)"));
        Assert.Equal("'it'\\''s'", PortableInstaller.ShQuote("it's"));
        var linux = FakeInstall(OSPlatform.Linux);
        Assert.Equal([Path.Combine(linux.Root, "Meridian")], PortableInstaller.RelaunchCommand(linux));
    }

    [Fact]
    public void The_cmd_script_quotes_paths_and_escapes_percent_signs()
    {
        var win = new InstallLocation(@"C:\Users\Ann Lee\AppData\Local\Programs (x86)\Meridian 100%", "Meridian.exe", OSPlatform.Windows);
        var script = PortableInstaller.CmdScript(4242, win, win.StagingPath + @"\new", @"C:\Users\Ann Lee\AppData\Roaming\Meridian\desktop.log", @"C:\Users\Ann Lee\AppData\Roaming\Meridian");
        Assert.StartsWith("@echo off\r\n", script);
        Assert.DoesNotContain("\n", script.Replace("\r\n", ""));   // CRLF only
        Assert.Contains("setlocal DisableDelayedExpansion", script);
        Assert.Contains("set \"PID=4242\"", script);
        Assert.Contains(@"set ""APP=C:\Users\Ann Lee\AppData\Local\Programs (x86)\Meridian 100%%""", script);
        Assert.Contains("tasklist /FI \"PID eq %PID%\"", script);
        Assert.Contains("move \"%APP%\" \"%OLD%\"", script);
        Assert.Contains("move \"%OLD%\" \"%APP%\"", script);   // restore on failure
        Assert.Contains("start \"\" \"%APP%\\%EXE%\"", script);
        Assert.Throws<ArgumentException>(() => PortableInstaller.CmdValue("C:\\a\"b"));

        var start = PortableInstaller.ScriptStartInfo(@"C:\Users\Ann Lee\.Meridian.meridian-update\update.cmd", OSPlatform.Windows);
        Assert.Equal("cmd.exe", start.FileName);
        Assert.Equal(@"/d /s /c """"C:\Users\Ann Lee\.Meridian.meridian-update\update.cmd""""", start.Arguments);
        Assert.True(start.CreateNoWindow);
        var sh = PortableInstaller.ScriptStartInfo("/tmp/a b/update.sh", OSPlatform.OSX);
        Assert.Equal("/bin/sh", sh.FileName);
        Assert.Equal(["/tmp/a b/update.sh"], sh.ArgumentList);
    }

    /// <summary>Runs update.sh for real, in the temp folder only: waits for a process, swaps the bundle, keeps data, relaunches.</summary>
    [Fact]
    public async Task The_shell_script_swaps_the_install_after_the_process_quits()
    {
        if (OperatingSystem.IsWindows()) return;   // update.cmd is only checked for content here
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        var install = FakeInstall(os, "old");
        var data = P("user data");
        Touch(Path.Combine(data, "meridian.db"), "models");
        var archive = os == OSPlatform.OSX
            ? Zip("new.zip", ("Meridian.app/Contents/MacOS/Meridian", "new", Exec), ("Meridian.app/Contents/MacOS/version.txt", "new", Plain))
            : Zip("new.zip", ("meridian-0.6.0/Meridian", "new", Exec), ("meridian-0.6.0/version.txt", "new", Plain));
        var staged = await PortableInstaller.StageAsync(archive, install);

        using var waitFor = Process.Start(new ProcessStartInfo("/bin/sleep", ["1"]))!;   // stands in for the running Meridian
        var marker = P("relaunched");
        var log = Path.Combine(data, "desktop.log");
        var script = PortableInstaller.WriteScript(install, staged, waitFor.Id, log, data, ["/usr/bin/touch", marker]);
        Assert.Equal(Path.Combine(install.StagingPath, "update.sh"), script);

        using (var run = Process.Start(PortableInstaller.ScriptStartInfo(script, os))!)
        {
            Assert.True(run.WaitForExit(30_000));
            Assert.Equal(0, run.ExitCode);
        }
        Assert.True(waitFor.HasExited);   // the script waited for it
        var versionFile = os == OSPlatform.OSX ? Path.Combine(install.Root, "Contents", "MacOS", "version.txt") : Path.Combine(install.Root, "version.txt");
        Assert.Equal("new", File.ReadAllText(versionFile));
        Assert.True(Directory.Exists(install.OldPath));
        Assert.Equal("models", File.ReadAllText(Path.Combine(data, "meridian.db")));   // data untouched
        Assert.Contains("update: installed", File.ReadAllText(log));
        await WaitForFile(marker);

        PortableInstaller.CleanupLeftovers(install);
        Assert.False(Directory.Exists(install.OldPath));
        Assert.False(Directory.Exists(install.StagingPath));
        Assert.True(Directory.Exists(install.Root));
    }

    [Fact]
    public async Task The_shell_script_restores_the_old_install_when_the_new_one_cannot_move_in()
    {
        if (OperatingSystem.IsWindows()) return;
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        var install = FakeInstall(os, "old");
        var versionFile = os == OSPlatform.OSX ? Path.Combine(install.Root, "Contents", "MacOS", "version.txt") : Path.Combine(install.Root, "version.txt");
        var log = P("desktop.log");
        var staged = Path.Combine(install.StagingPath, "new");
        Directory.CreateDirectory(Path.Combine(staged, "Contents", "MacOS"));
        var script = PortableInstaller.WriteScript(install, staged, 999_999_9, log, P("data"), ["/usr/bin/touch", P("relaunched-1")]);
        // The staging folder is read-only: the old install moves aside, then moving the new one out of staging fails.
        File.SetUnixFileMode(install.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var run = Process.Start(PortableInstaller.ScriptStartInfo(script, os))!;
            Assert.True(run.WaitForExit(30_000));
            Assert.Equal(1, run.ExitCode);
        }
        finally { File.SetUnixFileMode(install.StagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal("old", File.ReadAllText(versionFile));
        Assert.False(Directory.Exists(install.OldPath));
        Assert.Contains("restoring the previous one", File.ReadAllText(log));
        await WaitForFile(P("relaunched-1"));

        // No new version at all: nothing is moved.
        Directory.Delete(staged, true);
        script = PortableInstaller.WriteScript(install, staged, 999_999_9, log, P("data"), ["/usr/bin/touch", P("relaunched-2")]);
        using (var run = Process.Start(PortableInstaller.ScriptStartInfo(script, os))!)
        {
            Assert.True(run.WaitForExit(30_000));
            Assert.Equal(1, run.ExitCode);
        }
        Assert.Equal("old", File.ReadAllText(versionFile));
        Assert.Contains("nothing was changed", File.ReadAllText(log));
        await WaitForFile(P("relaunched-2"));
    }

    private static async Task WaitForFile(string path)
    {
        for (var i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(50);
        Assert.True(File.Exists(path), "relaunch command did not run");
    }
}
