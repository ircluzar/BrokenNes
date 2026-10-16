"""Run one probe ROM on this emulator and on BlastEm, and read the results back.

Two channels carry a probe's answer out of BlastEm (a window program with no register dump):
  * the picture: its own screenshot (key P) is the 320x224 (or 256x224) framebuffer;
  * work RAM: a probe that stores its measurements in RAM at $FF0000 is paused and its quick-save state read; the 64 KB of RAM sit in the state at $10941 as a plain big-endian byte stream
    (found by writing a counting pattern and then a probe whose bytes were off by one; see docs/projects/sega-track-b3a.md).

Environment: BN_ORACLE_ROOT = a private working folder holding blastem\\ (a copy of BlastEm whose default.cfg points screenshot_path at be_shots and save_path at be_save/$ROMNAME), be_shots, be_save;
BN_ARTIFACTS = the --artifacts-path the Workshop was built with (default bn_sega_b3a). Drive BlastEm only while holding the oracle lock (Workshop\\Sega\\oracle-lock.ps1 -Acquire blastem).
"""
import os, subprocess, sys, shutil, struct, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get('BN_ORACLE_ROOT', HERE)
ART = os.environ.get('BN_ARTIFACTS', os.path.join(os.environ.get('LOCALAPPDATA', ''), 'VRUN_Nes_Dev_work', 'bn_sega_b3a'))
WORKSHOP = os.environ.get('BN_WORKSHOP', os.path.join(ART, 'bin', 'BrokenNes.Workshop', 'release', 'BrokenNes.Workshop.exe'))    # BN_WORKSHOP: another build (a saved baseline for before/after numbers)
STATE_RAM = 0x10941          # 68000 work RAM inside a BlastEm 1.0.0 quick-save state: 64 KB as a plain big-endian byte stream (word k at STATE_RAM + 2k, odd offset!)


def ours(rom_path, frames=60, region=None, png=None, ram=None, inputs=None, extra=(), timeout=300):
    """Run the ROM on this emulator; returns (exit code, stdout text). ram -> 64 KB of work RAM written to that path; png -> the last frame."""
    cmd = [WORKSHOP, '--md-run', rom_path, '--frames', str(frames), '--quiet', '--force']
    if region: cmd += ['--region', region]
    if png: cmd += ['--png', png]
    if ram: cmd += ['--ram-out', ram]
    if inputs: cmd += ['--input', inputs]
    cmd += list(extra)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, p.stdout + p.stderr


def blastem(rom_path, name, script='w1500,pause,snap', startup_ms=2500, out_root=None, cache=True):
    """Run BlastEm on the ROM with a bl.ps1 script; returns the output folder (s000.png, s000.state ...). The oracle is deterministic per ROM and script, so a finished run of the same ROM bytes and
    script is reused (cache=False forces a new run)."""
    import hashlib
    out = os.path.join(out_root or os.path.join(ROOT, 'out'), name)
    key = hashlib.sha1(open(rom_path, 'rb').read() + script.encode() + str(startup_ms).encode()).hexdigest()
    marker = os.path.join(out, 'run.key')
    if cache and os.path.exists(marker) and open(marker).read() == key: return out
    shutil.rmtree(out, ignore_errors=True)
    env = dict(os.environ, BN_ORACLE_ROOT=ROOT)
    for attempt in range(3):
        p = subprocess.run(['pwsh', '-NoProfile', '-File', os.path.join(HERE, 'bl.ps1'), '-Rom', rom_path, '-OutDir', out, '-Script', script, '-StartupMs', str(startup_ms)],
                           capture_output=True, text=True, timeout=300, env=env)
        if os.path.isdir(out) and os.listdir(out): break
        time.sleep(2)
    if os.path.isdir(out): open(marker, 'w').write(key)
    return out


STATE_BASE_SIZE = 141640     # the size of a BlastEm 1.0.0 Genesis quick-save when a section in front of the work RAM is at its smallest


def state_ram_offset(d):
    """Where the work RAM is in a state file. It is not fixed: a section in front of it (it looks like the VDP's write FIFO, 12 bytes an entry) is longer while the FIFO holds entries, and the state is as much longer;
    wave 5 found Comix Zone's states 12 to 48 bytes longer than the base size with the RAM that much later (comparing at $10941 then reported 13,000 differing bytes that were not there). Use the size to place it;
    when it is not a multiple of 12 above the base (games with a cartridge section after the RAM: 15 and 63 were seen) compare with ours and take the best offset (MdOracleCli --ramcmp does)."""
    extra = len(d) - STATE_BASE_SIZE
    return STATE_RAM + (extra - extra % 12 if extra > 0 else 0)


def state_ram(path):
    """The 32768 words of work RAM from a BlastEm state file."""
    d = open(path, 'rb').read()
    off = state_ram_offset(d)
    return list(struct.unpack('>%dH' % 32768, d[off:off + 65536]))


def ram_words(path):
    """The 32768 words of a RAM dump written by --md-run --ram-out (big-endian)."""
    d = open(path, 'rb').read()
    return list(struct.unpack('>%dH' % (len(d) // 2), d))


def both_ram(rom_path, name, frames=300, region=None, wait_ms=1500):
    """Run a RAM-reporting probe on both; returns (ours, blastem) word lists."""
    ours_ram = os.path.join(ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ours_ram), exist_ok=True)
    code, text = ours(rom_path, frames, region, ram=ours_ram)
    o = ram_words(ours_ram)
    out = blastem(rom_path, name, 'w%d,pause,snap' % wait_ms)
    st = os.path.join(out, 's000.state')
    b = state_ram(st) if os.path.exists(st) else None
    return o, b
