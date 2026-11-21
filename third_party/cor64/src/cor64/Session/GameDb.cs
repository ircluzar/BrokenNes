using System;
using System.Collections.Generic;

namespace cor64.Session
{
    /// <summary>Per-game settings in one table, keyed by the two middle letters of the cart game code (NSME -> "SM").
    /// Row = setting, game id, value, gate flag, note. The gate is a COR64_* variable that turns the row off when it is
    /// "0" (null = always on; "A+B" = both must be on). A lookup takes the FIRST enabled row for (setting, id), so row
    /// order is precedence. To add a game, append a row (a row appended for an id already listed is shadowed by the
    /// earlier one). Global env overrides (COR64_SESSION_BATCH, COR64_COUNT_SCALE, COR64_SPDPOLL2_FORCEBATCH,
    /// COR64_CATFIX2_COUNTX, COR64_SPDPOLL1=all, ...) stay in the consumers: N64Session.BatchFor,
    /// SystemController.CountScaleFor, Interpreter.Spd1For / Spd2LongFor and Telemetry.ViSpeedupFor.</summary>
    public static class GameDb
    {
        public enum Setting {
            Slice,       // CPU steps per RunBatch slice (default 16, or COR64_SPDCAT3_DEFSLICE for uncertified games)
            SliceCert,   // certified game: keeps 16 instead of the speed_cat3 default slice (value unused)
            SliceExcl,   // excluded from the speed_cat3 default slice (value unused; COR64_SPDCAT3_EXCL adds more)
            CountFixed,  // Count ticks per CPU step, returned as is
            CountBase,   // Count ticks per CPU step, then raised to ceil(ViSpeedup/2) for ViCountFix games
            ViCountFix,  // visp_fix1: Count follows the VI speed-up (value unused)
            ViCap,       // VI speed-up ceiling
            PollSkip,    // speed_poll1 poll-loop skip (value unused)
            PollLong,    // speed_poll2 long cycles / store logs (value unused)
            ViLineWait,  // speed_cat9 VI line wait skip (value = loop head PC)
        }

        public readonly struct Row {
            public readonly Setting Setting; public readonly string Id; public readonly int Value; public readonly string Flag; public readonly string Note;
            public Row(Setting s, string id, int v, string flag, string note) { Setting = s; Id = id; Value = v; Flag = flag; Note = note; }
        }

        static Row R(Setting s, string id, int v, string flag, string note) => new Row(s, id, v, flag, note);
        const Setting Slice = Setting.Slice, SliceCert = Setting.SliceCert, SliceExcl = Setting.SliceExcl, CountFixed = Setting.CountFixed,
            CountBase = Setting.CountBase, ViCountFix = Setting.ViCountFix, ViCap = Setting.ViCap, PollSkip = Setting.PollSkip, PollLong = Setting.PollLong;
        const string P2 = "COR64_SPDPOLL2", C4 = "COR64_SPDCAT4_POLL", K1 = "COR64_SPDCAT1_BATCH", K2 = "COR64_SPDCAT2_BATCH";

        // Order within a setting is precedence. Tags in [] name the FINDINGS.md entry.
        public static readonly Row[] Rows = {
            // ---- Slice ----
            R(Slice, "HX", 1024, "COR64_SPDPOLL2_BATCH", "Hexen [speed_poll2]"),
            R(Slice, "GE", 256, null, "GoldenEye 007 [ge_batch]"),
            R(Slice, "IJ", 1024, "COR64_SPEEDC2_F5+COR64_F5SPEED2_BATCH", "Indiana Jones [speed_c2]"),
            R(Slice, "NA", 1024, "COR64_SPEEDC2_F5+COR64_F5SPEED2_BATCH", "Battle for Naboo [speed_c2]"),
            R(Slice, "IJ", 256, "COR64_F5SPEED2_BATCH", "Indiana Jones [f5_speed2]"),
            R(Slice, "NA", 256, "COR64_F5SPEED2_BATCH", "Battle for Naboo [f5_speed2]"),
            R(Slice, "RS", 256, "COR64_F5SPEED2_BATCH", "Rogue Squadron [f5_speed2]"),
            R(Slice, "B7", 512, "COR64_BT_BATCH", "Banjo-Tooie [bt_speed]"),
            R(Slice, "FU", 512, "COR64_SPEEDC1_BATCH", "Conker [speed_c1]"),
            R(Slice, "RE", 512, "COR64_SPEEDC1_BATCH", "Resident Evil 2 [speed_c1]"),
            R(Slice, "N6", 512, "COR64_SPEEDC2_BATCH", "Dr. Mario 64 [speed_c2]"),
            R(Slice, "RT", 1024, K1, "Rat Attack [speed_cat1]"),
            R(Slice, "CU", 1024, K1, "Cruis'n USA [speed_cat1]"),
            R(Slice, "CW", 1024, K1, "Cruis'n World [speed_cat1]"),
            R(Slice, "HV", 1024, K1, "Hybrid Heaven [speed_cat1]"),
            R(Slice, "DN", 1024, K1, "Duke Nukem 64 [speed_cat1]"),
            R(Slice, "ER", 1024, K1, "AeroFighters Assault [speed_cat1]"),
            R(Slice, "CF", 1024, K1, "ClayFighter 63 1/3 [speed_cat1]"),
            R(Slice, "BE", 1024, K1, "All-Star Baseball 2000 [speed_cat1]"),
            R(Slice, "TE", 1024, K1, "1080 Snowboarding [speed_cat1]"),
            R(Slice, "WM", 1024, K2, "WCW Mayhem [speed_cat2]"),
            R(Slice, "EP", 1024, K2, "Episode I Racer [speed_cat2]"),
            R(Slice, "VR", 1024, K2, "Virtual Pool 64 [speed_cat2]"),
            R(Slice, "L2", 1024, K2, "Top Gear Rally 2 [speed_cat2]"),
            R(Slice, "MJ", 1024, K2, "Mahjong 64 [speed_cat2]"),
            R(Slice, "8M", 1024, K2, "Madden Football 64 [speed_cat2]"),
            R(Slice, "WP", 1024, K2, "Wipeout 64 [speed_cat2]"),
            R(Slice, "SC", 1024, K2, "Starshot [speed_cat2]"),
            R(Slice, "LR", 1024, K2, "Lode Runner 3D [speed_cat2]"),
            R(Slice, "9F", 1024, K2, "FIFA 99 [speed_cat2]"),
            R(Slice, "Q2", 1024, K2, "Quake II [speed_cat2]"),
            R(Slice, "QK", 1024, K2, "Quake 64 [speed_cat2]"),
            R(Slice, "BS", 1024, K2, "All-Star Baseball 99 [speed_cat2]"),
            R(Slice, "TP", 1024, K2, "Tetrisphere [speed_cat2]"),
            R(Slice, "SQ", 1024, K2, "StarCraft 64 [speed_cat2]"),
            R(Slice, "SK", 1024, K2, "Snowboard Kids [speed_cat2]"),
            R(Slice, "W4", 1024, "COR64_INGFIX3_BATCH", "WWF No Mercy [ingame_fix3]"),
            R(Slice, "DM", 1024, "COR64_INGFIX3_BATCH", "Doom 64 [ingame_fix3]"),
            R(Slice, "DM", 1024, "COR64_SPDPOLL1_BATCH", "Doom 64 [speed_poll1]"),
            // ---- SliceCert: certified games keep 16 [speed_cat3] ----
            R(SliceCert, "SM", 0, null, ""), R(SliceCert, "KT", 0, null, ""), R(SliceCert, "FX", 0, null, ""),
            R(SliceCert, "ZL", 0, null, ""), R(SliceCert, "WR", 0, null, ""), R(SliceCert, "AL", 0, null, ""),
            R(SliceCert, "DY", 0, null, ""), R(SliceCert, "LB", 0, null, ""), R(SliceCert, "K4", 0, null, ""),
            R(SliceCert, "YS", 0, null, ""), R(SliceCert, "PF", 0, null, ""), R(SliceCert, "GE", 0, null, ""),
            R(SliceCert, "FZ", 0, null, ""), R(SliceCert, "M8", 0, null, ""), R(SliceCert, "N6", 0, null, ""),
            R(SliceCert, "ZS", 0, null, ""), R(SliceCert, "PD", 0, null, ""), R(SliceCert, "DO", 0, null, ""),
            R(SliceCert, "FU", 0, null, ""), R(SliceCert, "B7", 0, null, ""), R(SliceCert, "JF", 0, null, ""),
            R(SliceCert, "RS", 0, null, ""), R(SliceCert, "NA", 0, null, ""), R(SliceCert, "IJ", 0, null, ""),
            R(SliceCert, "WO", 0, null, ""), R(SliceCert, "RE", 0, null, ""), R(SliceCert, "P3", 0, null, ""),
            R(SliceCert, "BC", 0, null, ""), R(SliceCert, "PW", 0, null, ""), R(SliceCert, "MX", 0, null, ""),
            R(SliceExcl, "AI", 0, null, "Midway's Greatest Arcade Hits 1: unimplemented op with 1024"),
            R(SliceExcl, "SY", 0, null, "Scooby-Doo: slower with 1024"),
            // [n64-revive] Roadsters Trophy (RR) row removed: its title_fix3 black boot was the VI_CURRENT wrap at 1024 slices (Video.ViMod).
            // ---- Count scale [catalog_fix2/5, ingame_fix1/3] (all gated by COR64_CATFIX2_COUNTX) ----
            R(CountFixed, "HC", 6, "COR64_VISPFIX2_COUNT", "Hercules at VI cap 6: the Titus licence needs Count >= 5 [visp_fix2]"),
            R(CountFixed, "GX", 2, "COR64_CATFIX5_COUNT", "Gauntlet Legends [catalog_fix5]"),
            R(CountFixed, "GD", 2, "COR64_CATFIX5_COUNT", "Gauntlet Legends [catalog_fix5]"),
            R(CountFixed, "7I", 2, "COR64_INGFIX1_FIFACOUNT", "FIFA Soccer 64 [ingame_fix1]"),
            R(CountFixed, "BQ", 3, "COR64_INGFIX3_BQCOUNT", "BattleTanx: Global Assault [ingame_fix3]"),
            R(CountBase, "BX", 4, null, "BattleTanx [catalog_fix2]"),
            R(CountBase, "BQ", 4, null, "BattleTanx: Global Assault [catalog_fix2]"),
            // ---- Count follows VI speed-up [visp_fix1] (COR64_VISPFIX1_COUNT=0 off / =all every game) ----
            R(ViCountFix, "HC", 0, null, "Hercules"), R(ViCountFix, "BP", 0, null, "Blues Brothers 2000"),
            R(ViCountFix, "BR", 0, null, "Milo's Astro Lanes"), R(ViCountFix, "W4", 0, null, "WWF No Mercy"),
            R(ViCountFix, "DE", 0, null, "Destruction Derby 64"), R(ViCountFix, "CU", 0, null, "Cruis'n USA"),
            R(ViCountFix, "M3", 0, null, "Monster Truck Madness 64"), R(ViCountFix, "DM", 0, null, "Doom 64"),
            // ---- VI speed-up ceiling [visp_fix1] ----
            R(ViCap, "M3", 6, "COR64_VISPFIX1_CAP", "Monster Truck Madness 64"),
            R(ViCap, "DM", 6, "COR64_VISPFIX1_CAP", "Doom 64"),
            R(ViCap, "TO", 3, "COR64_VISPFIX1_CAP", "Toukon Road: Brave Spirits"),
            R(ViCap, "XO", 1, "COR64_VISPFIX1_CAP", "Cruis'n Exotica"),
            // [visp_fix2] VI-11 hangs past the visp_fix1 screens (FINDINGS: visp_fix2)
            R(ViCap, "HC", 6, "COR64_VISPFIX2_CAP", "Hercules: title never leaves at 9/11"),
            R(ViCap, "TH", 6, "COR64_VISPFIX2_CAP", "Toy Story 2: Etch A Sketch level screen freezes at 11"),
            R(ViCap, "SD", 6, "COR64_VISPFIX2_CAP", "Shadow Man: first level load hangs at 11"),
            R(ViCap, "PM", 3, "COR64_TITLE3_CAP", "Premier Manager 64: D-pad moves dropped/doubled at 6/11 (name entry, team select); squad screen at 3 [title_fix3]"),
            R(ViCap, "SJ", 6, "COR64_TITLE3_CAP", "J.League Tactics Soccer: VS-mode pre-match screen ignores A at 11; match plays at 6 [title_fix3]"),
            R(ViCap, "GL", 1, "COR64_TITLE2_CAP", "Getter Love!!: personality select hangs after the first pick at 6/11; board at 1 [title_fix2]"),
            R(ViCap, "GL", 6, "COR64_VISPFIX2_CAP", "Getter Love!!: intro text freezes at 11"),
            R(ViCap, "IB", 1, "COR64_VISPFIX2_CAP", "Bass Tsuri No.1: freezes at 11, black at 6"),
            R(ViCap, "CU", 6, "COR64_VISPFIX2_CAP", "Cruis'n USA: RDP stops after the car pick at 11"),
            // [title_fix1] short presses/START dropped at 11 (catalog_nav2: not SI/PIF); gameplay reached at 1 (FINDINGS: title_fix1)
            R(ViCap, "YW", 1, "COR64_TITLE1_CAP", "Harvest Moon 64: START ignored at 11; field play at 1"),
            R(ViCap, "FR", 1, "COR64_TITLE1_CAP", "F1 Racing Championship: Select Driver ignores A at 11; race at 1"),
            R(ViCap, "DZ", 1, "COR64_TITLE1_CAP", "Dezaemon 3D: stage black/dialogue only at 11; stage plays at 1"),
            R(ViCap, "KK", 1, "COR64_ACC2_KKCAP", "Knockout Kings 2000: garbage-stripe frames (menus, fight intro, replays) at 11 and 6; clean at 1, ~260% [accuracy1/accuracy2]"),
            // ---- Poll-loop skip [speed_poll1/2, speed_cat4] (COR64_SPDPOLL1=0 off / =all every cart) ----
            R(PollSkip, "DM", 0, null, "Doom 64 [speed_poll1]"), R(PollSkip, "SK", 0, null, "Snowboard Kids [speed_poll1]"),
            R(PollSkip, "QK", 0, null, "Quake 64 [speed_poll1]"),
            R(PollSkip, "SW", 0, "COR64_SPDCAT6_POLL", "Shadows of the Empire [speed_cat6]"), R(PollSkip, "C2", 0, "COR64_SPDCAT6_POLL", "ClayFighter SC [speed_cat6]"),
            R(PollSkip, "MO", 0, "COR64_SPDCAT7_POLL", "Monopoly [speed_cat7]"),
            R(PollSkip, "HX", 0, P2, "Hexen"), R(PollSkip, "B4", 0, P2, ""), R(PollSkip, "BF", 0, P2, ""), R(PollSkip, "CF", 0, P2, ""),
            R(PollSkip, "DA", 0, P2, ""), R(PollSkip, "9F", 0, P2, ""), R(PollSkip, "8I", 0, P2, ""), R(PollSkip, "7I", 0, P2, ""),
            R(PollSkip, "FF", 0, P2, ""), R(PollSkip, "GN", 0, P2, ""), R(PollSkip, "YW", 0, P2, ""), R(PollSkip, "JL", 0, P2, ""),
            R(PollSkip, "JO", 0, P2, ""), R(PollSkip, "LR", 0, P2, ""), R(PollSkip, "8M", 0, P2, ""), R(PollSkip, "9M", 0, P2, ""),
            R(PollSkip, "OF", 0, P2, ""), R(PollSkip, "Q2", 0, P2, ""), R(PollSkip, "RK", 0, P2, ""), R(PollSkip, "SQ", 0, P2, ""),
            R(PollSkip, "SC", 0, P2, ""), R(PollSkip, "VR", 0, P2, ""), R(PollSkip, "WV", 0, P2, ""), R(PollSkip, "W3", 0, P2, ""),
            R(PollSkip, "AD", 0, P2, ""),
            R(PollSkip, "AC", 0, C4, ""), R(PollSkip, "ZO", 0, C4, ""), R(PollSkip, "HF", 0, C4, ""), R(PollSkip, "MD", 0, C4, ""),
            R(PollSkip, "FL", 0, C4, ""), R(PollSkip, "2M", 0, C4, ""), R(PollSkip, "MP", 0, C4, ""), R(PollSkip, "MI", 0, C4, ""),
            R(PollSkip, "MK", 0, C4, ""), R(PollSkip, "PU", 0, C4, ""), R(PollSkip, "2P", 0, C4, ""), R(PollSkip, "RD", 0, C4, ""),
            R(PollSkip, "22", 0, C4, ""), R(PollSkip, "SW", 0, C4, ""), R(PollSkip, "SX", 0, C4, ""), R(PollSkip, "TX", 0, C4, ""),
            R(PollSkip, "3P", 0, C4, ""), R(PollSkip, "SB", 0, C4, ""), R(PollSkip, "WF", 0, C4, ""), R(PollSkip, "WD", 0, C4, ""),
            R(PollSkip, "8W", 0, C4, ""),
            R(PollLong, "DM", 0, null, "Doom 64 [speed_poll2]"),
            R(Setting.ViLineWait, "RR", unchecked((int)0x80001DE8u), "COR64_SPDCAT9_VIWAIT", "Roadsters Trophy [speed_cat9]"),
            // ---- append new rows above this line, inside their setting's block ----
        };

        static readonly Dictionary<string, bool> s_Flags = new();
        static bool FlagOn(string flag) {
            if (flag == null) return true;
            lock (s_Flags) {
                if (s_Flags.TryGetValue(flag, out var on)) return on;
                on = true;
                foreach (var f in flag.Split('+')) if (Environment.GetEnvironmentVariable(f) == "0") on = false;
                s_Flags[flag] = on; return on;
            }
        }

        /// <summary>Middle two letters of a game code (null when too short).</summary>
        public static string Id(string gameCode) => gameCode == null || gameCode.Length < 3 ? null : gameCode.Substring(1, 2);

        /// <summary>First enabled row for (setting, id).</summary>
        public static bool TryGet(Setting s, string id, out int value) {
            if (id != null)
                foreach (var r in Rows)
                    if (r.Setting == s && r.Id == id && FlagOn(r.Flag)) { value = r.Value; return true; }
            value = 0; return false;
        }
        public static bool Has(Setting s, string id) => TryGet(s, id, out _);
    }
}
