// Relink (2026-10-18, the user's report from FL with build r12): "the counter never increases" when MIDI Out channels are replaced by BrokenNes2 one by one, and new BrokenNes2 channels are not
// detected either. Before this, an instance became a candidate for a track only by an "untouched" heuristic that FL's parameter pushes or a state could defeat, and the Record window counted only
// the channels that had already found their track (at their first note, during playback): nothing moved before Play, and nothing said why.
//
// Now an instance is LINKED to the tracks on offer, visibly, before any note:
//   joined by itself   every instance created while tracks are on offer (a new channel, or the one FL makes for a Replace), and every instance still pristine (nothing but defaults so far) when
//                      tracks are offered. FL's parameter pushes do not undo it (they are logged). It leaves only when it is clearly the user's own: a project state that holds a game, a preset or a
//                      track, or a change made in its editor.
//   linked by hand     the editor's "Link to recording" button (every unlinked instance shows it while tracks are on offer) and the Record window's "Link all BrokenNes2 channels". A channel linked
//                      by hand stays linked through knob changes in its editor; loading a preset or a game into it, or Unlink, ends it.
// A linked instance finds its track from its first note as before (Bn2Plugin.Tracks.cs, Record/RecTracks.cs), or AT ONCE from its channel's name when FL tells it: FL names imported channels after
// the MIDI file's track names, and the SDK has FHD_GetContextInfo with CI_TrackName ("(R/W) PAnsiChar encoded as UTF-8", fp_plugclass.h). Whether FL answers a generator with its channel's name
// (or the mixer track's), and whether a Replace keeps the imported name, is not known (FL could not be run here): the instance asks after FL opened it (FPD_PluginLoaded), when FL says the
// context changed (FPD_ContextInfoChanged, FPD_RoutingChanged), after a project load, and every second from FL's Idle and the open editor while it waits; every answer goes to the host log.
using System.Runtime.InteropServices;
using System.Text;
using BrokenNes.Fruity;
using BrokenNes2.Record;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    public const int LinkNone = 0, LinkAuto = 1, LinkHand = 2;
    private volatile int link;                  // LinkNone, LinkAuto (joined by itself), LinkHand (the button or Link all)
    private volatile string? linkNote;          // how it joined, or why it is not linked
    private volatile bool pristine = true;      // nothing but defaults so far (instances that existed before an offer join it by themselves only when pristine)
    private volatile string? channelName;       // what FL answered when asked for the channel's name (null: no name)
    private volatile string? nameNote;          // why that name took no track
    private long namePolled = long.MinValue;
    private string? nameLogged = "\0";
    private int nameAsks;

    /// <summary>Whether this instance is linked to the tracks on offer, and how (LinkNone, LinkAuto, LinkHand).</summary>
    internal int LinkState => link;
    internal string? LinkNote => linkNote;
    internal string? ChannelName => channelName;

    /// <summary>FL's host version (TFruityPlugHost.HostVersion, the int after the vtable pointer), for the log.</summary>
    private int HostVersion => Host.Ptr == 0 ? 0 : *(int*)(Host.Ptr + 8);

    /// <summary>Joins the tracks on offer by itself (the constructor; an offer that finds it pristine). UI thread.</summary>
    private void Join(string how)
    {
        if (link != LinkNone) return;
        link = LinkAuto;
        linkNote = how;
        HostLog.Text(InstanceNo, "joined the recording: " + how);
        TrackOffers.Changed();
    }

    /// <summary>The host log's line for an instance that was alive before the log ran (tracks were just offered): where it sits and what it is.</summary>
    internal void LogPresence()
    {
        var a = assignment;
        HostLog.Text(InstanceNo, $"present when the tracks were offered: emulator #{a.Emulator?.Id ?? 0} channel {a.Channel}, {(a.Emulator?.Mode == 1 ? "ROM mode" : "Direct mode")}, "
            + (IsSourceNow ? "it made the recording" : binding != null ? "plays " + binding.Describe() : configured ? "the user's own (" + (linkNote ?? "configured") + ")" : pristine ? "pristine" : "FL set values on it")
            + $", values [{string.Join(",", Values)}]");
    }

    /// <summary>Tracks were offered (TrackOffers.Offer, after publishing): an instance that is still pristine joins them.</summary>
    internal void OfferAppeared()
    {
        if (link != LinkNone || binding != null || bindState != 0 || IsSourceNow) return;
        if (configured || !pristine) { linkNote ??= configured ? "it is configured" : "it was made before the tracks were offered, with values of its own"; return; }
        Join("it was untouched when the tracks were offered");
    }

    /// <summary>Linked by hand (the editor's "Link to recording", the Record window's "Link all"). Null when linked, else why not.</summary>
    internal string? LinkByHand(string how, bool fromButton)
    {
        var snap = TrackOffers.Current;
        if (snap == null) return "no tracks are on offer: record, or reopen a project with a recording";
        if (binding != null || bindState != 0) return "this channel already plays a track";
        if (IsSourceNow) return "this channel's emulator made the recording";
        if (fromButton && RecMutants.On(RecMutants.LinkButtonIgnored)) return null;          // (the self-test's deliberate bug: says linked, does nothing)
        link = LinkHand;
        linkNote = how;
        nameNote = null;
        HostLog.Text(InstanceNo, "linked to the recording by hand: " + how);
        TrackOffers.Changed();
        PollChannelName("linked by hand", fromEvent: true);
        return null;
    }

    /// <summary>"Unlink" in the editor: the channel stops waiting for its track (it keeps its own sound) and does not join an offer by itself again.</summary>
    internal void UnlinkByHand()
    {
        if (binding != null || bindState != 0) return;
        if (link == LinkNone) return;
        link = LinkNone;
        MarkConfigured("you pressed Unlink");
        linkNote = "you pressed Unlink";
        HostLog.Text(InstanceNo, "unlinked by hand");
        TrackOffers.Changed();
    }

    /// <summary>The recording's own channel: a configured instance on the emulator whose recording is on offer (the one that recorded, the user's stems of that game). It is not counted, shows no
    /// banner and Link all leaves it alone. An instance FL makes while the tracks are on offer may land on that emulator too (Emulator = Auto takes the first emulator with a free channel, the
    /// game's): it is not configured, so it joins like any other (found by the editor-shot capture of relink: counting it out would have kept the counter at 0, the user's report).</summary>
    internal bool IsSourceNow => configured && TrackOffers.Current is { } s && assignment.Emulator is { } e && s.IsSource(e.Id);

    /// <summary>What this instance is to the tracks on offer: 0 nothing (no offer), 1 the recording's own channel, 2 not linked, 3 linked and waiting for its first note, 4 found its track.</summary>
    internal int OfferRole(TrackOfferSnapshot? snap)
    {
        if (snap == null) return 0;
        var b = binding;
        if (bindState == 1) return 4;
        if (b != null) { foreach (var r in snap.Recordings) if (r.Held.Session.Id == b.SessionId) return 4; }
        if (RecMutants.On(RecMutants.SourceEmulatorExcludes) && assignment.Emulator is { } se && snap.IsSource(se.Id)) return 1;      // (the deliberate bug: every instance on the recording's emulator counted out)
        if (link != LinkNone) return 3;
        if (configured && assignment.Emulator is { } e && snap.IsSource(e.Id)) return 1;
        return 2;
    }

    /// <summary>One line for the Record window's list of channels.</summary>
    internal string OfferLine(TrackOfferSnapshot snap)
    {
        string who = $"#{InstanceNo}" + (channelName is { } n ? $" \"{n}\"" : "");
        switch (OfferRole(snap))
        {
            case 4:
                var b = binding;
                if (b != null) return $"{who}: plays track {b.Track + 1} ({b.HowWords})";
                return boundSlot is { } s ? $"{who}: plays track {s.Info.Index + 1} (found from its notes)" : $"{who}: plays a track";
            case 3: return $"{who}: waiting for its first note" + (link == LinkHand ? " (linked by hand)" : "") + (nameNote != null ? $" ({nameNote})" : "");
            case 2: return $"{who}: not linked ({linkNote ?? "its own settings"})";
            default: return $"{who}: made the recording";
        }
    }

    /// <summary>What the editor's banner says (and whether it offers Link or Unlink): null when no tracks are on offer or this channel made the recording.</summary>
    internal (string Title, string Text, int Button)? LinkBanner()
    {
        var snap = TrackOffers.Current;
        int role = OfferRole(snap);
        if (role is 0 or 1) return null;
        string rec = snap!.Recordings[^1].Held.Session.Name;
        if (role == 4)
        {
            var b = binding;
            string what = b != null ? $"track {b.Track + 1} of \"{b.Recording}\": {b.Name} ({b.HowWords})" : boundSlot is { } s ? $"track {s.Info.Index + 1}: {s.Info.Name} (found from its first notes; it moves to the tracks' emulator in a moment)" : "a track";
            return ("PLAYS ITS TRACK", "This channel plays " + what + ".", 0);
        }
        if (role == 3)
            return ("LINKED TO THE RECORDING", "Press Play in FL and I will find my track from my first note (its colour and its key) and play its instrument; until then I am silent" + (channelName != null && nameNote != null ? $" (FL calls this channel \"{channelName}\": {nameNote})" : "") + ". Or use Play here in the Record window.", 2);
        return ($"TRACKS OF \"{rec}\" ON OFFER", $"This channel is not linked ({linkNote ?? "its own settings"}). Link it and it finds its track from its first note when FL plays.", 1);
    }

    // ---- the channel's name, as FL says it ----

    public override void Idle() => PollChannelName("idle", fromEvent: false);

    /// <summary>Asks FL for the channel's name (FHD_GetContextInfo with CI_TrackName; the GUI thread) and logs the answer; when the instance is linked and waiting and the name is exactly one
    /// offered track's, it takes that track at once, before any note. <paramref name="fromEvent"/>: FL just said something changed (always asked and logged); else at most once a second, and only
    /// while it waits.</summary>
    internal void PollChannelName(string when, bool fromEvent)
    {
        var snap = TrackOffers.Current;
        if (snap == null) return;
        bool waiting = link != LinkNone && bindState == 0 && binding == null;
        if (!fromEvent)
        {
            if (!waiting) return;
            long now = Environment.TickCount64;
            if (now - namePolled < 1000) return;
            namePolled = now;
        }
        string? name = ReadChannelName(out string answer);
        nameAsks++;
        if (fromEvent || !string.Equals(name, nameLogged, StringComparison.Ordinal)) { HostLog.Text(InstanceNo, $"FL's name for this channel ({when}, FHD_GetContextInfo CI_TrackName): {answer}"); nameLogged = name; }
        if (!string.Equals(name, channelName, StringComparison.Ordinal)) { channelName = name; TrackOffers.Changed(); }
        if (!waiting || name == null) return;
        if (RecMutants.On(RecMutants.NameIgnored)) return;                                   // (the self-test's deliberate bug: the name never takes a track)
        var (slot, why) = TrackOffers.FindByName(name);
        if (slot == null && name.StartsWith(DisplayName, StringComparison.OrdinalIgnoreCase))
            why = "FL named the channel after the plugin: its \"Auto name channels\" option (Options > General settings) renames a replaced channel; untick it before replacing to keep the imported track names";
        if (slot == null) { if (nameNote != why) { nameNote = why; TrackOffers.Changed(); HostLog.Text(InstanceNo, $"the name \"{name}\" takes no track: {why}"); } return; }
        int was = link;
        if (PickTrack(slot, TrackBinding.ByName) is { } refused)
        {
            if (binding == null && bindState == 0) link = was;                    // (still waiting: its notes may yet find it)
            nameNote = "its track could not be taken: " + refused;
            HostLog.Text(InstanceNo, $"the name \"{name}\" names track {slot.Info.Index + 1}, not taken: {refused}");
            TrackOffers.Changed();
        }
        else HostLog.Text(InstanceNo, $"took track {slot.Info.Index + 1} ({slot.Info.Name}) from its channel's name, before any note");
    }

    /// <summary>The channel's name FL gives (null: none), and what FL answered, in words for the log. The answer is read as a pointer only when it is one (above 64 KB and in readable memory).</summary>
    private string? ReadChannelName(out string answer)
    {
        nint r;
        try { r = Host.Dispatcher(HostTag, Fhd.GetContextInfo, Ci.TrackName, 0); }
        catch (Exception e) { answer = "the call failed: " + e.Message; return null; }
        if (r == 0) { answer = "0 (no name)"; return null; }
        if ((ulong)r < 0x10000) { answer = $"{r} (a number, not a pointer to a name: not used)"; return null; }
        var s = ReadUtf8(r, 1024);
        if (s == null) { answer = $"0x{r:X} (no readable text there: not used)"; return null; }
        answer = $"\"{s}\"";
        return s.Trim().Length == 0 ? null : s.Trim();
    }

    /// <summary>A zero-terminated UTF-8 string at <paramref name="p"/>, read only as far as committed readable memory goes (null: not readable, or no terminator within <paramref name="max"/> bytes).</summary>
    private static string? ReadUtf8(nint p, int max)
    {
        int n = 0;
        nint at = p;
        while (n < max)
        {
            if (VirtualQuery(at, out var mbi, (nuint)sizeof(MemoryInfo)) == 0) return null;
            const uint commit = 0x1000, readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            if (mbi.State != commit || (mbi.Protect & 0x101) != 0 || (mbi.Protect & readable) == 0) return null;
            long left = (long)mbi.BaseAddress + (long)mbi.RegionSize - (long)at;
            byte* b = (byte*)at;
            for (long i = 0; i < left && n < max; i++, n++)
                if (b[i] == 0) return Encoding.UTF8.GetString((byte*)p, n);
            at = (nint)((long)at + left);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [LibraryImport("kernel32.dll")] private static partial nuint VirtualQuery(nint address, out MemoryInfo info, nuint length);

    // ---- test hooks 720-739 ----

    /// <summary>720 link state (0 none, 1 joined by itself, 2 by hand), 721 link by hand (value 1: as the button does, 0: as Link all does; 0 done, 1 refused: the words in 643 field 8), 722 unlink,
    /// 723 Link all (returns how many were linked), 724 the offer role (0 no offer, 1 made the recording, 2 not linked, 3 waiting, 4 found), 725 copy the window's counter text, 726 copy the
    /// window's list of channels (lines joined by " | "), 727 copy this channel's banner text (title | text | button), 728 ask FL for the channel's name now (as an event does), 729 copy the name FL gave
    /// (-1 none), 730 the times it asked, 731 pristine (1/0), 732 copy the host log's path, 733 the host log's lines written so far (after a flush), 734 copy why it is not linked / how it joined,
    /// 735 the members version (changes whenever an instance joins, leaves, finds or settles), 736 the user's own log setting (value >= 0 sets it), 737 the log is automatic now (tracks on offer),
    /// 738 the BrokenNes2 instances alive in the process.</summary>
    private nint RelinkTestHook(int n, nint index, nint value)
    {
        switch (n)
        {
            case 720: return link;
            case 721: { var why = LinkByHand(value != 0 ? "the Link to recording button" : "Link all", value != 0); lastRecordResult = why ?? "linked"; return why == null ? 0 : 1; }
            case 722: UnlinkByHand(); return 0;
            case 723: return TrackOffers.LinkAll(out _);
            case 724: return OfferRole(TrackOffers.Current);
            case 725: return CopyText(TrackOffers.CounterText(), index, value);
            case 726: return CopyText(string.Join(" | ", TrackOffers.Count().Lines), index, value);
            case 727: { var b = LinkBanner(); return b is { } bb ? CopyText($"{bb.Title} | {bb.Text} | {bb.Button}", index, value) : -1; }
            case 728: PollChannelName("test", fromEvent: true); return 0;
            case 729: return channelName is { } cn ? CopyText(cn, index, value) : -1;
            case 730: return nameAsks;
            case 731: return pristine ? 1 : 0;
            case 732: return CopyText(HostLog.FilePath, index, value);
            case 733: HostLog.Flush(); return (nint)HostLog.LinesWritten;
            case 734: return CopyText(linkNote ?? "", index, value);
            case 735: return (nint)TrackOffers.MembersVersion;
            case 736: if (value >= 0) HostLog.Set(value != 0); return HostLog.UserOn ? 1 : 0;
            case 737: return HostLog.Automatic ? 1 : 0;
            case 738: return TrackOffers.Live;
            default: return 0;
        }
    }
}
