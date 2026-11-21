/* n98launch.c - the nano98 shell (SYSTEM.INI shell=C:\N98\N98LAUNCH.EXE). A Win32 GUI program, so no DOS box stays open.
 *
 * Host -> guest: the COMMAND DISK = the primary slave hard disk (BIOS drive 81h) with NO partition table, so Windows gives it no
 * drive letter (the CD keeps D:). Its first sectors hold plain text, read through VWIN32's INT 13h service:
 *     N98CMD1
 *     run=C:\SIERRA\HALF-L~1\HLDEMO.EXE
 *     args=-console +map hldemo1
 *     dir=C:\SIERRA\HALF-L~1
 *     exit=poweroff        (poweroff | shell | restart: what to do when the program ends)
 * Guest -> host: status lines on port E9h ("N98 ..."), written only by this program (deterministic):
 *     N98 READY | N98 NOCMD | N98 RUN <path> | N98 FAIL <error> | N98 EXIT <code> | N98 POWEROFF
 * No command disk: Explorer starts as the normal shell.
 * Build: tools/nano98/launcher/build.ps1 (Open Watcom, nt_win, runs on Windows 95/98).
 */
#include <windows.h>
#include <conio.h>
#include <string.h>
#include <stdio.h>

static void status(const char *s)
{
    while (*s) outp(0xe9, *s++);
    outp(0xe9, '\n');
}

#pragma pack(push, 1)
typedef struct { DWORD ebx, edx, ecx, eax, edi, esi, flags; } DIOC_REGS;
#pragma pack(pop)
#define VWIN32_DIOC_DOS_INT13 4

/* reads `count` sectors from CHS 0/0/1 of BIOS drive 81h; 0 on failure */
static int read_cmd_disk(char *buf, int count)
{
    HANDLE h = CreateFileA("\\\\.\\vwin32", 0, 0, NULL, 0, FILE_FLAG_DELETE_ON_CLOSE, NULL);
    DIOC_REGS r; DWORD got = 0; BOOL ok;
    if (h == INVALID_HANDLE_VALUE) return 0;
    memset(&r, 0, sizeof r);
    r.eax = 0x0200 | (count & 0xff);      /* AH=02 read, AL=count */
    r.ecx = 0x0001;                         /* cylinder 0, sector 1 */
    r.edx = 0x0081;                         /* head 0, drive 81h */
    r.ebx = (DWORD)buf;
    r.flags = 1;                            /* carry set: cleared on success */
    ok = DeviceIoControl(h, VWIN32_DIOC_DOS_INT13, &r, sizeof r, &r, sizeof r, &got, NULL);
    CloseHandle(h);
    return ok && !(r.flags & 1);
}

static char g_buf[8 * 512 + 1];

static const char *field(const char *name, char *out, int n)
{
    int ln = (int)strlen(name);
    const char *p = g_buf;
    out[0] = 0;
    while (*p) {
        if (strncmp(p, name, ln) == 0 && p[ln] == '=') {
            int i = 0; p += ln + 1;
            while (*p && *p != '\r' && *p != '\n' && i < n - 1) out[i++] = *p++;
            out[i] = 0; return out;
        }
        while (*p && *p != '\n') p++;
        if (*p) p++;
    }
    return out;
}

static void run_explorer(void)
{
    STARTUPINFOA si; PROCESS_INFORMATION pi;
    memset(&si, 0, sizeof si); si.cb = sizeof si;
    if (CreateProcessA(NULL, "EXPLORER.EXE", NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
}

int PASCAL WinMain(HINSTANCE hi, HINSTANCE hp, LPSTR cl, int show)
{
    char run[260], args[260], dir[260], ex[32], line[600];
    STARTUPINFOA si; PROCESS_INFORMATION pi; DWORD code = 0;
    memset(g_buf, 0, sizeof g_buf);
    if (!read_cmd_disk(g_buf, 8) || strncmp(g_buf, "N98CMD1", 7) != 0) { status("N98 NOCMD"); run_explorer(); return 0; }
    status("N98 READY");
    field("run", run, sizeof run); field("args", args, sizeof args); field("dir", dir, sizeof dir); field("exit", ex, sizeof ex);
    sprintf(line, "\"%s\" %s", run, args);
    memset(&si, 0, sizeof si); si.cb = sizeof si;
    sprintf(line + 300, "N98 RUN %s", run); status(line + 300);
    if (!CreateProcessA(run[0] ? run : NULL, line, NULL, NULL, FALSE, 0, NULL, dir[0] ? dir : NULL, &si, &pi)) {
        sprintf(line, "N98 FAIL %lu", GetLastError()); status(line); run_explorer(); return 1;
    }
    CloseHandle(pi.hThread);
    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &code); CloseHandle(pi.hProcess);
    sprintf(line, "N98 EXIT %lu", code); status(line);
    if (strcmp(ex, "shell") == 0) { run_explorer(); return 0; }
    status(strcmp(ex, "restart") == 0 ? "N98 RESTART" : "N98 POWEROFF");
    ExitWindowsEx(strcmp(ex, "restart") == 0 ? EWX_REBOOT : (EWX_SHUTDOWN | EWX_POWEROFF), 0);
    return 0;
}
