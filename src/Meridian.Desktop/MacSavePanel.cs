using System.Runtime.InteropServices;

namespace Meridian.Desktop;

/// <summary>
/// macOS save dialog with the file name filled in. Photino's save dialog only sets the folder, so generated documents
/// came up as "Untitled" and lost their name; this drives NSSavePanel directly through the Objective-C runtime.
/// Must be called on the main (UI) thread.
/// </summary>
internal static class MacSavePanel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long NSModalResponseOK = 1;

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr sel, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr receiver, IntPtr sel);

    /// <summary>Shows the panel modally; returns the chosen path, or null when cancelled.</summary>
    public static string? Show(string title, string folder, string fileName)
    {
        var panel = Send(objc_getClass("NSSavePanel"), Sel("savePanel"));
        if (panel == IntPtr.Zero) throw new InvalidOperationException("NSSavePanel is unavailable.");
        SendPtr(panel, Sel("setTitle:"), Str(title));
        SendPtr(panel, Sel("setNameFieldStringValue:"), Str(fileName));
        if (Directory.Exists(folder)) SendPtr(panel, Sel("setDirectoryURL:"), SendPtr(objc_getClass("NSURL"), Sel("fileURLWithPath:"), Str(folder)));
        SendBool(panel, Sel("setCanCreateDirectories:"), true);
        SendBool(panel, Sel("setExtensionHidden:"), false);
        // Bring the app forward so the panel is not left behind another app's window.
        SendBool(Send(objc_getClass("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), true);
        if (SendLong(panel, Sel("runModal")) != NSModalResponseOK) return null;
        var url = Send(panel, Sel("URL"));
        return url == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(Send(url, Sel("path")), Sel("UTF8String")));
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr Str(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return SendPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }
}
