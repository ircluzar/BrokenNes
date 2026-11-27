// Dynamic automation (prototype, 2026-11-07): an Instruments tab field wired to one of the blank parameters after the 14 fixed ones, moved the way FL's automation moves a
// parameter (ProcessParam with REC_UpdateValue), applied on the GUI thread, kept by the project. Hooks 960-964 (Bn2Plugin.Automation.cs). Real FL is not tried here: the
// right-click menu itself is FL's, and FL is what turns "Create automation clip" into a clip.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private void AutomationTest(T t)
    {
        if (!File.Exists(InjectRun.Sonic1)) { t.Note($"dynamic automation: not tested, {InjectRun.Sonic1} is not there"); return; }
        const int Fixed = 14, Slots = 64, Inst = 1, FeedbackField = 1;
        byte[] project;
        using (var p = SegaGame(InjectRun.Sonic1, 4, "MD"))
        {
            DriveFrames(p, f => 0, 0, 1400);
            p.Test(HkRunaway, 0, 1);
            int slot = (int)p.Test(960, FeedbackField, Inst);
            t.Expect(slot == Fixed, $"wiring FM 2's Feedback gave parameter {slot}, expected the first slot ({Fixed})");
            if (slot < Fixed) return;
            t.Expect(p.Test(960, FeedbackField, Inst) == slot, "wiring the same knob again took another slot");
            string name = p.GetName(Fpn.Param, slot, 0);
            t.Expect(name.Contains("Feedback"), $"the slot's name is '{name}', not the knob's");
            t.Expect(p.GetName(Fpn.Param, slot + 1, 0).StartsWith("Free slot"), "the next slot is not free");
            t.Expect(p.GetName(Fpn.Param, Fixed + Slots - 1, 0) == $"Free slot {Slots}", "the last slot is not there");

            foreach (var (slotValue, want) in new[] { (1000, 7), (0, 0), (500, 4), (1000, 7) })
            {
                p.SetParam(slot, slotValue);                 // what FL's automation does (REC_UpdateValue)
                p.Test(961);                                 // FL's Idle on the GUI thread
                int now = (int)p.Test(962, FeedbackField, Inst);
                t.Expect(now == want, $"slot at {slotValue}: Feedback is {now}, expected {want}");
                t.Expect(p.GetName(Fpn.ParamValue, slot, slotValue) == want.ToString(), $"FL's value text for {slotValue} is '{p.GetName(Fpn.ParamValue, slot, slotValue)}', expected {want}");
            }
            t.Expect(p.Test(963) == 4, $"{p.Test(963)} edits applied, expected 4");
            p.SetParam(slot, 1000); p.SetParam(slot, 1000); p.Test(961);
            t.Expect(p.Test(963) == 4, "the same value again was applied again");
            project = p.SaveState();
        }
        using (var q = Reopen(project))
        {
            string name = q.GetName(Fpn.Param, Fixed, 0);
            t.Expect(name.Contains("Feedback"), $"after reopening, slot 1 is '{name}': the wiring was not kept");
        }
    }
}
