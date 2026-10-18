using System.Diagnostics;
using Fufucode.Core;
using Fufucode.Pi;

internal static class PiShutdownTests
{
    public static async Task<int> RunAsync(string root, string node)
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + "shutdown barrier: " + name);
            if (!condition) failures++;
        }
        var work = Path.Combine(root, "tmp", "pi-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var script = Path.Combine(work, "held-stdin.mjs");
        await File.WriteAllTextAsync(script, """
            import fs from 'node:fs';
            import path from 'node:path';
            import readline from 'node:readline';
            const root = process.argv[2];
            const input = readline.createInterface({input: process.stdin});
            input.on('line', line => {
              const r = JSON.parse(line);
              if (r.type === 'hang') return;
              process.stdout.write(JSON.stringify({type:'response', command:r.type, id:r.id, success:true, data:{pid:process.pid}})+'\n');
            });
            input.on('close', () => fs.writeFileSync(path.join(root, 'stdin-ended'), 'closed'));
            setInterval(() => { if (fs.existsSync(path.join(root, 'release'))) process.exit(0); }, 20);
            """);
        foreach (var mode in new[] { "parallel-stop", "mixed-dispose", "forced-stop" })
        {
            var caseRoot = Path.Combine(work, mode); Directory.CreateDirectory(caseRoot);
            var client = await PiRpcClient.StartAsync(new PiRpcOptions(node,
                [script, caseRoot, "--model", "devin/swe-2"], caseRoot,
                ShutdownTimeout: TimeSpan.FromSeconds(mode == "forced-stop" ? 2 : 8)));
            var response = await client.RequestAsync("get_state");
            using var process = Process.GetProcessById(response.Data!.Value.GetProperty("pid").GetInt32());
            _ = process.Handle; // Retain our independent handle while the child still exists.
            var pending = client.RequestAsync("hang");
            var first = client.StopAsync();
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(caseRoot, "stdin-ended")) && deadline.Elapsed < TimeSpan.FromSeconds(1))
                    await Task.Delay(10);
                Check(File.Exists(Path.Combine(caseRoot, "stdin-ended")) && !process.HasExited && !first.IsCompleted,
                    "fixture is actually alive after stdin closes: " + mode);
                Check(client.ProcessExit is null && client.ExitCode is null,
                    "closing stdin is never recorded as process exit while the child is alive: " + mode);
                var second = client.StopAsync();
                Check(!second.IsCompleted && ReferenceEquals(first, second),
                    "every Stop caller receives the same pending termination task: " + mode);
                var dispose = mode == "mixed-dispose" ? client.DisposeAsync().AsTask() : null;
                var repeatedDispose = mode == "mixed-dispose" ? client.DisposeAsync().AsTask() : null;
                var contenders = Array.Empty<Task>();
                if (dispose is not null)
                {
                    Check(!dispose.IsCompleted && !repeatedDispose!.IsCompleted && ReferenceEquals(dispose, repeatedDispose),
                        "concurrent Dispose waits for the same shutdown instead of disposing live process handles");
                    var entered = 0;
                    contenders = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
                    {
                        var closing = i % 2 == 0 ? client.StopAsync() : client.DisposeAsync().AsTask();
                        Interlocked.Increment(ref entered);
                        await closing;
                    })).ToArray();
                    var wait = Stopwatch.StartNew();
                    while (Volatile.Read(ref entered) != contenders.Length && wait.Elapsed < TimeSpan.FromSeconds(2))
                        await Task.Delay(10);
                    Check(entered == 16 && contenders.All(task => !task.IsCompleted) && !process.HasExited,
                        "sixteen parallel Stop/Dispose callers all remain pending until the same actual exit");
                }
                if (mode != "forced-stop") await File.WriteAllTextAsync(Path.Combine(caseRoot, "release"), "release own fixture");
                try
                {
                    await Task.WhenAll(contenders.Concat(new[] { first, second, dispose ?? Task.CompletedTask, repeatedDispose ?? Task.CompletedTask }))
                        .WaitAsync(TimeSpan.FromSeconds(12));
                    Check(process.HasExited, "all successful shutdown awaits imply actual child exit: " + mode);
                    var exit = client.ProcessExit;
                    Check(exit is not null && exit.Generation == client.Generation && exit.ProcessId == process.Id &&
                        exit.ExitCode == process.ExitCode && exit.ObservedAtUtc != default && client.ExitCode == exit.ExitCode,
                        "exit observation binds the actual process, generation and exit code: " + mode);
                }
                catch (Exception error)
                { Check(false, "shutdown must not invalidate another caller's process handle: " + mode + ": " + error.GetBaseException().GetType().Name); }
                var ended = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (RpcException) { ended = true; }
                catch (Exception error) { Console.WriteLine("pending diagnostic: " + error.GetType().Name); }
                Check(ended, "original pending request still ends with the protocol failure: " + mode);
                var repeat = client.StopAsync();
                Check(ReferenceEquals(first, repeat) && repeat.IsCompletedSuccessfully,
                    "repeated Stop after exit returns the original successful barrier: " + mode);
                await client.DisposeAsync();
                await client.DisposeAsync();
                Check(client.ProcessExit?.ProcessId == process.Id && client.ExitCode == process.ExitCode,
                    "repeat disposal preserves the original observed exit after native handles close: " + mode);
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(caseRoot, "release"), "cleanup own fixture");
                try { await client.DisposeAsync(); }
                catch (Exception error) { Console.WriteLine("cleanup diagnostic: " + error.GetBaseException().GetType().Name); }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Console.WriteLine("shutdown evidence root: " + work);
        Console.WriteLine("Shutdown barrier failures: " + failures);
        return failures;
    }
}
