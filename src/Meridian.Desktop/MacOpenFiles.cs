using System.Runtime.InteropServices;

namespace Meridian.Desktop;

/// <summary>
/// macOS delivers "open this document" (Finder double-click, Open With, dropping on the Dock icon) as an Apple Event, not as a
/// command-line argument. Photino does not expose it, so this adds <c>application:openFiles:</c> to Photino's NSApplication
/// delegate class through the Objective-C runtime; AppKit then routes those events here.
/// </summary>
internal static unsafe class MacOpenFiles
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendNUInt(IntPtr receiver, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr receiver, IntPtr sel, nuint index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNUInt(IntPtr receiver, IntPtr sel, nuint value);

    private static Action<string[]>? _handler;

    /// <summary>Registers the handler. Call after the Photino window object exists and again once the window is created.</summary>
    public static void Install(Action<string[]> handler)
    {
        if (!OperatingSystem.IsMacOS()) return;
        _handler = handler;
        try
        {
            NativeLibrary.TryLoad("Photino.Native", typeof(Photino.NET.PhotinoWindow).Assembly, null, out _);
            AddTo(objc_getClass("AppDelegate"));
            // Whatever delegate NSApp actually has (Photino's AppDelegate once registered).
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            var del = app == IntPtr.Zero ? IntPtr.Zero : Send(app, sel_registerName("delegate"));
            if (del != IntPtr.Zero) AddTo(object_getClass(del));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Program.Log($"Open-file events unavailable: {ex.Message}");
        }
    }

    private static void AddTo(IntPtr cls)
    {
        if (cls == IntPtr.Zero) return;
        // Returns false when the class already has the method (installed earlier); that is fine.
        class_addMethod(cls, sel_registerName("application:openFiles:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OpenFiles, "v@:@@");
    }

    [UnmanagedCallersOnly]
    private static void OpenFiles(IntPtr self, IntPtr cmd, IntPtr app, IntPtr files)
    {
        try
        {
            var paths = new List<string>();
            var count = SendNUInt(files, sel_registerName("count"));
            for (nuint i = 0; i < count; i++)
            {
                var s = SendIndex(files, sel_registerName("objectAtIndex:"), i);
                if (Marshal.PtrToStringUTF8(Send(s, sel_registerName("UTF8String"))) is { Length: > 0 } p) paths.Add(p);
            }
            SendVoidNUInt(app, sel_registerName("replyToOpenOrPrint:"), 0); // NSApplicationDelegateReplySuccess
            if (paths.Count > 0) _handler?.Invoke(paths.ToArray());
        }
        catch (Exception ex)
        {
            Program.Log("Open-file event failed: " + ex);
        }
    }
}
