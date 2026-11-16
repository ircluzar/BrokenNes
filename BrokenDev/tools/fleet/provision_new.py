"""bring new PCs up to the same kit as the rest: python provision_new.py AMBER QUARTZ"""
import sys
sys.path.insert(0, r"E:\fleet")
from jobs import put  # noqa: E402

names = sys.argv[1:]
F = r"C:\BrokenNes-work\fleet"
items = [
    (F + r"\kit", r"C:\brokennes\kit"),
    (F + r"\kit_bizhawk\bizhawk", r"C:\brokennes\kit\bizhawk"),
    (F + r"\stage_dotnet\dotnet", r"C:\brokennes\kit\dotnet"),
    (r"C:\Program Files\PowerShell\7", r"C:\brokennes\kit\pwsh"),
    (F + r"\nested_stage\fork", r"C:\brokennes\nested\fork"),
    (F + r"\nested_stage\fork_x", r"C:\brokennes\nested\fork_x"),
    (F + r"\stage_mcp\CORAL", r"C:\brokennes\mcp"),
    (F + r"\stage_mcp2\CORAL", r"C:\brokennes\mcp2"),
    (F + r"\stage_g2s_ws", r"C:\brokennes\gg2sms\ws"),
    (r"C:\BrokenNes-work\bn_gg2sms\Workshop\bin\Release\net10.0-windows", r"C:\brokennes\gg2sms\ws_fd"),
    (F + r"\gg2sms_stage\ws.cmd", r"C:\brokennes\gg2sms"),
    (r"C:\BrokenNes-work\gg2sms_r3\BrokenDev\tools\nested-everything", r"C:\brokennes\gg2sms\BrokenDev\tools\nested-everything"),
    (r"C:\BrokenNes-work\gg2sms_r3\BrokenDev\projects\nested-gg2sms", r"C:\brokennes\gg2sms\BrokenDev\projects\nested-gg2sms"),
]
for src, dst in items:
    print(dst, put(src, dst, names), flush=True)
