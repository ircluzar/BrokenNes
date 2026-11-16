"""Game-agnostic client for the BrokenNes MCP bridge (C:/BrokenNes-work/cc_pilot/bridge).

Bridge(pc="CORAL") runs the emulator on a fleet PC; Bridge() runs it here. Every output path
(record path/state_path, save_state path, screenshot path) is a LOCAL path: in --pc mode the
bridge writes it on the PC and copies it back; load_state path is pushed to the PC first.
"""
import json, os, subprocess

BRIDGE = os.environ.get("BN_BRIDGE", r"C:\BrokenNes-work\cc_pilot\bridge")
# the AutoTAS build of the MCP server on the fleet PCs (separate from the shared C:\brokennes\mcp\mcp)
# hosted by the Microsoft-signed kit dotnet.exe (Application Control blocks new unsigned exes; which DLLs pass depends on the PC:
# 2026-11-15 the AutoTAS build ran on PEARL only, blocked on OPAL CORAL ZIRCON SAPPHIRE EMERALD DIAMOND)
AUTOTAS_REMOTE_EXE = r"C:\brokennes\kit\dotnet\dotnet.exe C:\brokennes\autotas\mcp_fd\BrokenNes.Mcp.dll"


class BridgeError(RuntimeError):
    pass


class Bridge:
    def __init__(self, pc=None, remote_exe=AUTOTAS_REMOTE_EXE, max_steps=100000, exe=None):
        args = ["node", os.path.join(BRIDGE, "mcp.mjs"), "start", "--max-steps", str(max_steps)]
        if pc:
            args += ["--pc", pc]
            if remote_exe:
                args += ["--remote-exe", remote_exe]
        if exe:
            args += ["--exe", exe]
        r = subprocess.run(args, capture_output=True, text=True, timeout=180)
        if r.returncode:
            raise BridgeError("bridge start failed: " + r.stdout + r.stderr)
        self.port = json.loads(r.stdout.strip().splitlines()[-1])["port"]  # the bridge picks a free port
        self.pc = pc

    def call(self, tool, **args):
        return self.raw(tool, **args).get("text")

    def screenshot(self, session, dest, scale=1):
        """Writes the session's picture to a local PNG (the bridge brings pictures back inline)."""
        import shutil
        imgs = self.raw("screenshot", session=session, scale=scale).get("images") or []
        if not imgs: raise BridgeError("screenshot: no picture")
        os.makedirs(os.path.dirname(os.path.abspath(dest)), exist_ok=True)
        shutil.move(imgs[0], dest)
        return dest

    def raw(self, tool, **args):
        args = {k: v for k, v in args.items() if v is not None}
        r = subprocess.run(["node", os.path.join(BRIDGE, "mcp.mjs"), tool, json.dumps(args), "--port", str(self.port)],
                           capture_output=True, text=True, timeout=900)
        try:
            out = json.loads(r.stdout)
        except ValueError:
            raise BridgeError(f"{tool}: {r.stdout}{r.stderr}")
        if out.get("error") or out.get("isError"):
            raise BridgeError(f"{tool}: {json.dumps(out)[:2000]}")
        return out

    def stop(self):
        subprocess.run(["node", os.path.join(BRIDGE, "mcp.mjs"), "stop", "--port", str(self.port)], capture_output=True, timeout=120)

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.stop()
