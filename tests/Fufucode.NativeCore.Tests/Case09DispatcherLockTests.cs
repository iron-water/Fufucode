using System.Reflection;
using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Case 09: deterministic cross-dispatcher path-lock test using the real ProductionToolDispatcher.
/// A reflection-held lease gates both editors; assertions prove read-match-commit serialization,
/// cancellation cleanup, write_file sharing, and lock-table draining.
/// </summary>
internal static class Case09DispatcherLockTests
{
    private static readonly Type DispatcherType = typeof(ProductionToolDispatcher);
    private static readonly MethodInfo AcquirePathAsync = DispatcherType.GetMethod(
        "AcquirePathAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("未找到 AcquirePathAsync：补丁未应用？");
    private static readonly FieldInfo PathLocksGate = DispatcherType.GetField(
        "PathLocksGate", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("未找到 PathLocksGate：补丁未应用？");
    private static readonly FieldInfo PathLocks = DispatcherType.GetField(
        "PathLocks", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("未找到 PathLocks：补丁未应用？");

    private static Checks _checks = null!;

    public static async Task RunAsync(Checks checks)
    {
        _checks = checks;
        var scratch = TestTemp.Create("09");
        var previousDataRoot = Environment.GetEnvironmentVariable("FUFUCODE_DATA_ROOT");
        // FileChangeJournal writes backups under the app data root; isolate from the real user root.
        Environment.SetEnvironmentVariable("FUFUCODE_DATA_ROOT", Path.Combine(scratch, "data"));
        var workspace = Path.Combine(scratch, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            await ConcurrentEditsAcrossDispatchersPreserveBothUpdatesAsync(workspace);
            await WriteWaitsAndCanceledWaiterReleasesLockTableAsync(workspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUFUCODE_DATA_ROOT", previousDataRoot);
            TestTemp.Delete(scratch);
        }
    }

    private static async Task ConcurrentEditsAcrossDispatchersPreserveBothUpdatesAsync(string workspace)
    {
        const string name = "shared.txt";
        var path = Path.Combine(workspace, name);
        await File.WriteAllTextAsync(path, "alpha\nbeta\n");

        var first = new ProductionToolDispatcher(workspace);
        var second = new ProductionToolDispatcher(workspace);
        using var gate = await AcquireAsync(path);
        _checks.Equal(1, LockUsers(path), "闸门持有后锁用户数为 1");

        var editA = Task.Run(() => first.DispatchAsync(EditCall("a", name, "alpha", "ALPHA")));
        var editB = Task.Run(() => second.DispatchAsync(new PureToolCall("b", "edit_file",
            JsonSerializer.Serialize(new
            {
                path = "nested/../" + (OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name),
                edits = new[] { new { old_string = "beta", new_string = "BETA" } },
            }))));

        await WaitUntilAsync(() => LockUsers(path) == 3, "两次编辑都到达同一规范路径锁");
        _checks.Check(!editA.IsCompleted && !editB.IsCompleted,
            "两个 dispatcher 的编辑都在闸门后等待，未越过锁读写");

        gate.Dispose();

        var results = await Task.WhenAll(editA, editB);
        _checks.Check(results.All(result => result.Success),
            "两个 dispatcher 的编辑均成功：" + string.Join(" | ", results.Select(result => result.Content)));
        var final = await File.ReadAllTextAsync(path);
        _checks.Check(final.Contains("ALPHA", StringComparison.Ordinal) &&
                      final.Contains("BETA", StringComparison.Ordinal),
            "读匹配写串行后两次更新都保留：" + JsonSerializer.Serialize(final));
        await WaitUntilAsync(() => LockTableCount() == 0, "编辑完成后锁表已清空");
    }

    private static async Task WriteWaitsAndCanceledWaiterReleasesLockTableAsync(string workspace)
    {
        const string name = "written.txt";
        var path = Path.Combine(workspace, name);
        await File.WriteAllTextAsync(path, "seed\n");

        var editor = new ProductionToolDispatcher(workspace);
        var writer = new ProductionToolDispatcher(workspace);
        var gate = await AcquireAsync(path);
        _checks.Equal(1, LockUsers(path), "写入测试闸门持有后锁用户数为 1");

        using var cancellation = new CancellationTokenSource();
        var canceledEdit = Task.Run(() => editor.DispatchAsync(
            EditCall("canceled", name, "seed", "canceled"), cancellation.Token));
        var write = Task.Run(() => writer.DispatchAsync(new PureToolCall("w", "write_file",
            JsonSerializer.Serialize(new { path = "sub/../" + name, content = "written\n" }))));

        await WaitUntilAsync(() => LockUsers(path) == 3, "被取消的编辑与写入都到达同一把锁");
        _checks.Check(!canceledEdit.IsCompleted && !write.IsCompleted,
            "取消前编辑和 write_file 都在闸门后等待（write_file 参与同一把锁）");

        cancellation.Cancel();
        _checks.Check(await CapturesCancellationAsync(canceledEdit),
            "取消等待中的编辑抛出 OperationCanceledException");
        await WaitUntilAsync(() => LockUsers(path) == 2, "取消的等待者归还锁引用，不泄漏");
        _checks.Check(!write.IsCompleted, "取消其它等待者不会提前放行 write_file");

        gate.Dispose();
        gate.Dispose(); // 幂等：重复释放不得二次 Release 或破坏引用计数。
        var writeResult = await write;
        _checks.Check(writeResult.Success, "闸门释放后 write_file 成功：" + writeResult.Content);
        _checks.Equal("written\n", await File.ReadAllTextAsync(path), "write_file 写入内容正确");
        await WaitUntilAsync(() => LockTableCount() == 0, "取消后锁表无残留条目");

        var followUp = await editor.DispatchAsync(EditCall("follow", name, "written", "final"));
        _checks.Check(followUp.Success && await File.ReadAllTextAsync(path) == "final\n",
            "取消/写入结束后同路径编辑仍可正常获取锁");
        await WaitUntilAsync(() => LockTableCount() == 0, "后续编辑完成后锁表仍为空");
    }

    private static PureToolCall EditCall(string id, string path, string oldString, string newString) =>
        new(id, "edit_file", JsonSerializer.Serialize(new
        {
            path,
            old_string = oldString,
            new_string = newString,
        }));

    private static async Task<IDisposable> AcquireAsync(string gatePath)
    {
        var task = (Task<IDisposable>)AcquirePathAsync.Invoke(null,
            new object?[] { gatePath, CancellationToken.None })!;
        return await task.ConfigureAwait(false);
    }

    private static string Canonical(string path) => Path.GetFullPath(path);

    private static int LockUsers(string path)
    {
        lock (PathLocksGate.GetValue(null)!)
        {
            var table = PathLocks.GetValue(null)!;
            var args = new object?[] { Canonical(path), null };
            var found = (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!;
            if (!found) return 0;
            return (int)args[1]!.GetType().GetField("Users")!.GetValue(args[1])!;
        }
    }

    private static int LockTableCount()
    {
        lock (PathLocksGate.GetValue(null)!)
        {
            var table = PathLocks.GetValue(null)!;
            return (int)table.GetType().GetProperty("Count")!.GetValue(table)!;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待超时：" + description);
            await Task.Delay(5);
        }
        Console.WriteLine("PASS " + description);
    }

    private static async Task<bool> CapturesCancellationAsync(Task task)
    {
        try
        {
            await task;
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
