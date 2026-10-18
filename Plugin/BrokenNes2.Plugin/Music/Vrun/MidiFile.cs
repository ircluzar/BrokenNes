using System;
using System.Collections.Generic;

namespace Vrun.Audio;

/// <summary>One MIDI event, as the converter sees it. For meta events
/// (status 0xFF) <see cref="Data1"/> is the meta type and
/// <see cref="MetaData"/> its payload; SysEx (0xF0/0xF7) carries its payload
/// in <see cref="MetaData"/>.</summary>
public readonly record struct MidiEvent(int DeltaTime, int Status, int Data1, int Data2, byte[]? MetaData)
{
    public int Channel => Status & 0x0F;
    public int Command => Status & 0xF0;
}

/// <summary>A parsed Standard MIDI File. Parsing is deliberately permissive
/// in exactly the same ways the original Python converter was (running
/// status, a data byte with no running status yet reads as status 0, the
/// declared track length wins over the events inside it), so every file the
/// VRUN build accepted parses to the same events here.</summary>
public sealed class MidiFile
{
    public const int NoteOn = 0x90;
    public const int NoteOff = 0x80;
    public const int ControlChange = 0xB0;
    public const int Meta = 0xFF;
    public const int SetTempo = 0x51;

    public int Format { get; }
    public int TicksPerQuarter { get; }
    public IReadOnlyList<IReadOnlyList<MidiEvent>> Tracks { get; }

    public MidiFile(int format, int ticksPerQuarter, IReadOnlyList<IReadOnlyList<MidiEvent>> tracks)
    {
        Format = format;
        TicksPerQuarter = ticksPerQuarter;
        Tracks = tracks;
    }

    public static MidiFile Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 14 || buf[0] != (byte)'M' || buf[1] != (byte)'T' || buf[2] != (byte)'h' || buf[3] != (byte)'d')
            throw new FormatException("Invalid MIDI header");
        int headerLen = (int)ReadU32(buf, 4);
        int fmt = ReadU16(buf, 8);
        int trackCount = ReadU16(buf, 10);
        int tpq = ReadU16(buf, 12);
        int pos = 4 + 4 + headerLen;

        var tracks = new List<IReadOnlyList<MidiEvent>>(trackCount);
        for (int t = 0; t < trackCount; t++)
        {
            var events = ParseTrack(buf, ref pos);
            tracks.Add(events);
        }
        return new MidiFile(fmt, tpq, tracks);
    }

    static List<MidiEvent> ParseTrack(ReadOnlySpan<byte> buf, ref int pos)
    {
        if (buf[pos] != (byte)'M' || buf[pos + 1] != (byte)'T' || buf[pos + 2] != (byte)'r' || buf[pos + 3] != (byte)'k')
            throw new FormatException($"Invalid track header at offset {pos}");
        pos += 4;
        int length = (int)ReadU32(buf, pos);
        pos += 4;
        int endPos = pos + length;

        var events = new List<MidiEvent>();
        int runningStatus = 0;
        while (pos < endPos)
        {
            int delta = ReadVariableLength(buf, ref pos);
            int status = buf[pos];
            pos += 1;
            if (status < 0x80)
            {
                status = runningStatus;
                pos -= 1; // this byte was actually data, not a status byte
            }
            if (status >= 0x80 && status < 0xF0)
                runningStatus = status;

            if (status == Meta)
            {
                int evType = buf[pos];
                pos += 1;
                int mlen = ReadVariableLength(buf, ref pos);
                byte[] meta = SliceClipped(buf, pos, mlen);
                pos += mlen;
                events.Add(new MidiEvent(delta, status, evType, 0, meta));
            }
            else if (status == 0xF0 || status == 0xF7)
            {
                int mlen = ReadVariableLength(buf, ref pos);
                byte[] meta = SliceClipped(buf, pos, mlen);
                pos += mlen;
                events.Add(new MidiEvent(delta, status, 0, 0, meta));
            }
            else
            {
                int command = status & 0xF0;
                if (command == 0xC0 || command == 0xD0)
                {
                    int d1 = buf[pos];
                    pos += 1;
                    events.Add(new MidiEvent(delta, status, d1, 0, null));
                }
                else
                {
                    int d1 = buf[pos];
                    int d2 = buf[pos + 1];
                    pos += 2;
                    events.Add(new MidiEvent(delta, status, d1, d2, null));
                }
            }
        }
        pos = endPos;
        return events;
    }

    // Python's buf[pos:pos + n]: clipped to the buffer, never throws.
    static byte[] SliceClipped(ReadOnlySpan<byte> buf, int pos, int n)
    {
        if (pos >= buf.Length || n <= 0)
            return Array.Empty<byte>();
        return buf.Slice(pos, Math.Min(n, buf.Length - pos)).ToArray();
    }

    static int ReadVariableLength(ReadOnlySpan<byte> buf, ref int pos)
    {
        int value = 0;
        while (true)
        {
            int b = buf[pos];
            pos += 1;
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }
        return value;
    }

    static uint ReadU32(ReadOnlySpan<byte> b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
    static int ReadU16(ReadOnlySpan<byte> b, int p) => b[p] << 8 | b[p + 1];
}
