// [cer64plus] Scripted controller input for headless runs.
// COR64_INPUT="start:900-930,a:1400-1410,stickup:2000-2300" holds each button while the VI interrupt count is in [from,to].
// Names: a b z start l r cup cdown cleft cright dup ddown dleft dright stickup stickdown stickleft stickright
using System;
using System.Collections.Generic;
using System.Threading;
using cor64;
using cor64.PIF;

namespace RunN64
{
    public class InputScript
    {
        private readonly List<(string name, long from, long to)> m_Steps = new();
        private readonly JoyController m_Joy;

        public static InputScript FromEnvironment(JoyController joy)
        {
            var spec = Environment.GetEnvironmentVariable("COR64_INPUT");
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var s = new InputScript(joy);
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                var kv = part.Split(':'); var range = kv[1].Split('-');
                s.m_Steps.Add((kv[0].Trim().ToLowerInvariant(), long.Parse(range[0]), long.Parse(range[1])));
            }
            return s;
        }

        private InputScript(JoyController joy) { m_Joy = joy; }

        public void Start() => new Thread(Run) { IsBackground = true, Name = "InputScript" }.Start();

        private void Run()
        {
            string lastActive = "";
            while (true) {
                long vi = Telemetry.ViInterrupts;
                var b = JoyController.ButtonPress.None; sbyte x = 0, y = 0;
                var active = new List<string>();
                foreach (var (name, from, to) in m_Steps) {
                    if (vi < from || vi > to) continue;
                    active.Add(name);
                    switch (name) {
                        case "a": b |= JoyController.ButtonPress.A; break;
                        case "b": b |= JoyController.ButtonPress.B; break;
                        case "z": b |= JoyController.ButtonPress.Z; break;
                        case "start": b |= JoyController.ButtonPress.Start; break;
                        case "l": b |= JoyController.ButtonPress.ShoulderL; break;
                        case "r": b |= JoyController.ButtonPress.ShoulderR; break;
                        case "cup": b |= JoyController.ButtonPress.CUp; break;
                        case "cdown": b |= JoyController.ButtonPress.CDown; break;
                        case "cleft": b |= JoyController.ButtonPress.CLeft; break;
                        case "cright": b |= JoyController.ButtonPress.CRight; break;
                        case "dup": b |= JoyController.ButtonPress.DPadUp; break;
                        case "ddown": b |= JoyController.ButtonPress.DPadDown; break;
                        case "dleft": b |= JoyController.ButtonPress.DPadLeft; break;
                        case "dright": b |= JoyController.ButtonPress.DPadRight; break;
                        case "stickup": y = JoyController.StickFull; break;
                        case "stickdown": y = (sbyte)-JoyController.StickFull; break;
                        case "stickleft": x = (sbyte)-JoyController.StickFull; break;
                        case "stickright": x = JoyController.StickFull; break;
                    }
                }
                m_Joy.SetScript(b, x, y);
                var now = string.Join("+", active);
                if (now != lastActive) { if (EventLog.Enabled) EventLog.E("INPUT", "vi=" + vi + " " + (now == "" ? "none" : now)); lastActive = now; }
                Thread.Sleep(2);
            }
        }
    }
}
