using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.App.Infrastructure;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.App.SelfTest;

/// <summary>FUMO 会话分叉：只能落在用户消息处，历史截断正确，父子关系落库。</summary>
internal static class ForkSelfTest
{
    public static async Task RunAsync(Action<string> pass, Action<string> fail)
    {
        void Check(bool value, string name) { if (value) pass("Fork: " + name); else fail("Fork: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-fork", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        try
        {
            var layout = new AppDataLayout(Path.Combine(root, "data"));
            var store = new DesktopMetadataStore(layout);
            var project = store.GetOrCreateProject(projectPath);
            var session = store.CreateSession(project, null, BackendId.NativeCSharp);
            var file = NativeSessionFactory.SessionFilePath(layout, project.Id, session.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                version = 2,
                messages = new[]
                {
                    new PureAgentMessage(PureAgentRole.User, "第一条用户消息"),
                    new PureAgentMessage(PureAgentRole.Assistant, "第一条回复"),
                    new PureAgentMessage(PureAgentRole.User, "第二条用户消息"),
                    new PureAgentMessage(PureAgentRole.Assistant, "第二条回复"),
                },
                thinkingLevel = "off",
            }));
            store.UpdateSessionFile(session.Id, file);

            var manager = new PersistentSessionManager(string.Empty, string.Empty, layout,
                requireApprovalBridge: false);
            var opened = await manager.ResumeAsync(session.Id);
            try
            {
                var result = await manager.ForkAsync(opened, "entry_2");
                var child = result.Session.Session;
                Check(child.ParentSessionId == session.Id && child.ForkEntryId == "entry_2" && child.IsFork,
                    "fork row records parent and entry");
                Check(child.Id != session.Id && child.ProjectId == project.Id, "fork gets its own session id in the same project");
                Check(child.FilePath is { Length: > 0 } childFile && File.Exists(childFile) && childFile != file,
                    "fork writes its own history file");
                var messages = PersistentSessionManager.LoadNativeMessages(child.FilePath!)!.Messages;
                Check(messages.Count == 3 && messages[^1].Content == "第二条用户消息",
                    "fork history is truncated at the chosen user message");
                Check(File.Exists(file), "parent history file is kept unchanged");
                await result.Session.DisposeAsync();
            }
            finally
            {
                await opened.DisposeAsync();
            }

            var reopened = await manager.ResumeAsync(session.Id);
            try
            {
                try
                {
                    await manager.ForkAsync(reopened, "entry_1");
                    Check(false, "fork at an assistant message is rejected");
                }
                catch (InvalidOperationException)
                {
                    Check(true, "fork at an assistant message is rejected");
                }
                try
                {
                    await manager.ForkAsync(reopened, "entry_99");
                    Check(false, "fork at an out-of-range entry is rejected");
                }
                catch (ArgumentOutOfRangeException)
                {
                    Check(true, "fork at an out-of-range entry is rejected");
                }
            }
            finally
            {
                await reopened.DisposeAsync();
            }
        }
        catch (Exception error)
        {
            fail("Fork: unexpected exception: " + error);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
