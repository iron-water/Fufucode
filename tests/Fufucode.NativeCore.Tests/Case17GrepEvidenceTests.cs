using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

internal static class Case17GrepEvidenceTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("grep-evidence");
        try
        {
            var dispatcher = new ProductionToolDispatcher(root);
            Task<PureToolResult> Search(string path, string pattern, int context = 0, int limit = 100, int offset = 0) =>
                dispatcher.DispatchAsync(new("grep-test", "grep_search", JsonSerializer.Serialize(new { path, pattern,
                    context_lines = context, max_matches = limit, offset })));

            var path = Path.Combine(root, "trace.txt");
            var tag = "Release_Tag=7a9b2e";
            await File.WriteAllTextAsync(path, new string('a', 62000) + tag + new string('b', 38000));
            foreach (var pattern in new[] { "release_tag=", @"Release_Tag=\w{6}" })
            {
                var evidence = await Search(path, pattern);
                checks.Check(evidence.Success && NativeAgentLoop.PruneForTest(evidence.Content).Contains(tag),
                    "the matched value in the middle of a long line survives the tool-to-model boundary");
                checks.Check(evidence.Content.Length < 4096 && evidence.Content.Contains('…'), "long grep line is an explicitly bounded excerpt");
            }

            await File.WriteAllTextAsync(path, "before\nhit A\nhit B\nbetween\nhit C\nafter\nlast\n");
            var around = (await Search(path, "hit", context: 1)).Content;
            checks.Check(around.Contains("3 处命中") && around.Contains("2: hit A") && around.Contains("3: hit B") && around.Contains("5: hit C"),
                "overlapping windows still mark every matching line as a match");
            checks.Check(around.Split("4- between").Length == 2 && around.Contains("6- after"), "overlapping context emitted once in line order");
            var last = (await Search(path, "hit", context: 1, limit: 1)).Content;
            checks.Check(last.Contains("1- before") && last.Contains("3- hit B") && !last.Contains("4- between"), "last allowed hit gets its requested trailing context");

            var dir = Directory.CreateDirectory(Path.Combine(root, "pages")).FullName;
            foreach (var name in new[] { "a", "b", "c" }) await File.WriteAllTextAsync(Path.Combine(dir, name + ".txt"), "match\n");
            for (var offset = 0; offset < 3; offset++)
            {
                var page = (await Search(dir, "match", limit: 1, offset: offset)).Content;
                checks.Check(page.Contains($"## pages/{(char)('a' + offset)}.txt") && page.Split("## ").Length == 2,
                    "file offset reaches the next unique group: " + offset);
                if (offset < 2) checks.Check(page.Contains($"offset: {offset + 1}"), "full page supplies an advancing file cursor");
            }
            checks.Check(!(await Search(dir, "match", limit: 1, offset: int.MaxValue)).Content.Contains("## "),
                "large offset cannot overflow or allocate proportional to the offset");
        }
        finally { TestTemp.Delete(root); }
    }
}
