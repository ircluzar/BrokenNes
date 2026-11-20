# mkdmareg.py OUT.REG: turns on bus-master DMA for Windows 98's ESDI_506 on the emulated PIIX4 IDE (run in the guest: regedit /s OUT.REG, reboot).
# ESDI_506 uses DMA only when the channel's hdc class key holds IDEDMADriveN = binary 01 (and the channel's allocated config has the
# bus-master range, which our PCI.VXD/MF split already gives: E040-E047 primary, E048-E04F secondary). Nothing in the emulator was wrong:
# earlier .REG attempts were typed as "hdc\0001" through a tool that ate "\000" (octal NUL), so they wrote keys "hdc 0".
# Write the file from code with real backslashes (chr(92)) and CRLF. Proof 2026-11-19: 2797 DMA transfers / 13 MB per cold boot; a file copy
# through WRITE DMA is byte-identical.
import sys
B = chr(92)
s = 'REGEDIT4\r\n\r\n'
for k in ('0001', '0002'):    # Primary / Secondary IDE controller (dual fifo); check Class\hdc with creg.py if a disk numbers them differently
    s += f'[HKEY_LOCAL_MACHINE{B}System{B}CurrentControlSet{B}Services{B}Class{B}hdc{B}{k}]\r\n"IDEDMADrive0"=hex:01\r\n"IDEDMADrive1"=hex:01\r\n\r\n'
open(sys.argv[1], 'w', newline='').write(s)
