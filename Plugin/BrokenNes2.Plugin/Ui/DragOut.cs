// Round 8 (record): dragging a file OUT of the plugin's window (the recording's MIDI file onto FL's channel rack, playlist or piano roll). The shell makes the data object (SHCreateDataObject over the
// file's item: CF_HDROP and the shell's own formats, what Explorer offers when a file is dragged from it) and runs the drag (SHDoDragDrop with the shell's default drop source). Native AOT clean: no
// managed COM interfaces, the one call into the data object (GetData, to read back what it carries) goes through its vtable.
// Test mode (the self-test): everything but the modal drag loop runs, and what the data object carries is read back the way a drop target reads it (DragQueryFile on its CF_HDROP): the loop itself
// follows the real mouse, so a test cannot drive it without moving the user's cursor.
using System.Runtime.InteropServices;

namespace BrokenNes2.Ui;

internal static unsafe partial class DragOut
{
    [StructLayout(LayoutKind.Sequential)] private struct FORMATETC { public ushort cfFormat; public nint ptd; public uint dwAspect; public int lindex; public uint tymed; }
    [StructLayout(LayoutKind.Sequential)] private struct STGMEDIUM { public uint tymed; public nint handle; public nint pUnkForRelease; }

    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] private static partial void ReleaseStgMedium(STGMEDIUM* medium);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHParseDisplayName(string name, nint bindCtx, out nint pidl, uint sfgaoIn, out uint sfgaoOut);
    [LibraryImport("shell32.dll")] private static partial int SHCreateDataObject(nint pidlFolder, uint cidl, nint* apidl, nint pdtInner, Guid* riid, out nint ppv);
    [LibraryImport("shell32.dll")] private static partial int SHDoDragDrop(nint hwnd, nint pdata, nint pdsrc, uint dwEffect, out uint pdwEffect);
    [LibraryImport("shell32.dll")] private static partial nint ILFindLastID(nint pidl);
    [LibraryImport("shell32.dll")] private static partial nint ILClone(nint pidl);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ILRemoveLastID(nint pidl);
    [LibraryImport("shell32.dll")] private static partial void ILFree(nint pidl);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] private static partial uint DragQueryFile(nint hDrop, uint iFile, char* file, uint cch);

    private const ushort CF_HDROP = 15;
    private const uint DVASPECT_CONTENT = 1, TYMED_HGLOBAL = 1, DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    private const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101;
    private static readonly Guid IidDataObject = new("0000010e-0000-0000-C000-000000000046");

    /// <summary>Tests: run everything but the modal drag loop.</summary>
    public static volatile bool TestMode;
    /// <summary>The last drag: the file asked for, what its data object carried (read back as a drop target reads it), and the outcome in words.</summary>
    public static volatile string LastAsked = "", LastCarried = "", LastResult = "";
    public static int Drags;

    /// <summary>Drags <paramref name="path"/> out of <paramref name="hwnd"/>'s window (on the GUI thread; returns when the button is released). Ok and the words for the window.</summary>
    public static (bool Ok, string Message) DragFile(nint hwnd, string path)
    {
        Interlocked.Increment(ref Drags);
        LastAsked = path; LastCarried = ""; LastResult = "";
        var r = Run(hwnd, path);
        LastResult = r.Message;
        return r;
    }

    private static (bool Ok, string Message) Run(nint hwnd, string path)
    {
        if (!File.Exists(path)) return (false, "the file is gone: " + path);
        int hr = OleInitialize(0);
        if (hr < 0) return (false, $"this window's thread cannot drag (OLE error 0x{hr:X8}): use Save MIDI...");
        hr = SHParseDisplayName(path, 0, out nint pidl, 0, out _);
        if (hr < 0 || pidl == 0) return (false, $"the shell does not know the file (0x{hr:X8}): use Save MIDI...");
        try
        {
            nint parent = ILClone(pidl);
            ILRemoveLastID(parent);
            nint child = ILFindLastID(pidl);
            Guid iid = IidDataObject;
            hr = SHCreateDataObject(parent, 1, &child, 0, &iid, out nint data);
            ILFree(parent);
            if (hr < 0 || data == 0) return (false, $"the shell could not make the drag data (0x{hr:X8}): use Save MIDI...");
            try
            {
                LastCarried = ReadHDrop(data) ?? "";
                if (!string.Equals(LastCarried, path, StringComparison.OrdinalIgnoreCase)) return (false, $"the drag data does not carry the file ({(LastCarried.Length == 0 ? "no file list" : LastCarried)}): use Save MIDI...");
                if (TestMode) return (true, "test: the drag data carries " + Path.GetFileName(path));
                hr = SHDoDragDrop(hwnd, data, 0, DROPEFFECT_COPY | DROPEFFECT_LINK, out uint effect);
                return hr == DRAGDROP_S_DROP ? (true, $"dropped {Path.GetFileName(path)}") : hr == DRAGDROP_S_CANCEL ? (false, "the drag was cancelled") : hr < 0 ? (false, $"the drag failed (0x{hr:X8})") : (true, $"the drag ended ({effect})");
            }
            finally { Release(data); }
        }
        finally { ILFree(pidl); }
    }

    /// <summary>The first file a data object's CF_HDROP lists, read the way a drop target reads it; null when it has none.</summary>
    private static string? ReadHDrop(nint data)
    {
        var fe = new FORMATETC { cfFormat = CF_HDROP, ptd = 0, dwAspect = DVASPECT_CONTENT, lindex = -1, tymed = TYMED_HGLOBAL };
        STGMEDIUM m;
        var getData = (delegate* unmanaged<nint, FORMATETC*, STGMEDIUM*, int>)(*(void***)data)[3];        // IDataObject::GetData
        if (getData(data, &fe, &m) < 0) return null;
        try
        {
            if (m.handle == 0 || DragQueryFile(m.handle, 0xFFFFFFFF, null, 0) == 0) return null;
            char* buf = stackalloc char[1024];
            uint n = DragQueryFile(m.handle, 0, buf, 1024);
            return n == 0 ? null : new string(buf, 0, (int)n);
        }
        finally { ReleaseStgMedium(&m); }
    }

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(void***)unknown)[2])(unknown);
}
