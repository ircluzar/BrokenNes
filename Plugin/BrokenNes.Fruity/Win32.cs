using System.Runtime.InteropServices;

namespace BrokenNes.Fruity;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public void* lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct INITCOMMONCONTROLSEX
{
    public uint dwSize;
    public uint dwICC;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore, fIncUpdate;
    public fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth, biHeight;
    public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public uint biClrUsed, biClrImportant;
}

/// <summary>The x64 layout of OPENFILENAMEW (comdlg32).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct OPENFILENAMEW
{
    public uint lStructSize;
    public nint hwndOwner, hInstance;
    public char* lpstrFilter, lpstrCustomFilter;
    public uint nMaxCustFilter, nFilterIndex;
    public char* lpstrFile;
    public uint nMaxFile;
    public char* lpstrFileTitle;
    public uint nMaxFileTitle;
    public char* lpstrInitialDir, lpstrTitle;
    public uint Flags;
    public ushort nFileOffset, nFileExtension;
    public char* lpstrDefExt;
    public nint lCustData, lpfnHook;
    public char* lpTemplateName;
    public nint pvReserved;
    public uint dwReserved, FlagsEx;
}

public static unsafe partial class Win32
{
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint SS_LEFT = 0x0;
    public const uint SS_RIGHT = 0x2;
    public const uint TBS_HORZ = 0x0;
    public const uint TBS_NOTICKS = 0x10;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_HSCROLL = 0x0114;

    public const uint TBM_GETPOS = 0x0400;
    public const uint TBM_SETPOS = 0x0405;
    public const uint TBM_SETRANGEMIN = 0x0407;
    public const uint TBM_SETRANGEMAX = 0x0408;

    public const uint MF_STRING = 0x0;
    public const uint MF_GRAYED = 0x1;
    public const uint MF_CHECKED = 0x8;
    public const uint MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2;
    public const uint TPM_RETURNCMD = 0x100;

    public const uint WM_PAINT = 0x000F;
    public const uint WM_COMMAND = 0x0111;
    public const uint BS_PUSHBUTTON = 0x0;
    public const uint OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x800, OFN_NOCHANGEDIR = 0x8;
    public const uint SRCCOPY = 0x00CC0020;
    public const int BLACK_BRUSH = 4;
    public const int COLORONCOLOR = 3;
    public const int GWLP_USERDATA = -21;
    public const int COLOR_BTNFACE = 15;
    public const int DEFAULT_GUI_FONT = 17;
    public const uint ICC_BAR_CLASSES = 0x4;
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(nint hwnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuA")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nuint id, byte* text);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* point);

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int obj);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, nint* module);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(INITCOMMONCONTROLSEX* icc);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hdc, RECT* rect, nint brush);

    [LibraryImport("gdi32.dll")]
    public static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    public static partial int StretchDIBits(nint hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc,
        void* bits, BITMAPINFOHEADER* info, uint usage, uint rop);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOpenFileName(OPENFILENAMEW* ofn);

    /// <summary>Shows the standard Open dialog; <paramref name="filter"/> is pairs separated by '|' (e.g. "NES ROMs|*.nes|All files|*.*").</summary>
    public static string? ChooseFile(nint owner, string title, string filter)
    {
        char* file = stackalloc char[1024];
        file[0] = '\0';
        string f = filter.Replace('|', '\0') + "\0\0";
        fixed (char* pf = f)
        fixed (char* pt = title)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = owner, lpstrFilter = pf, nFilterIndex = 1,
                lpstrFile = file, nMaxFile = 1024, lpstrTitle = pt, Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            return GetOpenFileName(&ofn) ? new string(file) : null;
        }
    }
}
