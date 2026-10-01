using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes2;

/// <summary>The one function FL looks for in a native plugin DLL.</summary>
public static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "CreatePlugInstance")]
    public static NativePlug* CreatePlugInstance(nint host, nint tag) =>
        FruityNative.Create(host, tag, Bn2Plugin.Info, P.Count, (h, t) => new Bn2Plugin(h, t));
}
