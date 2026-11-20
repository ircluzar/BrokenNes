// [cer64plus] 1 Hz sampler: writes telemetry.jsonl and periodic framebuffer PNGs next to the exe.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using cor64;
using cor64.RCP;

namespace RunN64
{
    public class TelemetrySampler
    {
        private readonly N64System m_Sys;
        private readonly Thread m_Thread;
        private volatile bool m_Stop;
        private readonly int m_ShotEverySec;
        public TelemetrySampler(N64System sys, int shotEverySec = 5)
        {
            m_Sys = sys; m_ShotEverySec = shotEverySec;
            m_Thread = new Thread(Run) { Name = "Telemetry", IsBackground = true };
        }
        public void Start() => m_Thread.Start();
        public void Stop() => m_Stop = true;

        private unsafe void Shot(string path)
        {
            var vi = m_Sys.VI;
            int w = vi.Width, h = vi.Height;
            if (w < 1 || h < 1 || w > 800 || h > 600) { w = 320; h = 240; }
            int mode = vi.ControlReg.GetPixelMode();
            if (mode != VideoControlReg.PIXELMODE_16BPP && mode != VideoControlReg.PIXELMODE_32BPP) return;
            int stride = (int)vi.WidthReg; if (stride < w || stride > 1024) stride = w;
            byte* src = (byte*)vi.FramebufferPtr;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                for (int x = 0; x < w; x++)
                {
                    byte r, g, b;
                    if (mode == VideoControlReg.PIXELMODE_32BPP) { uint px = *(uint*)(src + (y * stride + x) * 4); r = (byte)(px >> 24); g = (byte)(px >> 16); b = (byte)(px >> 8); }
                    else { int bo = ((y * stride + x) * 2) ^ 2; int v = src[bo] | (src[bo + 1] << 8); /* RDRAM is host-LE 32-bit words */ r = (byte)(((v >> 11) & 31) * 255 / 31); g = (byte)(((v >> 6) & 31) * 255 / 31); b = (byte)(((v >> 1) & 31) * 255 / 31); }
                    row[x * 4] = b; row[x * 4 + 1] = g; row[x * 4 + 2] = r; row[x * 4 + 3] = 255;
                }
            }
            bmp.UnlockBits(bd);
            bmp.Save(path, ImageFormat.Png);
        }

        private unsafe void RspDiag(int n)
        {
            var rsp = m_Sys.DeviceRcp.DeviceRsp;
            var sb = new System.Text.StringBuilder();
            var hist = new System.Collections.Generic.Dictionary<long, int>();
            int halted = 0;
            for (int k = 0; k < 4000; k++) { long pc = rsp.ReadPC(); hist.TryGetValue(pc, out int c); hist[pc] = c + 1; if (rsp.IsHalted) halted++; Thread.SpinWait(3000); }
            sb.AppendLine("halted samples: " + halted + "/4000");
            foreach (var kv in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(hist, x => x.Value), 25)) sb.AppendLine("pc 0x" + kv.Key.ToString("X3") + " x" + kv.Value);
            sb.AppendLine("--- RSP executions per PC (top 25) ---");
            var order = new System.Collections.Generic.List<int>(); for (int i = 0; i < 0x400; i++) if (Telemetry.RspPcCount[i] > 0) order.Add(i);
            order.Sort((a, b) => Telemetry.RspPcCount[b].CompareTo(Telemetry.RspPcCount[a]));
            for (int i = 0; i < Math.Min(25, order.Count); i++) sb.AppendLine("pc 0x" + (order[i] * 4).ToString("X3") + " exec " + Telemetry.RspPcCount[order[i]]);
            sb.AppendLine("--- IMEM disasm 0x000-0x200 ---");
            for (long a = 0; a < 0x1000; a += 4)
            {
                uint w = rsp.IMEM.U32(a);
                string d; try { d = rsp.Disassembler.Disassemble(rsp.Disassembler.Decode(w)); } catch { d = "?"; }
                sb.AppendLine(a.ToString("X3") + ": " + w.ToString("X8") + "  " + d);
            }
            sb.AppendLine("--- RSP GPRs ---"); for (int g = 0; g < 32; g++) sb.AppendLine("r" + g + "=" + rsp.ReadGPR(g).ToString("X8"));
            sb.AppendLine("--- DMEM 0xFC0-0xFFF ---");
            for (long a = 0; a < 0x1000; a += 4) sb.AppendLine(a.ToString("X3") + ": " + rsp.DMEM.U32(a).ToString("X8"));
            File.WriteAllText($"telemetry/rsp_diag_{n}.txt", sb.ToString());
            unsafe { var ptr = (byte*)m_Sys.SystemMemory.RDRAM.GetRamPointer(0); var buf = new byte[0x800000]; System.Runtime.InteropServices.Marshal.Copy((IntPtr)ptr, buf, 0, buf.Length); File.WriteAllBytes($"telemetry/rdram_{n}.bin", buf); }
        }

        private void Run()
        {
            Directory.CreateDirectory("telemetry");
            EventLog.Open("telemetry/events.log");
            unsafe { Telemetry.RdramPtr = (byte*)m_Sys.SystemMemory.RDRAM.GetRamPointer(0); }
            using var log = new StreamWriter("telemetry/telemetry.jsonl", false) { AutoFlush = true };
            long lastTicks = 0, lastVi = 0, lastAi = 0, lastAiB = 0, lastRsp = 0, lastRdp = 0;
            var t0 = DateTime.UtcNow; var last = t0; int n = 0;
            while (!m_Stop)
            {
                Thread.Sleep(1000); EventLog.Flush();
                var now = DateTime.UtcNow; double dt = (now - last).TotalSeconds; last = now; n++;
                long t = Telemetry.Ticks, vi = Telemetry.ViInterrupts, ai = Telemetry.AiLenWrites, aib = Telemetry.AiBytes, rsp = Telemetry.RspStarts, rdp = Telemetry.RdpListRuns;
                ulong pc = 0; try { pc = (ulong)m_Sys.DeviceCPU.ReadPC(); } catch { }
                var v = m_Sys.VI;
                var pcs = new System.Collections.Generic.HashSet<long>(); for (int k = 0; k < 400; k++) { pcs.Add(m_Sys.DeviceCPU.ReadPC()); Thread.SpinWait(2000); }
                string inst = ""; try { var c2 = m_Sys.DeviceCPU.CurrentInst; inst += ",\"instWord\":\"0x" + c2.Inst.inst.ToString("X8") + "\",\"opFamily\":\"" + c2.Op.Family + "\",\"opName\":\"" + c2.Op.Op + "\""; } catch (Exception e2) { inst += ",\"instWErr\":\"" + e2.Message + "\""; } try { var ci = m_Sys.DeviceCPU.CurrentInst; inst += ",\"inst\":\"" + m_Sys.DeviceCPU.Disassembler.Disassemble(ci) + "\",\"instWord\":\"0x" + ci.Inst.inst.ToString("X8") + "\",\"instInvalid\":" + (ci.IsInvalid ? "true" : "false"); } catch (Exception e) { inst += ",\"instErr\":\"" + e.Message + "\""; }
                string mem = ""; try { var sm = m_Sys.SystemMemory; var sb = new System.Text.StringBuilder(); for (long a = 0x04000040; a < 0x04000060; a += 4) sb.Append(sm.U32(a).ToString("X8") + " "); mem = ",\"dmem40\":\"" + sb + "\""; } catch (Exception e) { mem = ",\"memErr\":\"" + e.Message + "\""; }
                string dec = ""; try { var d = m_Sys.DeviceCPU.Disassembler; foreach (uint w in new uint[]{0x40806800,0x40804800,0x40805800,0x3C08A470,0x25080000,0x8D09000C}) dec += w.ToString("X8") + "=" + d.Disassemble(d.Decode(w)) + "; "; dec = ",\"decode\":\"" + dec + "\""; } catch (Exception e) { dec = ",\"decErr\":\"" + e.Message + "\""; }
                string cop = ""; try { var c0 = m_Sys.DeviceCPU.Cop0; ulong R(int r) => (ulong)c0.CpuRegisterRead(r); cop = $",\"sr\":\"0x{R(12):X}\",\"cause\":\"0x{R(13):X}\",\"epc\":\"0x{R(14):X}\",\"count\":\"0x{R(9):X}\",\"compare\":\"0x{R(11):X}\""; } catch (Exception e) { cop = ",\"copErr\":\"" + e.Message + "\""; }
                log.WriteLine("{" +
                    $"\"t\":{(now - t0).TotalSeconds:F1},\"ticksPerSec\":{(long)((t - lastTicks) / dt)},\"speedPct\":{((t - lastTicks) / dt) / 93750000.0 * 100:F2}," +
                    $"\"viPerSec\":{(vi - lastVi) / dt:F2},\"aiWritesPerSec\":{(ai - lastAi) / dt:F1},\"aiBytesPerSec\":{(long)((aib - lastAiB) / dt)}," +
                    $"\"rspStartsPerSec\":{(rsp - lastRsp) / dt:F1},\"rdpListsPerSec\":{(rdp - lastRdp) / dt:F1}," +
                    $"\"pc\":\"0x{pc:X}\",\"distinctPcs\":{pcs.Count},\"aiDac\":{Telemetry.AiDacRate},\"audioFrames\":{((AiDac)m_Sys.Apu).FramesPushed},\"audioNonSilent\":{((AiDac)m_Sys.Apu).NonSilentFrames},\"audioPeak\":{((AiDac)m_Sys.Apu).PeakAbs},\"aiBitRate\":{Telemetry.AiBitRate}," +
                    $"\"viCtrl\":\"0x{v.ControlReg.GetPixelMode():X}\",\"viW\":{v.Width},\"viH\":{v.Height},\"viOrigin\":\"0x{v.Origin:X}\",\"viActive\":{(v.IsVideoActive ? "true" : "false")}" +
                    cop + inst + mem + dec + "}");
                lastTicks = t; lastVi = vi; lastAi = ai; lastAiB = aib; lastRsp = rsp; lastRdp = rdp;
                if (n == 40 || n == 62) { try { RspDiag(n); } catch (Exception e) { File.WriteAllText("telemetry/rsp_diag.txt", "ERR " + e); } }
                if (n == (int.TryParse(Environment.GetEnvironmentVariable("COR64_RDP_CAPTURE_AT"), out var cap) ? cap : 38)) Telemetry.CaptureRdp = 6000;
                if (n % 10 == 0) { var sbc = new System.Text.StringBuilder(); foreach (var kv in Telemetry.RdpCmd) sbc.Append(kv.Key + '=' + kv.Value + ' '); File.AppendAllText("telemetry/rdp_cmds.txt", "t=" + n + " " + sbc + System.Environment.NewLine); }
                if (n % m_ShotEverySec == 0) { try { Shot($"telemetry/fb_{n:D4}.png"); } catch (Exception e) { log.WriteLine("{\"shotError\":\"" + e.Message.Replace("\"", "'") + "\"}"); } }
            }
        }
    }
}
