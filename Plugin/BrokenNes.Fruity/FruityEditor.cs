// Generic Win32 editor built from a plugin's parameter table: one slider per parameter plus a live
// readout. Slider moves are reported to FL (OnParamChanged) so it can record automation, and a
// right-click shows FL's own parameter menu (FHD_GetParamMenuEntry / FHD_ParamMenu). Everything
// that touches controls runs on the GUI thread, from a 30 ms timer.
using System.Runtime.InteropServices;

namespace BrokenNes.Fruity;

public sealed unsafe class FruityEditor
{
    private const string ClassName = "BrokenNesFruityEditor";
    private const int Width = 520, RowHeight = 30, Margin = 10, ReadoutHeight = 190;
    private const nuint TimerId = 1;

    private static bool registered;

    private readonly FruityPluginBase plugin;
    private readonly nint[] sliders;
    private readonly nint[] valueLabels;
    private readonly int[] shown;
    private readonly nint readout;
    private readonly GCHandle self;
    private string lastReadout = "";

    public nint Hwnd { get; }

    public FruityEditor(FruityPluginBase plugin, nint parent)
    {
        this.plugin = plugin;
        int n = plugin.Params.Length;
        sliders = new nint[n]; valueLabels = new nint[n]; shown = new int[n];

        var module = OwnModule();
        EnsureClass(module);

        int height = Margin * 2 + n * RowHeight + ReadoutHeight;
        Hwnd = Win32.CreateWindowEx(0, ClassName, "BrokenNes", Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN,
            0, 0, Width, height, parent, 0, module, 0);
        self = GCHandle.Alloc(this);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWLP_USERDATA, GCHandle.ToIntPtr(self));

        var font = Win32.GetStockObject(Win32.DEFAULT_GUI_FONT);
        for (int i = 0; i < n; i++)
        {
            var def = plugin.Params[i];
            int y = Margin + i * RowHeight;
            Child("STATIC", def.Name, Win32.SS_LEFT, Margin, y + 4, 90, 20, module, font);
            sliders[i] = Child("msctls_trackbar32", "", Win32.TBS_HORZ | Win32.TBS_NOTICKS, 105, y, 270, 26, module, font);
            Win32.SendMessage(sliders[i], Win32.TBM_SETRANGEMIN, 0, def.Min);
            Win32.SendMessage(sliders[i], Win32.TBM_SETRANGEMAX, 1, def.Max);
            valueLabels[i] = Child("STATIC", "", Win32.SS_LEFT, 385, y + 4, 125, 20, module, font);
            shown[i] = int.MinValue;
        }
        readout = Child("STATIC", "", Win32.SS_LEFT, Margin, Margin + n * RowHeight + 6, Width - Margin * 2, ReadoutHeight - 10, module, font);

        Refresh();
        Win32.SetTimer(Hwnd, TimerId, 30, 0);
    }

    public void Destroy()
    {
        Win32.KillTimer(Hwnd, TimerId);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWLP_USERDATA, 0);
        Win32.DestroyWindow(Hwnd);
        self.Free();
    }

    private nint Child(string cls, string text, uint style, int x, int y, int w, int h, nint module, nint font)
    {
        var hwnd = Win32.CreateWindowEx(0, cls, text, Win32.WS_CHILD | Win32.WS_VISIBLE | style, x, y, w, h, Hwnd, 0, module, 0);
        Win32.SendMessage(hwnd, Win32.WM_SETFONT, font, 0);
        return hwnd;
    }

    private int SliderIndex(nint hwnd) => Array.IndexOf(sliders, hwnd);

    private void Refresh()
    {
        for (int i = 0; i < plugin.Params.Length; i++)
        {
            int v = plugin.Get(i);
            if (v == shown[i]) continue;
            shown[i] = v;
            if (Win32.SendMessage(sliders[i], Win32.TBM_GETPOS, 0, 0) != v)
                Win32.SendMessage(sliders[i], Win32.TBM_SETPOS, 1, v);
            Win32.SetWindowText(valueLabels[i], plugin.Params[i].Format(v));
        }
        var text = plugin.GetReadout().Replace("\n", "\r\n");
        if (text != lastReadout)
        {
            lastReadout = text;
            Win32.SetWindowText(readout, text);
        }
    }

    private void OnSlider(nint slider)
    {
        int i = SliderIndex(slider);
        if (i < 0) return;
        plugin.SetFromUi(i, (int)Win32.SendMessage(slider, Win32.TBM_GETPOS, 0, 0));
        Refresh();
    }

    private void OnContextMenu(nint control, nint lParam)
    {
        int index = SliderIndex(control);
        if (index < 0) return;

        var menu = Win32.CreatePopupMenu();
        for (int item = 0; ; item++)
        {
            var entry = (TParamMenuEntry*)plugin.Host.Dispatcher(plugin.HostTag, Fhd.GetParamMenuEntry, index, item);
            if (entry == null || entry->Name == null) break;
            uint flags = entry->Name[0] == (byte)'-' && entry->Name[1] == 0 ? Win32.MF_SEPARATOR : Win32.MF_STRING;
            if ((entry->Flags & Fhp.Disabled) != 0) flags |= Win32.MF_GRAYED;
            if ((entry->Flags & Fhp.Checked) != 0) flags |= Win32.MF_CHECKED;
            Win32.AppendMenu(menu, flags, (nuint)(item + 1), entry->Name);
        }

        POINT pt;
        int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        if (lParam == -1) { Win32.GetCursorPos(&pt); x = pt.X; y = pt.Y; }

        int chosen = Win32.TrackPopupMenu(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, x, y, 0, Hwnd, 0);
        Win32.DestroyMenu(menu);
        if (chosen > 0)
            plugin.Host.Dispatcher(plugin.HostTag, Fhd.ParamMenu, index, chosen - 1);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var handle = Win32.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
        if (handle != 0 && GCHandle.FromIntPtr(handle).Target is FruityEditor editor)
        {
            try
            {
                switch (msg)
                {
                    case Win32.WM_HSCROLL when lParam != 0:
                        editor.OnSlider(lParam);
                        return 0;
                    case Win32.WM_TIMER:
                        editor.Refresh();
                        return 0;
                    case Win32.WM_CONTEXTMENU:
                        editor.OnContextMenu(wParam, lParam);
                        return 0;
                }
            }
            catch (Exception e)
            {
                Diag.Log($"Editor exception: {e}");
            }
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void EnsureClass(nint module)
    {
        if (registered) return;

        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = Win32.ICC_BAR_CLASSES };
        Win32.InitCommonControlsEx(&icc);

        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = module,
                hCursor = Win32.LoadCursor(0, 32512), // IDC_ARROW
                hbrBackground = Win32.COLOR_BTNFACE + 1,
                lpszClassName = name,
            };
            Win32.RegisterClassEx(&wc);
        }
        registered = true;
    }

    /// <summary>The module (the plugin DLL) that contains this window procedure.</summary>
    private static nint OwnModule()
    {
        nint module;
        var addr = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc;
        Win32.GetModuleHandleEx(Win32.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Win32.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, addr, &module);
        return module;
    }
}
