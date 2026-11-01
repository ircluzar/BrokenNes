using System.Runtime.InteropServices;

namespace BrokenNes.Fruity;

[StructLayout(LayoutKind.Sequential)]
public struct TRACKMOUSEEVENT
{
    public uint cbSize, dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}

/// <summary>The GDI and mouse calls a self-drawn editor needs (double-buffered painting, fonts, text, rounded shapes, mouse capture).</summary>
public static unsafe partial class Win32
{
    public const uint CS_DBLCLKS = 0x8;
    public const uint WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A, WM_CAPTURECHANGED = 0x0215,
        WM_MOUSELEAVE = 0x02A3, WM_KEYDOWN = 0x0100, WM_PRINTCLIENT = 0x0318;
    public const uint TME_LEAVE = 0x2;
    public const int TRANSPARENT = 1, NULL_PEN = 8, NULL_BRUSH = 5, PS_SOLID = 0;
    public const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20,
        DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
    public const int FW_NORMAL = 400, FW_SEMIBOLD = 600, FW_BOLD = 700;
    public const nint IDC_ARROW = 32512, IDC_HAND = 32649;

    public static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* e);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW")]
    public static partial int DrawText(nint hdc, char* text, int count, RECT* rect, uint format);

    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(int color);
    [LibraryImport("gdi32.dll")] public static partial nint CreatePen(int style, int width, int color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RoundRect(nint dc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Ellipse(nint dc, int l, int t, int r, int b);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Rectangle(nint dc, int l, int t, int r, int b);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool MoveToEx(nint dc, int x, int y, POINT* old);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool LineTo(nint dc, int x, int y);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial int SaveDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RestoreDC(nint dc, int saved);
    [LibraryImport("gdi32.dll")] public static partial int IntersectClipRect(nint dc, int l, int t, int r, int b);
    [LibraryImport("msimg32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AlphaBlend(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint blendFunction);
    [LibraryImport("gdi32.dll")] public static partial int SetTextColor(nint dc, int color);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    /// <summary>A UI font by pixel height (negative = character height) and weight, ClearType-smoothed.</summary>
    public static nint MakeFont(string face, int pixelHeight, int weight, bool underline = false) =>
        CreateFont(-pixelHeight, 0, 0, 0, weight, 0, underline ? 1u : 0u, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, face);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ShellExecute(nint hwnd, string? operation, string file, string? parameters, string? directory, int show);
}
