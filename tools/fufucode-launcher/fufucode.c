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

/*
 * 数据根隔离：安装根目录下的 data-root.txt（若存在）指名本安装的独立数据根。
 * 实验性测试包用它把会话/设置写到 %LOCALAPPDATA%\FufucodeTest，与正式安装并存；
 * 正式包不带该文件，这里不设置任何环境变量，行为与历史一致。
 * 显式设置了 FUFUCODE_DATA_ROOT 的用户优先，不被覆盖。
 */
static void apply_package_data_root(const char* installDir) {
    char marker[MAX_PATH];
    if (snprintf(marker, sizeof(marker), "%s\\data-root.txt", installDir) <= 0) return;
    FILE* file = fopen(marker, "rb");
    if (!file) return;

    char value[MAX_PATH];
    size_t read = fread(value, 1, sizeof(value) - 1, file);
    fclose(file);
    value[read] = '\0';

    /* 去掉 BOM、首尾空白与换行。 */
    char* start = value;
    if ((unsigned char)start[0] == 0xEF && (unsigned char)start[1] == 0xBB && (unsigned char)start[2] == 0xBF) start += 3;
    while (*start == ' ' || *start == '\t' || *start == '\r' || *start == '\n') start++;
    char* end = start + strlen(start);
    while (end > start && (end[-1] == ' ' || end[-1] == '\t' || end[-1] == '\r' || end[-1] == '\n')) end--;
    *end = '\0';
    if (*start == '\0') return;

    char current[MAX_PATH];
    DWORD currentLen = GetEnvironmentVariableA("FUFUCODE_DATA_ROOT", current, (DWORD)sizeof(current));
    if (currentLen > 0) return;

    /* 允许相对名字：相对于 %LOCALAPPDATA%。 */
    char expanded[MAX_PATH];
    if (start[1] != ':' && start[0] != '\\') {
        char localAppData[MAX_PATH];
        DWORD n = GetEnvironmentVariableA("LOCALAPPDATA", localAppData, (DWORD)sizeof(localAppData));
        if (n == 0 || n >= sizeof(localAppData)) return;
        if (snprintf(expanded, sizeof(expanded), "%s\\%s", localAppData, start) <= 0) return;
    } else {
        if (snprintf(expanded, sizeof(expanded), "%s", start) <= 0) return;
    }
    SetEnvironmentVariableA("FUFUCODE_DATA_ROOT", expanded);
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

    apply_package_data_root(installDir);

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
