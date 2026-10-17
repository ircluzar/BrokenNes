using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The homemade DSP-1's save state: where the port conversation stands (command / input / output phase, the command byte, the parameter words received and the result words still to be read, which byte of a word comes next,
/// the raster stream), the three attitude matrices and the camera set up by the last Parameter command (focus, distances, angles, eye, view and screen vectors, held horizon). The camera is kept as the doubles it is computed in,
/// written bit for bit, so a loaded chip answers the next Raster / Project command with the same numbers.
/// </summary>
public sealed partial class DSP1_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write((int)phase); w.Write(command);
        SnesStateIo.Put(w, input); w.Write(inputCount); w.Write(inputNeeded);
        SnesStateIo.Put(w, output); w.Write(outputCount); w.Write(outputPos);
        w.Write(lowHalf); w.Write(partial); w.Write(rasterMode); w.Write(rasterStop); w.Write(rasterLine);
        SnesStateIo.Put(w, attA); SnesStateIo.Put(w, attB); SnesStateIo.Put(w, attC);
        w.Write(fx); w.Write(fy); w.Write(fz); w.Write(lfe); w.Write(les); w.Write(aas); w.Write(azs);
        w.Write(ex); w.Write(ey); w.Write(ez);
        w.Write(dx); w.Write(dy); w.Write(dz);
        w.Write(rx); w.Write(ry);
        w.Write(ux); w.Write(uy); w.Write(uz);
        w.Write(vva); w.Write(input6); w.Write(azsAsked);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "DSP-1");
        int p = r.ReadInt32();
        if (!Enum.IsDefined(typeof(Phase), p)) throw new InvalidDataException($"The DSP-1 part of the state holds a phase of {p}.");
        phase = (Phase)p; command = r.ReadByte();
        SnesStateIo.Get(r, input); inputCount = r.ReadInt32(); inputNeeded = r.ReadInt32();
        SnesStateIo.Get(r, output); outputCount = r.ReadInt32(); outputPos = r.ReadInt32();
        lowHalf = r.ReadBoolean(); partial = r.ReadUInt16(); rasterMode = r.ReadBoolean(); rasterStop = r.ReadBoolean(); rasterLine = r.ReadInt16();
        // the matrices are replaced in place (a loaded chip must not share an array with the state it was loaded from)
        SnesStateIo.Get(r, attA); SnesStateIo.Get(r, attB); SnesStateIo.Get(r, attC);
        fx = r.ReadDouble(); fy = r.ReadDouble(); fz = r.ReadDouble(); lfe = r.ReadDouble(); les = r.ReadDouble(); aas = r.ReadDouble(); azs = r.ReadDouble();
        ex = r.ReadDouble(); ey = r.ReadDouble(); ez = r.ReadDouble();
        dx = r.ReadDouble(); dy = r.ReadDouble(); dz = r.ReadDouble();
        rx = r.ReadDouble(); ry = r.ReadDouble();
        ux = r.ReadDouble(); uy = r.ReadDouble(); uz = r.ReadDouble();
        vva = r.ReadDouble(); input6 = r.ReadInt16(); azsAsked = r.ReadInt16();
        if (inputCount < 0 || inputCount > input.Length || outputCount < 0 || outputCount > output.Length || outputPos < 0 || outputPos > 8)
            throw new InvalidDataException("The DSP-1 part of the state holds counts that do not fit the chip's buffers.");
    }
}
