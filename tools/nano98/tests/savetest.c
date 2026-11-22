/* savetest.c: nano98 saves test game: C:/N98TEST/COUNT.TXT += 1, then exit 0 */
#include <windows.h>
#include <stdio.h>
int PASCAL WinMain(HINSTANCE a, HINSTANCE b, LPSTR c, int d)
{
    int n = 0; FILE *f;
    CreateDirectoryA("C:/N98TEST", NULL);
    f = fopen("C:/N98TEST/COUNT.TXT", "r"); if (f) { fscanf(f, "%d", &n); fclose(f); }
    f = fopen("C:/N98TEST/COUNT.TXT", "w"); if (f) { fprintf(f, "%d\n", n + 1); fclose(f); }
    return 0;
}
