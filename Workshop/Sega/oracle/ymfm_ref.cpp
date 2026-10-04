// ymfm_ref: the original ymfm YM2612 / YM3438 (BSD-3-Clause, Copyright (c) 2021 Aaron Giles) driven from a script, so the C# port (sega/Ym2612.cs) can be compared with it sample for sample.
// It is OUR code: the host half of ymfm_interface (timers and the busy flag counted in chip input clocks, exactly as the C# chip counts them), a script reader and a binary writer.
// ymfm itself stays in C:\BrokenNes-tools\sega\port-sources\ymfm and is compiled in by build-oracles.ps1.
//
// usage: ymfm_ref <script.txt> <out.bin>
//   script: first line "<variant> <chip clocks> <any-port>"  variant 0 = ym2612 (the discrete chip, with the ladder effect), 1 = ym3438; any-port 1 = status reads on every port answer the status
//           (ymfm itself answers port 0 only; the flag is the host's decode, as the C# chip does it)
//           then lines "W <chip clock> <port> <value>" and "R <chip clock> <port>" (a status read; the value goes to <out.bin>.reads as "<clock> <port> <value>")
//   out:    per sample (144 chip clocks): int32 left, int32 right, uint8 status (the register as a read at the end of the sample sees it)
//
// The event order is the C# chip's: a sample completes at chip clock 144 n; timers that expire at or before that clock fire first; an access lands in the sample whose interval holds it.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <memory>
#include "ymfm_opn.h"

struct event { long long clock; char kind; int port; int value; };

class host : public ymfm::ymfm_interface
{
public:
    long long now = 0;
    long long busy_end = 0;
    bool running[2] = { false, false };
    long long deadline[2] = { 0, 0 };

    virtual void ymfm_set_timer(uint32_t tnum, int32_t duration_in_clocks) override
    {
        if (duration_in_clocks < 0) running[tnum] = false;
        else { running[tnum] = true; deadline[tnum] = now + duration_in_clocks; }
    }
    virtual void ymfm_set_busy_end(uint32_t clocks) override { busy_end = now + clocks; }
    virtual bool ymfm_is_busy() override { return now < busy_end; }

    // fires every timer that expires at or before limit, earliest first
    void process_events(long long limit)
    {
        for (;;)
        {
            int t = -1;
            for (int i = 0; i < 2; i++)
                if (running[i] && deadline[i] <= limit && (t < 0 || deadline[i] < deadline[t])) t = i;
            if (t < 0) break;
            long long saved = now;
            now = deadline[t];
            m_engine->engine_timer_expired(t);
            now = saved;
        }
    }
};

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: ymfm_ref <script.txt> <out.bin>\n"); return 2; }
    FILE *in = fopen(argv[1], "r");
    if (!in) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    int variant = 0, anyPort = 0; long long clocks = 0;
    if (fscanf(in, "%d %lld %d", &variant, &clocks, &anyPort) != 3 || clocks <= 0) { fprintf(stderr, "bad script header\n"); return 2; }
    std::vector<event> ev;
    char tag[4]; long long c; int p, v;
    while (fscanf(in, "%3s", tag) == 1)
    {
        if (tag[0] == 'W') { if (fscanf(in, "%lld %d %d", &c, &p, &v) != 3) break; }
        else if (tag[0] == 'R') { if (fscanf(in, "%lld %d", &c, &p) != 2) break; v = 0; }
        else break;
        ev.push_back({ c, tag[0], p, v });
    }
    fclose(in);

    char readsName[1024];
    snprintf(readsName, sizeof readsName, "%s.reads", argv[2]);
    FILE *out = fopen(argv[2], "wb");
    FILE *reads = fopen(readsName, "w");
    if (!out || !reads) { fprintf(stderr, "cannot write output\n"); return 2; }

    host h;
    std::unique_ptr<ymfm::ym2612> chip2612;
    std::unique_ptr<ymfm::ym3438> chip3438;
    if (variant) chip3438.reset(new ymfm::ym3438(h)); else chip2612.reset(new ymfm::ym2612(h));
    ymfm::ym2612 &chip = variant ? static_cast<ymfm::ym2612 &>(*chip3438) : *chip2612;
    chip.reset();

    size_t next = 0;
    long long samples = clocks / 144;
    for (long long s = 0; s < samples; s++)
    {
        long long end = (s + 1) * 144;
        // accesses that land inside this sample's interval are applied before the sample is generated
        // (a C# chip applies an access at clock t after RunTo(t), which completes every sample ending at or before t)
        while (next < ev.size() && ev[next].clock < end)
        {
            // bring the host clock to the access, firing timers on the way
            h.process_events(ev[next].clock);
            h.now = ev[next].clock;
            if (ev[next].kind == 'W') chip.write(ev[next].port, (uint8_t)ev[next].value);
            else
            {
                uint8_t r = (ev[next].port & 3) == 0 || anyPort ? chip.read_status() : 0;
                fprintf(reads, "%lld %d %d\n", ev[next].clock, ev[next].port, (int)r);
            }
            next++;
        }
        h.process_events(end);
        h.now = end;
        ymfm::ym2612::output_data o;
        if (variant) chip3438->generate(&o, 1); else chip.generate(&o, 1);
        int32_t l = o.data[0], r = o.data[1];
        uint8_t st = chip.read_status();
        fwrite(&l, 4, 1, out); fwrite(&r, 4, 1, out); fwrite(&st, 1, 1, out);
    }
    fclose(out);
    fclose(reads);
    return 0;
}
