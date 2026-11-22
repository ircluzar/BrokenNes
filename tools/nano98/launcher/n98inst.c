/* n98inst.c - the nano98 INSTALL SHELL (N98INST.EXE). The host's install wizard (N98Install.cs) boots the seat with the launcher command
 *     N98CMD1 / run=C:\N98\N98INST.EXE / exit=poweroff
 * and puts this program on C: through the command port's file records. It then stays up for the whole install session and takes REQUESTS
 * from the host: the host rewrites the command port text (port EAh: OUT rewinds, each IN returns the next byte, 0 at the end) with
 *     N98CMD1 ... (the launcher's lines, ignored here)
 *     iseq=<n>          a new request when n differs from the last one handled (kept in C:\N98\INST.SEQ, so a restart does not replay it)
 *     iact=run | poweroff | restart | explorer
 *     irun=E:\SETUP.EXE  iargs=...  idir=E:\
 * Status lines on port E9h (written only by this program):
 *     N98 I IDLE <seq>        ready for a request (once at start, and after each one is taken)
 *     N98 I RUN <path>        started             N98 I FAIL <error>      CreateProcess failed
 *     N98 I EXIT <code>       the started program ended (a setup that hands off to a child ends early: the host decides when setup is done)
 *     N98 I POWEROFF / N98 I RESTART
 * Build: tools/nano98/launcher/build.ps1 (Open Watcom, nt_win).
 */
#include <windows.h>
#include <conio.h>
#include <string.h>
#include <stdio.h>

static char g_buf[8 * 512 + 1];

static void status(const char *s) { while (*s) outp(0xe9, *s++); outp(0xe9, '\n'); }

static int read_cmd(void)
{
    int i = 0, c;
    outp(0xea, 0);
    while (i < (int)sizeof g_buf - 1 && (c = inp(0xea)) != 0 && c != 0xff) g_buf[i++] = (char)c;
    g_buf[i] = 0;
    return i;
}

static const char *field(const char *name, char *out, int n)
{
    int ln = (int)strlen(name); const char *p = g_buf;
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

static long load_seq(void)
{
    char t[32]; DWORD r = 0; HANDLE f = CreateFileA("C:\\N98\\INST.SEQ", GENERIC_READ, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    ReadFile(f, t, sizeof t - 1, &r, NULL); CloseHandle(f); t[r] = 0;
    return atol(t);
}

static void save_seq(long s)
{
    char t[32]; DWORD w; HANDLE f = CreateFileA("C:\\N98\\INST.SEQ", GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    sprintf(t, "%ld", s); WriteFile(f, t, (DWORD)strlen(t), &w, NULL); CloseHandle(f);
}

/* the seat base's pointer scheme is blank until a game starts (seat_look.py): the install needs the pointer, so the standard cursors come back in
 * memory (the registry keeps the blank scheme, see n98launch.c cursors_on) */
static void cursors_on(void)
{
    static const char *names[] = { "Arrow", "Wait", "AppStarting" };
    HKEY k; int i;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, "Control Panel\\Cursors", 0, KEY_ALL_ACCESS, &k) != ERROR_SUCCESS) return;
    for (i = 0; i < 3; i++) RegDeleteValueA(k, names[i]);
    SystemParametersInfoA(SPI_SETCURSORS, 0, NULL, 0);
    for (i = 0; i < 3; i++) RegSetValueExA(k, names[i], 0, REG_SZ, (const BYTE *)"C:\\N98\\BLANK.CUR", 17);
    RegCloseKey(k);
}

int PASCAL WinMain(HINSTANCE hi, HINSTANCE hp, LPSTR cl, int show)
{
    char v[260], run[260], args[260], dir[260], act[32], line[600];
    long last = load_seq(), seq;
    HANDLE child = NULL;
    cursors_on();
    sprintf(line, "N98 I IDLE %ld", last); status(line);
    for (;;) {
        Sleep(250);
        if (child != NULL && WaitForSingleObject(child, 0) == WAIT_OBJECT_0) {
            DWORD code = 0; GetExitCodeProcess(child, &code); CloseHandle(child); child = NULL;
            sprintf(line, "N98 I EXIT %lu", code); status(line);
        }
        if (!read_cmd()) continue;
        seq = atol(field("iseq", v, sizeof v));
        if (seq == 0 || seq == last) continue;   /* a new request = a new number (the host starts its numbers per session, so a loaded savestate still takes them) */
        last = seq; save_seq(seq);
        field("iact", act, sizeof act); field("irun", run, sizeof run); field("iargs", args, sizeof args); field("idir", dir, sizeof dir);
        if (strcmp(act, "poweroff") == 0 || strcmp(act, "restart") == 0) {
            int rs = strcmp(act, "restart") == 0;
            status(rs ? "N98 I RESTART" : "N98 I POWEROFF");
            ExitWindowsEx(rs ? EWX_REBOOT : (EWX_SHUTDOWN | EWX_POWEROFF), 0);
            continue;
        }
        if (strcmp(act, "explorer") == 0) { strcpy(run, "EXPLORER.EXE"); args[0] = 0; dir[0] = 0; }
        if (run[0]) {
            STARTUPINFOA si; PROCESS_INFORMATION pi;
            memset(&si, 0, sizeof si); si.cb = sizeof si;
            sprintf(line, "\"%s\" %s", run, args);
            if (CreateProcessA(NULL, line, NULL, NULL, FALSE, 0, NULL, dir[0] ? dir : NULL, &si, &pi)) {
                CloseHandle(pi.hThread);
                if (child != NULL) CloseHandle(child);
                child = pi.hProcess;
                sprintf(line, "N98 I RUN %s", run); status(line);
            } else { sprintf(line, "N98 I FAIL %lu", GetLastError()); status(line); }
        }
        sprintf(line, "N98 I IDLE %ld", last); status(line);
    }
    return 0;
}
