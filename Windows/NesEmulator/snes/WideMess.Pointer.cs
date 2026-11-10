using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>What kind of thing is pointing (docs/projects/widemess-spec.md section 10): WMPTRST bits 6-7.</summary>
public enum WmsPointerDevice : byte { Mouse = 0, Touch = 1, Pen = 2, Agent = 3 }

/// <summary>
/// The pointer port's input for one frame (spec section 10): absolute X / Y in console pixels (0-399, 0-223), present (over the picture), the buttons, the device.
/// A pointer that is not present keeps its last X / Y and holds no buttons (<see cref="Normalized"/>). Packs into 32 bits for recordings (<see cref="Pack"/>).
/// </summary>
public readonly record struct WmsPointer(int X, int Y, bool Present, bool Left, bool Right, WmsPointerDevice Device)
{
    public const int Width = 400, Height = 224;
    /// <summary>Not present, at 0,0: the port at power-on and when no host feeds it.</summary>
    public static readonly WmsPointer Idle = default;

    public bool IsIdle => this == Idle;

    /// <summary>X / Y clamped to the picture; no buttons while not present.</summary>
    public WmsPointer Normalized() => new(Math.Clamp(X, 0, Width - 1), Math.Clamp(Y, 0, Height - 1), Present, Present && Left, Present && Right, (WmsPointerDevice)((int)Device & 3));

    /// <summary>bits 0-8 X, 9-16 Y, 17 present, 18 left, 19 right, 20-21 device.</summary>
    public uint Pack()
    {
        var p = Normalized();
        return (uint)p.X | (uint)p.Y << 9 | (p.Present ? 1u << 17 : 0) | (p.Left ? 1u << 18 : 0) | (p.Right ? 1u << 19 : 0) | (uint)p.Device << 20;
    }

    public static WmsPointer Unpack(uint v) => new((int)(v & 0x1FF), (int)(v >> 9 & 0xFF), (v & 1u << 17) != 0, (v & 1u << 18) != 0, (v & 1u << 19) != 0, (WmsPointerDevice)(v >> 20 & 3));

    /// <summary>Maps a host point to console pixels: (hx, hy) inside the rectangle the picture is drawn in (left, top, width, height, the host's units) to the frame's
    /// <paramref name="frameW"/> x <paramref name="frameH"/> pixels. Outside the rectangle (or an empty one) the result is not present, at the nearest edge pixel.</summary>
    public static WmsPointer FromHost(double hx, double hy, double left, double top, double width, double height, int frameW, int frameH, bool left_, bool right, WmsPointerDevice device)
    {
        if (width <= 0 || height <= 0 || frameW <= 0 || frameH <= 0) return Idle with { Device = device };
        double fx = (hx - left) / width * frameW, fy = (hy - top) / height * frameH;
        bool inside = fx >= 0 && fy >= 0 && fx < frameW && fy < frameH;
        int x = (int)Math.Floor(fx), y = (int)Math.Floor(fy);
        // a frame narrower or wider than 400 (a SNES game on the console) still reports console pixels of that frame, clamped to the port's range
        return new WmsPointer(Math.Clamp(x, 0, Math.Min(frameW, Width) - 1), Math.Clamp(y, 0, Math.Min(frameH, Height) - 1), inside, inside && left_, inside && right, device).Normalized();
    }

    public override string ToString() => $"{(Present ? "present" : "away")} {X},{Y}{(Left ? " left" : "")}{(Right ? " right" : "")} {Device.ToString().ToLowerInvariant()}";
}

/// <summary>The pointer port's registers, $4228-$422C (spec section 10.1): latched once a frame by <see cref="LatchPointer"/>.</summary>
public sealed partial class WmsExtension
{
    private WmsPointer ptr, ptrPrev;

    /// <summary>The pointer the registers show this frame.</summary>
    public WmsPointer Pointer => ptr;

    /// <summary>The session, at the start of each frame: the host's pointer becomes the registers' value, the old one the previous (for the edge bits).</summary>
    public void LatchPointer(WmsPointer p) { ptrPrev = ptr; ptr = p.Normalized(); }

    private byte ReadPointer(uint offset)
    {
        switch (offset)
        {
            case 0x4228: return (byte)ptr.X;
            case 0x4229: return (byte)(ptr.X >> 8 & 1);
            case 0x422A: return (byte)ptr.Y;
            case 0x422B:
                int s = (ptr.Present ? 1 : 0) | (ptr.Left ? 2 : 0) | (ptr.Right ? 4 : 0)
                      | (ptr.Left && !ptrPrev.Left ? 8 : 0) | (!ptr.Left && ptrPrev.Left ? 0x10 : 0) | (ptr.Right && !ptrPrev.Right ? 0x20 : 0) | (int)ptr.Device << 6;
                return (byte)s;
            case 0x422C: return (byte)((ptr.X != ptrPrev.X || ptr.Y != ptrPrev.Y ? 1 : 0) | (ptr.Present != ptrPrev.Present ? 2 : 0));
            default: return 0;
        }
    }

    private const byte PointerStateTag = 0x50;   // 'P'

    /// <summary>The pointer's part of a WideMess state: written last in the session's payload, so an older state (without it) still loads, the pointer idle.</summary>
    public void SavePointer(BinaryWriter w) { w.Write(PointerStateTag); w.Write(ptr.Pack()); w.Write(ptrPrev.Pack()); }

    public void LoadPointer(BinaryReader r)
    {
        var s = r.BaseStream;
        if (!s.CanSeek || s.Position >= s.Length) { ptr = ptrPrev = WmsPointer.Idle; return; }
        byte tag = r.ReadByte();
        if (tag != PointerStateTag) throw new InvalidDataException($"the WideMess part of the state ends with an unknown part {tag:X2} (written by a newer build)");
        ptr = WmsPointer.Unpack(r.ReadUInt32()); ptrPrev = WmsPointer.Unpack(r.ReadUInt32());
    }
}

/// <summary>A session with the WideMess pointer port (spec section 10): the host hands it the pointer once a frame, before <c>RunFrame</c>, as it hands the pads.</summary>
public interface IPointerSession
{
    /// <summary>True when the running console has the port (the WideMess console; a SNES game on it too, which never reads it).</summary>
    bool HasPointer { get; }
    /// <summary>The pointer the next frame latches.</summary>
    void SetPointer(WmsPointer p);
    /// <summary>What <see cref="SetPointer"/> last gave (what a recorder stores for the next frame).</summary>
    WmsPointer PendingPointer { get; }
}
