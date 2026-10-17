// The few Win32 calls the tabs need beyond Win32.cs / Win32Gdi.cs (kept here so those shared files stay untouched): resizing the editor window,
// keyboard focus, and the Save dialog.
using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal static unsafe partial class UiNative
{
    public const uint WM_CHAR = 0x0102;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const uint OFN_OVERWRITEPROMPT = 0x2;

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetFocus();
    [LibraryImport("user32.dll")] public static partial nint GetParent(nint hwnd);
    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetSaveFileName(OPENFILENAMEW* ofn);

    /// <summary>The standard Save dialog; <paramref name="filter"/> is pairs separated by '|' as for <see cref="Win32.ChooseFile"/>.</summary>
    public static string? ChooseSaveFile(nint owner, string title, string filter, string defaultExtension)
    {
        char* file = stackalloc char[1024];
        file[0] = '\0';
        string f = filter.Replace('|', '\0') + "\0\0";
        fixed (char* pf = f)
        fixed (char* pt = title)
        fixed (char* pe = defaultExtension)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = owner, lpstrFilter = pf, nFilterIndex = 1,
                lpstrFile = file, nMaxFile = 1024, lpstrTitle = pt, lpstrDefExt = pe,
                Flags = OFN_OVERWRITEPROMPT | Win32.OFN_PATHMUSTEXIST | Win32.OFN_NOCHANGEDIR,
            };
            return GetSaveFileName(&ofn) ? new string(file) : null;
        }
    }
}
