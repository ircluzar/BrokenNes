using System.Runtime.InteropServices;
using System.Text;

namespace BrokenNes.FruityHost;

/// <summary>The little of Win32 the editor test needs: a parent window like FL's plugin frame, and ways to look at
/// and operate the plugin's editor controls.</summary>
internal static unsafe partial class Win32Host
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] private static partial nint SendMessage(nint h, uint msg, nint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessage(MSG* m, nint h, uint min, uint max, uint remove);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static partial nint DispatchMessage(MSG* m);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetWindowText(nint h, char* buf, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassName(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> proc, nint lParam);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WM_HSCROLL = 0x0114, TBM_GETPOS = 0x0400, TBM_SETPOS = 0x0405, PM_REMOVE = 1;

    public static nint CreateParent() => CreateWindowEx(0, "STATIC", "FruityHost plugin frame", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 620, 520, 0, 0, 0, 0);
    public static void DestroyParent(nint h) => DestroyWindow(h);

    /// <summary>Process this thread's messages for a while (the editor's refresh timer needs the GUI thread's message loop).</summary>
    public static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        MSG m;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessage(&m, 0, 0, 0, PM_REMOVE)) DispatchMessage(&m);
            Thread.Sleep(5);
        }
    }

    private static readonly List<nint> Collected = new();

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam) { Collected.Add(hwnd); return 1; }

    private static List<nint> Children(nint parent)
    {
        Collected.Clear();
        EnumChildWindows(parent, &Collect, 0);
        return Collected.ToList();
    }

    private static string ClassOf(nint h) { char* b = stackalloc char[64]; int n = GetClassName(h, b, 64); return new string(b, 0, n); }
    private static string TextOf(nint h) { char* b = stackalloc char[2048]; int n = GetWindowText(h, b, 2048); return new string(b, 0, n); }

    /// <summary>The editor's sliders in creation order (= parameter order).</summary>
    public static List<nint> Sliders(nint editor) => Children(editor).Where(h => ClassOf(h) == "msctls_trackbar32").ToList();

    public static int SliderPos(nint slider) => (int)SendMessage(slider, TBM_GETPOS, 0, 0);

    /// <summary>What dragging a slider does: the position changes, then the control notifies its parent with WM_HSCROLL.</summary>
    public static void DragSlider(nint editor, nint slider, int pos)
    {
        SendMessage(slider, TBM_SETPOS, 1, pos);
        SendMessage(editor, WM_HSCROLL, 8 /* TB_ENDTRACK */, slider);
    }

    public static string AllText(nint editor)
    {
        var sb = new StringBuilder();
        foreach (var h in Children(editor)) if (ClassOf(h) == "Static") sb.AppendLine(TextOf(h));
        return sb.ToString();
    }
}
