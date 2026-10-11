using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Fufucode.App.Infrastructure;

public interface ITerminalSession : IDisposable
{
    event Action<string>? Output;
    event Action<int?, string?>? Exited;
    bool IsRunning { get; }
    void Start(string workingDirectory, int columns, int rows);
    void Write(string text);
    void Resize(int columns, int rows);
}

/// <summary>App-owned interactive shell. Its pipes are completely separate from the Pi JSONL process.</summary>
public sealed class ConPtyTerminalSession : ITerminalSession
{
    private readonly Channel<string> _writes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private FileStream? _input;
    private FileStream? _output;
    private SafeFileHandle? _process;
    private SafeFileHandle? _job;
    private IntPtr _pseudoConsole;
    private bool _disposed;
    private int _running;
    public event Action<string>? Output;
    public event Action<int?, string?>? Exited;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public void Start(string workingDirectory, int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("交互终端当前需要 Windows ConPTY。");
        if (IsRunning) return;
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        IntPtr attributes = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            Check(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            Check(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord((short)Math.Clamp(columns, 10, 400), (short)Math.Clamp(rows, 3, 160)), inputRead, outputWrite, 0, out _pseudoConsole));
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), _pseudoConsole, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx();
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // Null standard handles with STARTF_USESTDHANDLES prevent inherited redirected
            // parent pipes from bypassing the pseudoconsole (also used by Windows Terminal).
            startup.StartupInfo.Flags = 0x00000100;
            startup.AttributeList = attributes;
            var command = new StringBuilder($"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /Q /K chcp 65001");
            Check(CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000004, IntPtr.Zero, workingDirectory, ref startup, out var processInfo));
            _process = new SafeFileHandle(processInfo.Process, true);
            using var thread = new SafeFileHandle(processInfo.Thread, true);
            _job = CreateJobObject(IntPtr.Zero, null);
            Check(!_job.IsInvalid);
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // Kill child processes when this terminal is closed.
            var limitsSize = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var limitsPointer = Marshal.AllocHGlobal(limitsSize);
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, false);
                Check(SetInformationJobObject(_job, 9, limitsPointer, (uint)limitsSize));
                Check(AssignProcessToJobObject(_job, _process));
            }
            finally { Marshal.FreeHGlobal(limitsPointer); }
            _input = new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            _output = new FileStream(outputRead, FileAccess.Read, 4096, false); outputRead = null;
            Interlocked.Exchange(ref _running, 1);
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var outputStream = _output;
            var inputStream = _input;
            var processHandle = _process;
            _ = Task.Run(() => ReadOutput(outputStream));
            _ = Task.Run(() => WriteInput(inputStream));
            _ = Task.Run(() => WaitForExit(processHandle));
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
        }
    }

    public void Write(string text)
    {
        if (!IsRunning || _disposed || _input is null) return;
        _writes.Writer.TryWrite(text);
    }
    public void Resize(int columns, int rows)
    {
        if (_pseudoConsole == IntPtr.Zero || _disposed) return;
        Marshal.ThrowExceptionForHR(ResizePseudoConsole(_pseudoConsole, new Coord((short)Math.Clamp(columns, 10, 400), (short)Math.Clamp(rows, 3, 160))));
    }

    private void ReadOutput(FileStream output)
    {
        try
        {
            using var reader = new StreamReader(output, new UTF8Encoding(false), false, 4096, true);
            var chars = new char[2048];
            int count;
            while ((count = reader.Read(chars, 0, chars.Length)) > 0) Output?.Invoke(new string(chars, 0, count));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            if (!_disposed) Exited?.Invoke(null, error.Message);
        }
    }
    private async Task WriteInput(FileStream input)
    {
        try
        {
            await foreach (var text in _writes.Reader.ReadAllAsync())
            {
                if (_disposed) break;
                input.Write(Encoding.UTF8.GetBytes(text)); input.Flush();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            if (!_disposed) Exited?.Invoke(null, error.Message);
        }
    }
    private void WaitForExit(SafeFileHandle process)
    {
        WaitForSingleObject(process, uint.MaxValue);
        if (_disposed) return;
        int? code = GetExitCodeProcess(process, out var result) ? (int)result : null;
        Interlocked.Exchange(ref _running, 0);
        if (!_disposed) Exited?.Invoke(code, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _running, 0);
        _job?.Dispose(); _job = null;
        // A process suspended before job assignment still needs explicit termination on a startup failure.
        if (_process is { IsInvalid: false, IsClosed: false }) TerminateProcess(_process, 1);
        _writes.Writer.TryComplete();
        var input = _input; _input = null;
        var pseudoConsole = _pseudoConsole; _pseudoConsole = IntPtr.Zero;
        var output = _output; _output = null;
        var process = _process; _process = null;
        // ClosePseudoConsole may wait for its output pipe; leave the reader draining while closing.
        _ = Task.Run(() =>
        {
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            input?.Dispose(); output?.Dispose(); process?.Dispose();
        });
    }

    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord { public readonly short X, Y; public Coord(short x, short y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
}

/// <summary>Explicit screenshot/selftest backend; no shell or process is started.</summary>
public sealed class TerminalMockSession : ITerminalSession
{
    public event Action<string>? Output;
    public event Action<int?, string?>? Exited;
    public bool IsRunning { get; private set; }
    public void Start(string workingDirectory, int columns, int rows)
    {
        IsRunning = true;
        Output?.Invoke("\u001b[36m布局演示终端 · mock 输出\u001b[0m\r\n" + workingDirectory + "> dotnet build --no-restore\r\n  Fufucode.App -> bin/Debug/net10.0/Fufucode.App.dll\r\n\u001b[32m演示：构建成功，0 警告，0 错误\u001b[0m\r\n" + workingDirectory + "> ");
    }
    public void Write(string text) { if (IsRunning) Output?.Invoke(text + "\r\n\u001b[33m这是演示输入，未执行命令。\u001b[0m\r\n> "); }
    public void Resize(int columns, int rows) { }
    public void Dispose() { if (!IsRunning) return; IsRunning = false; Exited?.Invoke(0, null); }
}
