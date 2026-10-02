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

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PrintWindow(nint h, nint hdc, uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint h);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint h, nint dc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint h, nint after, int x, int y, int w, int hh, uint flags);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial int GetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, void* info, uint usage);

    /// <summary>What the editor window shows (its own client area, painted through WM_PRINT), as top-down BGRA pixels.</summary>
    public static byte[] CapturePixels(nint child, out int w, out int h)
    {
        RECT r; GetClientRect(child, &r);   // the editor window itself: its parent frame does not paint grandchildren
        w = r.Right - r.Left; h = r.Bottom - r.Top;
        nint screen = GetDC(0), dc = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        nint old = SelectObject(dc, bmp);
        PrintWindow(child, dc, 0x1 /* PW_CLIENTONLY */);
        var bits = new byte[w * h * 4];
        // BITMAPINFOHEADER, top-down 32 bpp
        var bi = new int[10]; bi[0] = 40; bi[1] = w; bi[2] = -h; bi[3] = 1 | (32 << 16);
        fixed (byte* pb = bits) fixed (int* pi = bi) GetDIBits(dc, bmp, 0, (uint)h, pb, pi, 0);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(0, screen);
        return bits;
    }

    private static nint Pt(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    private const uint WM_MOUSEMOVE_ = 0x0200, WM_LBUTTONDOWN_ = 0x0201, WM_LBUTTONUP_ = 0x0202, WM_LBUTTONDBLCLK_ = 0x0203, WM_MOUSEWHEEL_ = 0x020A;

    public static void MouseMove(nint hwnd, int x, int y) => SendMessage(hwnd, WM_MOUSEMOVE_, 0, Pt(x, y));

    /// <summary>What a click does to a window: the mouse moves there, the button goes down and up.</summary>
    public static void Click(nint hwnd, int x, int y)
    {
        MouseMove(hwnd, x, y);
        SendMessage(hwnd, WM_LBUTTONDOWN_, 1, Pt(x, y));
        SendMessage(hwnd, WM_LBUTTONUP_, 0, Pt(x, y));
    }

    public static void MouseDown(nint hwnd, int x, int y) => SendMessage(hwnd, WM_LBUTTONDOWN_, 1, Pt(x, y));
    public static void MouseUp(nint hwnd, int x, int y) => SendMessage(hwnd, WM_LBUTTONUP_, 0, Pt(x, y));
    public static void RightClick(nint hwnd, int x, int y)
    {
        MouseMove(hwnd, x, y);
        SendMessage(hwnd, 0x0204 /* WM_RBUTTONDOWN */, 2, Pt(x, y));
        SendMessage(hwnd, 0x0205 /* WM_RBUTTONUP */, 0, Pt(x, y));
    }

    public static void DoubleClick(nint hwnd, int x, int y)
    {
        MouseMove(hwnd, x, y);
        SendMessage(hwnd, WM_LBUTTONDOWN_, 1, Pt(x, y));
        SendMessage(hwnd, WM_LBUTTONUP_, 0, Pt(x, y));
        SendMessage(hwnd, WM_LBUTTONDBLCLK_, 1, Pt(x, y));
        SendMessage(hwnd, WM_LBUTTONUP_, 0, Pt(x, y));
    }

    public static void Drag(nint hwnd, int x0, int y0, int x1, int y1)
    {
        MouseMove(hwnd, x0, y0);
        SendMessage(hwnd, WM_LBUTTONDOWN_, 1, Pt(x0, y0));
        for (int i = 1; i <= 6; i++) SendMessage(hwnd, WM_MOUSEMOVE_, 1, Pt(x0 + (x1 - x0) * i / 6, y0 + (y1 - y0) * i / 6));
        SendMessage(hwnd, WM_LBUTTONUP_, 0, Pt(x1, y1));
    }

    /// <summary>One wheel notch per unit (positive = away from the user); at (x, y) when given.</summary>
    public static void Wheel(nint hwnd, int notches, int x = -1, int y = -1)
    {
        if (x >= 0) MouseMove(hwnd, x, y);
        SendMessage(hwnd, WM_MOUSEWHEEL_, (nint)((uint)(ushort)(short)(notches * 120) << 16), 0);
    }

    /// <summary>Resizes the parent frame to hold its child and saves what the window shows as a 32-bit BMP.</summary>
    public static void CaptureWindow(nint hwnd, nint child, int childW, int childH, string path)
    {
        SetWindowPos(hwnd, 0, 40, 40, childW + 16, childH + 39, 0x4 /* SWP_NOZORDER */);
        Pump(300);
        var bits = CapturePixels(child, out int w, out int h);
        using var f = File.Create(path);
        using var wr = new BinaryWriter(f);
        wr.Write((byte)'B'); wr.Write((byte)'M'); wr.Write(54 + bits.Length); wr.Write(0); wr.Write(54);
        wr.Write(40); wr.Write(w); wr.Write(-h); wr.Write((short)1); wr.Write((short)32); wr.Write(0); wr.Write(bits.Length); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0);
        wr.Write(bits);
    }

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

    /// <summary>The editor's buttons in creation order.</summary>
    public static List<nint> Buttons(nint editor) => Children(editor).Where(h => ClassOf(h) == "Button").ToList();

    public static string TextOfControl(nint h) => TextOf(h);

    /// <summary>What clicking a button does: it notifies its parent with WM_COMMAND (id in the low word, BN_CLICKED = 0 in the high word).</summary>
    public static void ClickButton(nint editor, int index) => SendMessage(editor, 0x0111 /* WM_COMMAND */, 1000 + index, 0);

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
