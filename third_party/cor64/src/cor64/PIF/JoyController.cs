using System;
using System.Collections.Generic;

namespace cor64.PIF {
    // [cer64plus] Held-state controller: buttons stay pressed until released, plus a real analog stick.
    public class JoyController {
        private readonly object m_Lock = new object();
        private object Lk => m_Lock ?? this; // [quirk_fix2] a savestate-restored controller (ports 2-4) has no lock object
        private ButtonPress m_Held = ButtonPress.None;
        private ButtonPress m_Script = ButtonPress.None;
        private sbyte m_StickX, m_StickY, m_ScriptX, m_ScriptY;
        private int m_StickLeft, m_StickRight, m_StickUp, m_StickDown;

        // Bit layout of the N64 controller status word.
        [Flags]
        public enum ButtonPress : ushort {
            None = 0,
            CRight = 0x0001,
            CLeft = 0x0002,
            CDown = 0x0004,
            CUp   = 0x0008,
            ShoulderR = 0x0010,
            ShoulderL = 0x0020,
            DPadRight = 0x0100,
            DPadLeft = 0x0200,
            DPadDown = 0x0400,
            DPadUp = 0x0800,
            Start = 0x1000,
            Z = 0x2000,
            B = 0x4000,
            A = 0x8000
        }

        public enum StickDir { Left, Right, Up, Down }

        public const sbyte StickFull = 80;

        public JoyController(int slot) {
            AssignedSlot = slot;
        }

        public int AssignedSlot { get; }

        public void Press(ButtonPress b) { lock (Lk) m_Held |= b; }
        public void Release(ButtonPress b) { lock (Lk) m_Held &= ~b; }

        public void Stick(StickDir dir, bool down) {
            lock (Lk) {
                int v = down ? 1 : 0;
                switch (dir) {
                    case StickDir.Left: m_StickLeft = v; break;
                    case StickDir.Right: m_StickRight = v; break;
                    case StickDir.Up: m_StickUp = v; break;
                    case StickDir.Down: m_StickDown = v; break;
                }
                m_StickX = (sbyte)((m_StickRight - m_StickLeft) * StickFull);
                m_StickY = (sbyte)((m_StickUp - m_StickDown) * StickFull);
            }
        }

        // Scripted input (headless tests) is ORed with the keyboard state.
        public void SetScript(ButtonPress b, sbyte x, sbyte y) { lock (Lk) {
            if (LatchGame) { var nw = b & ~m_Script; if (nw != ButtonPress.None) { m_Latch |= nw; m_LatchLeft = s_LatchReads; } } // [visp_fix2]
            m_Script = b; m_ScriptX = x; m_ScriptY = y; } }

        // [visp_fix2] COR64_VISPFIX2_LATCH (experiment, default off; =1 the listed games; =all every game).
        // At VI_SPEEDUP 11 a VI field is 11x shorter in CPU time, so a game whose input is not VI-locked reads its pad
        // only every few fields and a short press can fall between two reads. A newly pressed button is latched and
        // reported until the game has read the pad COR64_VISPFIX2_LATCH_N times (default 2) after the press, even if the
        // script released it earlier. Only short presses change; held buttons read as before.
        private static readonly string s_LatchEnv = System.Environment.GetEnvironmentVariable("COR64_VISPFIX2_LATCH");
        private static readonly int s_LatchReads = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_VISPFIX2_LATCH_N"), out var ln) && ln >= 1 ? ln : 2;
        public static bool LatchGame;
        public static bool LatchFor(string gameCode) {
            if (s_LatchEnv == "all") return true;
            if (s_LatchEnv != "1") return false; // off by default: no effect seen on the catalog nav scripts (FINDINGS)
            if (gameCode == null || gameCode.Length < 3) return false;
            switch (gameCode.Substring(1, 2)) {
                case "FR": // F1 Racing Championship (catalog_nav2: A needs ~40 frames at VI 11)
                case "YW": // Harvest Moon 64 (START ignored at VI 11, works at 1)
                case "GC": // GT 64 Championship Edition
                case "PM": // Premier Manager 64 (only the first D-pad move after a letter registers)
                    return true;
            }
            return false;
        }
        private ButtonPress m_Latch = ButtonPress.None;
        private int m_LatchLeft;

        public void Read(out ButtonPress buttons, out sbyte x, out sbyte y) {
            lock (Lk) {
                buttons = m_Held | m_Script | m_Latch;
                if (m_LatchLeft > 0 && --m_LatchLeft == 0) m_Latch = ButtonPress.None; // [visp_fix2]
                x = m_ScriptX != 0 ? m_ScriptX : m_StickX;
                y = m_ScriptY != 0 ? m_ScriptY : m_StickY;
            }
        }

        // Kept for old callers.
        public void AppendButtons(ButtonPress buttonPress) => Press(buttonPress);
    }
}
