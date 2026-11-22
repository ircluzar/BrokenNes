/* n98launch.c - the nano98 shell (SYSTEM.INI shell=C:\N98\N98LAUNCH.EXE). A Win32 GUI program, so no DOS box stays open.
 *
 * Host -> guest: the COMMAND PORT EAh (read-only text the host sets before power-on; a disk would take a drive letter and push
 * the CD off D:, and VWIN32's INT 13h service only reaches floppies). The text:
 *     N98CMD1
 *     run=C:\SIERRA\HALF-L~1\HLDEMO.EXE
 *     args=-console +map hldemo1
 *     dir=C:\SIERRA\HALF-L~1
 *     exit=poweroff        (poweroff | shell | restart: what to do when the program ends)
 *     save=C:\GAME\SAVE\*    (any number: the game's save files; * and ? in any path part, one level each)
 * After the text's terminating 0 byte the port carries the SAVED FILES to put back before the game starts:
 *     records of [u32 path length][path][u32 size][bytes], ended by a path length of 0.
 * When the game ends, every file matching a save= pattern goes back to the host: status line "N98 FILE <size> <path>", then its bytes
 * on port EBh; "N98 FILES END" closes the set (the host then keeps C: as it was at boot: the saves live in the host's saves folder).
 * Guest -> host: status lines on port E9h ("N98 ..."), written only by this program (deterministic):
 *     N98 BOOT (resume point) | N98 READY | N98 NOCMD | N98 RUN <path> | N98 FAIL <error> | N98 EXIT <code> | N98 POWEROFF
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

/* the command: host -> guest through the nano98 COMMAND PORT (EAh): OUT rewinds, each IN returns the next byte, 0 at the end */
static int read_cmd(char *buf, int n)
{
    int i = 0, c;
    outp(0xea, 0);
    while (i < n - 1 && (c = inp(0xea)) != 0 && c != 0xff) buf[i++] = (char)c;
    buf[i] = 0;
    return i;
}

static char g_buf[8 * 512 + 1];

static unsigned long in32(void) { unsigned long v = 0; int i; for (i = 0; i < 4; i++) v |= (unsigned long)inp(0xea) << (8 * i); return v; }

/* creates the folders of a path (C:\A\Bile -> C:\A, C:\A\B) */
static void make_dirs(const char *path)
{
    char t[260]; int i;
    strncpy(t, path, 259); t[259] = 0;
    for (i = 3; t[i]; i++) if (t[i] == '\\') { t[i] = 0; CreateDirectoryA(t, NULL); t[i] = '\\'; }
}

/* the saved files after the command text: written back onto the drives before the game starts */
static void restore_files(void)
{
    static unsigned char buf[32768];
    char path[260], line[300]; unsigned long n, size, got; int count = 0;
    for (;;) {
        n = in32();
        if (n == 0 || n >= sizeof path) break;
        for (got = 0; got < n; got++) path[got] = (char)inp(0xea);
        path[n] = 0;
        size = in32();
        make_dirs(path);
        {
            HANDLE f = CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            while (size > 0) {
                unsigned long k = size < sizeof buf ? size : sizeof buf, i; DWORD w;
                for (i = 0; i < k; i++) buf[i] = (unsigned char)inp(0xea);
                if (f != INVALID_HANDLE_VALUE) WriteFile(f, buf, k, &w, NULL);
                size -= k;
            }
            if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        }
        count++;
    }
    sprintf(line, "N98 RESTORED %d", count); status(line);
}

/* sends one file to the host: "N98 FILE <size> <path>" then the bytes on port EBh */
static void upload(const char *path)
{
    static unsigned char buf[32768];
    char line[320]; DWORD sz, r, i;
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    sz = GetFileSize(f, NULL);
    sprintf(line, "N98 FILE %lu %s", sz, path); status(line);
    while (sz > 0 && ReadFile(f, buf, sizeof buf, &r, NULL) && r > 0) { for (i = 0; i < r; i++) outp(0xeb, buf[i]); sz -= r; }
    while (sz > 0) { outp(0xeb, 0); sz--; }   /* (a file that shrank while read: keep the byte count promised) */
    CloseHandle(f);
}

/* expands a pattern part by part (wildcards in any part, one level each) and uploads the files it names */
static void upload_glob(const char *done, const char *rest)
{
    char part[260], pat[300], next[300]; const char *slash; WIN32_FIND_DATAA fd; HANDLE h; int last;
    while (*rest == '\\') rest++;
    slash = strchr(rest, '\\'); last = slash == NULL;
    strncpy(part, rest, last ? 259 : (size_t)(slash - rest)); part[last ? 259 : slash - rest] = 0;
    sprintf(pat, "%s\\%s", done, part);
    h = FindFirstFileA(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0) continue;
        sprintf(next, "%s\\%s", done, fd.cFileName);
        if (last) { if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) upload(next); }
        else if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) upload_glob(next, slash + 1);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}

static void upload_saves(void)
{
    const char *p = g_buf; char pat[260], drive[3];
    while (*p) {
        if (strncmp(p, "save=", 5) == 0) {
            int i = 0; p += 5;
            while (*p && *p != 13 && *p != 10 && i < 259) { pat[i] = *p == '/' ? '\\' : *p; i++; p++; }
            pat[i] = 0;
            if (i > 3 && pat[1] == ':') { drive[0] = pat[0]; drive[1] = ':'; drive[2] = 0; upload_glob(drive, pat + 3); }
        }
        while (*p && *p != '\n') p++;
        if (*p) p++;
    }
    status("N98 FILES END");
}


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

/* The seat base's cursor scheme is blank (Arrow / Wait / AppStarting = C:\N98\BLANK.CUR, seat_look.py) so no pointer shows from
 * boot to the game. cursors_on() brings the standard cursors back in memory: drop the values, have USER reload the scheme
 * (SPI_SETCURSORS), then put the values back so the disk (and the next boot) keeps the blank scheme. */
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
    char run[260], args[260], dir[260], ex[32], line[600];
    STARTUPINFOA si; PROCESS_INFORMATION pi; DWORD code = 0;
    memset(g_buf, 0, sizeof g_buf);
    status("N98 BOOT");   /* the host takes its resume snapshot here (at its next frame end), before the command is read */
    Sleep(1000);
    if (!read_cmd(g_buf, sizeof g_buf) || strncmp(g_buf, "N98CMD1", 7) != 0) { status("N98 NOCMD"); cursors_on(); run_explorer(); return 0; }
    restore_files();
    status("N98 READY");
    field("run", run, sizeof run); field("args", args, sizeof args); field("dir", dir, sizeof dir); field("exit", ex, sizeof ex);
    sprintf(line, "\"%s\" %s", run, args);
    memset(&si, 0, sizeof si); si.cb = sizeof si;
    sprintf(line + 300, "N98 RUN %s", run); status(line + 300);
    if (!CreateProcessA(run[0] ? run : NULL, line, NULL, NULL, FALSE, 0, NULL, dir[0] ? dir : NULL, &si, &pi)) {
        sprintf(line, "N98 FAIL %lu", GetLastError()); status(line); cursors_on(); run_explorer(); return 1;
    }
    CloseHandle(pi.hThread);
    WaitForInputIdle(pi.hProcess, 30000);   /* the game's window is up (it draws its own pointer, or uses the standard ones from here) */
    cursors_on();
    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &code); CloseHandle(pi.hProcess);
    upload_saves();
    sprintf(line, "N98 EXIT %lu", code); status(line);
    if (strcmp(ex, "shell") == 0) { cursors_on(); run_explorer(); return 0; }
    status(strcmp(ex, "restart") == 0 ? "N98 RESTART" : "N98 POWEROFF");
    ExitWindowsEx(strcmp(ex, "restart") == 0 ? EWX_REBOOT : (EWX_SHUTDOWN | EWX_POWEROFF), 0);
    return 0;
}
