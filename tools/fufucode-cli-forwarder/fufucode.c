#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(void) {
    char exePath[MAX_PATH];
    DWORD len = GetModuleFileNameA(NULL, exePath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        fprintf(stderr, "fufucode: failed to get module file path\n");
        return 1;
    }

    // Strip file name: <installDir>\cli\fufucode.exe -> <installDir>\cli
    char* p = strrchr(exePath, '\\');
    if (p) *p = '\0';
    // Strip cli directory: <installDir>\cli -> <installDir>
    p = strrchr(exePath, '\\');
    if (p) *p = '\0';
    // Append target: <installDir>\app\Fufucode.Cli.exe
    if (strlen(exePath) + strlen("\\app\\Fufucode.Cli.exe") >= MAX_PATH) {
        fprintf(stderr, "fufucode: path too long\n");
        return 1;
    }
    strcat(exePath, "\\app\\Fufucode.Cli.exe");

    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    ZeroMemory(&pi, sizeof(pi));

    // Forward the full command line intact
    char* cmdLine = GetCommandLineA();

    if (!CreateProcessA(exePath, cmdLine, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
        DWORD err = GetLastError();
        fprintf(stderr, "fufucode: failed to execute '%s' (error %lu)\n", exePath, err);
        return (int)err;
    }

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)exitCode;
}
