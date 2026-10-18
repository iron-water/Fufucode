/* Fufucode native launcher.
 *
 * 无参 / --selftest  -> 启动 app\Fufucode.App.exe（GUI，无 console）
 * 其他参数           -> AttachConsole 后转发 app\Fufucode.Cli.exe（保持 Ctrl+C 语义）
 *
 * 编译为 windows 子系统：双击/开始菜单不产生黑框；作为快捷方式目标时 Windows
 * 把它当普通应用（有图标、版本信息、进"应用"列表）。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* 返回 exe 所在目录（即安装根目录，因为本 exe 放根目录）。 */
static int get_install_dir(char* out, size_t outSize) {
    DWORD len = GetModuleFileNameA(NULL, out, (DWORD)outSize);
    if (len == 0 || len >= outSize) return 0;
    char* p = strrchr(out, '\\');
    if (!p) return 0;
    *p = '\0';
    return 1;
}

/* 从完整命令行里剥掉 argv[0]，只留参数尾巴。 */
static const char* cmdline_tail(const char* cmd) {
    if (!cmd) return "";
    /* argv[0] 可能带引号也可能不带；跳过第一个 token。 */
    if (*cmd == '"') {
        const char* e = strchr(cmd + 1, '"');
        if (!e) return "";
        cmd = e + 1;
    } else {
        while (*cmd && *cmd != ' ') cmd++;
    }
    while (*cmd == ' ') cmd++;
    return cmd;
}

static int run_target(const char* targetPath, const char* args, int wantConsole) {
    char cmdLine[32768];
    int n = snprintf(cmdLine, sizeof(cmdLine), "\"%s\" %s", targetPath, args);
    if (n <= 0 || (size_t)n >= sizeof(cmdLine)) {
        fprintf(stderr, "fufucode: command line too long\n");
        return 1;
    }

    if (wantConsole) {
        /* 有父 console 就接（从 cmd/快捷方式/终端启动）；没有就由子进程自开。 */
        AttachConsole(ATTACH_PARENT_PROCESS);
    }

    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    /* 子进程继承本进程的 std handles；接了 console 时这就是父 shell 的句柄。 */
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput  = GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError  = GetStdHandle(STD_ERROR_HANDLE);
    ZeroMemory(&pi, sizeof(pi));

    if (!CreateProcessA(targetPath, cmdLine, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
        DWORD err = GetLastError();
        if (wantConsole) {
            fprintf(stderr, "fufucode: failed to launch '%s' (error %lu)\n", targetPath, err);
        } else {
            char msg[1024];
            snprintf(msg, sizeof(msg), "Failed to launch:\n%s\n\nError %lu", targetPath, err);
            MessageBoxA(NULL, msg, "Fufucode", MB_ICONERROR | MB_OK);
        }
        return (int)err;
    }

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR lpCmdLine, int nShow) {
    (void)hInst; (void)hPrev; (void)nShow;

    char installDir[MAX_PATH];
    if (!get_install_dir(installDir, sizeof(installDir))) {
        MessageBoxA(NULL, "Cannot resolve install directory.", "Fufucode", MB_ICONERROR);
        return 1;
    }

    const char* args = cmdline_tail(GetCommandLineA());

    /* 判 GUI：无参、或第一个参数是 --selftest / --headless-ui 都走 App.exe（GUI 子系统无 console）。 */
    int gui = (*args == '\0')
              || strncmp(args, "--selftest", 10) == 0
              || strncmp(args, "--headless-ui", 13) == 0;

    char target[MAX_PATH];
    const char* exe = gui ? "Fufucode.App.exe" : "Fufucode.Cli.exe";
    if (snprintf(target, sizeof(target), "%s\\app\\%s", installDir, exe) < 0) {
        return 1;
    }
    return run_target(target, args, /*wantConsole=*/!gui);
}
