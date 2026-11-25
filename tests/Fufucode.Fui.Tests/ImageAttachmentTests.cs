using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Security;
using static Fufucode.Fui.Tests.RuntimeTests;

namespace Fufucode.Fui.Tests;

internal static class ImageAttachmentTests
{
    // Independently encoded, one-pixel grayscale images. Compare the bytes on the HTTP wire and in the native asset store.
    internal static readonly ChatImage Png = new("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR42mPQAgAALAArYfKSawAAAABJRU5ErkJggg==", "image/png");
    private static readonly ChatImage Jpeg = new("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAB//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKX//2Q==", "image/jpeg");

    public static async Task RunAsync(string project, AppDataLayout layout, string credential, CancellationToken ct)
    {
        var dir = Directory.CreateDirectory(Path.Combine(project, "images")).FullName;
        var png = Convert.FromBase64String(Png.Data);
        var jpeg = Convert.FromBase64String(Jpeg.Data);
        await File.WriteAllBytesAsync(Path.Combine(dir, "screen.PNG"), png, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, "photo.jpeg"), jpeg, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "测试笔记", ct);
        var files = new ProjectFiles(project, layout.Root);
        var screen = await files.ReadAttachmentAsync("images/screen.PNG", ct);
        var photo = await files.ReadAttachmentAsync("images/photo.jpeg", ct);
        var notes = await files.ReadAttachmentAsync("images/notes.txt", ct);
        Check(screen is { MimeType: "image/png", Text: "" } && screen.Digest.SequenceEqual(SHA256.HashData(png)) &&
            photo is { MimeType: "image/jpeg", Text: "" } && notes.MimeType is null, "incorrect image or text descriptor");
        await Reject<InvalidDataException>(() => files.ReadAsync(screen.Path, ct));
        var only = await files.PrepareAsync("", [screen], ct);
        var mixed = await files.PrepareAsync("看图", [photo, notes, screen], ct);
        Check(only.Text == "" && only.Images.SequenceEqual([Png]), "pure image preparation fabricated text or changed bytes");
        Check(mixed.Text == "看图\n\n[项目文件：images/notes.txt]\n测试笔记\n[文件结束]" && mixed.Images.SequenceEqual([Jpeg, Png]),
            "mixed image context entered text or lost ordering");
        await CheckFileProtectionsAsync(files, screen, dir, layout, png, jpeg, ct);

        await using var endpoint = new Loopback
        {
            Models = """{"data":[{"id":"fixture-text","modalities":{"input":["text"]}},{"id":"fixture-vision","architecture":{"input_modalities":["text","image"]}},{"id":"fixture-delayed","modalities":{"input":["image"]}}]}"""
        };
        new AppSettingsStore(layout).Update(s => s with { Endpoint = new("image-fixture", endpoint.Url, "fixture-text", credential) });
        var metadata = new DesktopMetadataStore(layout);
        string id;
        string storePath;
        await using (var session = await FumoTuiSession.OpenAsync(project, layout.Root, null, false, ct))
        {
            id = session.Id;
            storePath = metadata.GetSession(id).FilePath!;
            var media = (IChatAttachments)session;
            await RejectUnsupported(media, only, ct);
            Check(!media.CanSendImages && endpoint.MessageArrays.IsEmpty && !Directory.Exists(storePath + ".assets"),
                "text-only model accepted an image request or wrote a native asset");
            await session.ExecuteAsync("model", "fixture-vision", ct);
            endpoint.When("", false, Frame(new { content = "received image" }, "stop"));
            await media.SendWithImagesAsync(only.Text, only.Images, ct);
            var run = await Settled(session, ct);
            Check(media.CanSendImages && run.OfType<TuiEvent.Message>().Any(m => m.Kind == "user" && m.Text == "[图片 × 1]") &&
                run.OfType<TuiEvent.Message>().Any(m => m.Text == "received image"), "pure image live transcript or response missing");
            CheckParts(LastUser(endpoint.MessageArrays.Single()), "", [Png]);

            await session.ExecuteAsync("model", "fixture-text", ct);
            await RejectUnsupported(media, mixed, ct);
            Check(!media.CanSendImages && endpoint.MessageArrays.Count == 1, "switched text-only model accepted old vision capability");
            await session.ExecuteAsync("model", "fixture-vision", ct);
            endpoint.When(mixed.Text, false, Frame(new { content = "received mixed" }, "stop"));
            await media.SendWithImagesAsync(mixed.Text, mixed.Images, ct);
            await Settled(session, ct);
            CheckParts(LastUser(endpoint.MessageArrays.Last()), mixed.Text, [Jpeg, Png]);
            Check(endpoint.Choices.All(c => c.Model == "fixture-vision"), "image reached the wrong model");
        }

        var persistedText = await File.ReadAllTextAsync(storePath, ct);
        Check(!persistedText.Contains(Png.Data) && !persistedText.Contains(Jpeg.Data), "native history inlined image payloads");
        using (var file = File.OpenRead(storePath))
        {
            var users = PersistentSessionManager.ParseNativeMessages(file)!.Messages.Where(m => m.Role == PureAgentRole.User).ToArray();
            Check(users.Length == 2 && users[0].Content == "" && users[1].Content == mixed.Text, "native history changed user text");
            var saved = users.SelectMany(m => m.Images!).ToArray();
            Check(saved.Length == 3 && Directory.GetFiles(storePath + ".assets").Length == 2, "native asset counts incorrect");
            foreach (var (attachment, expected) in saved.Zip(new[] { Png, Jpeg, Png }))
            {
                Check(attachment is { Base64Data.Length: 0, FilePath: not null } && attachment.MimeType == expected.MimeType &&
                    Path.GetDirectoryName(attachment.FilePath) == storePath + ".assets" &&
                    Convert.ToBase64String(await File.ReadAllBytesAsync(attachment.FilePath, ct)) == expected.Data,
                    "native file reference or asset bytes changed");
            }
        }

        await using (var resumed = await FumoTuiSession.OpenAsync(project, layout.Root, id, false, ct))
        {
            Check(resumed.Model == "fixture-vision" && resumed.History.Any(l => l is { Kind: "user", Text: "[图片 × 1]" }) &&
                resumed.History.Any(l => l.Kind == "user" && l.Text == mixed.Text + "\n[图片 × 2]"), "image markers lost on resume");
            var exported = await resumed.ExecuteAsync("export", "", ct);
            Check((await File.ReadAllTextAsync(exported.Notice!, ct)).Contains("[图片 × 2]"), "export lost image marker");
            endpoint.When("resume", false, Frame(new { content = "restored assets" }, "stop"));
            await resumed.SendAsync("resume", ct);
            await Settled(resumed, ct);
            var replay = endpoint.MessageArrays.Last().EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").ToArray();
            CheckParts(replay[0].GetProperty("content"), "", [Png]);
            CheckParts(replay[1].GetProperty("content"), mixed.Text, [Jpeg, Png]);
            await CheckTurnChangedDuringProbeAsync(resumed, endpoint, only, ct);
            await resumed.DisposeAsync();
            await Reject<ObjectDisposedException>(() => resumed.SendWithImagesAsync(only.Text, only.Images, ct));
            Check(endpoint.MessageArrays.Count == 4, "closed session submitted image");
        }
        Console.WriteLine("PASS project images: format, byte limits, permissions, native worker gates, model switching, HTTP parts, persisted assets and replay");
    }

    private static async Task CheckTurnChangedDuringProbeAsync(FumoTuiSession session, Loopback endpoint, FuiPrompt image, CancellationToken ct)
    {
        await session.ExecuteAsync("model", "fixture-delayed", ct);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.HoldModelList = async token =>
        {
            endpoint.HoldModelList = null;
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        };
        var delayed = session.SendWithImagesAsync(image.Text, image.Images, ct);
        try
        {
            await arrived.Task.WaitAsync(ct);
            endpoint.When("intervening turn", false, Frame(new { content = "finished before image lookup" }, "stop"));
            await session.SendAsync("intervening turn", ct);
            await Settled(session, ct);
        }
        finally { release.TrySetResult(); endpoint.HoldModelList = null; }
        try { await delayed; }
        catch (InvalidOperationException e) when (e.Message.Contains("回合已切换"))
        {
            Check(endpoint.Choices.Count == 4 && endpoint.Choices.Last().Prompt == "intervening turn" &&
                (await session.ExecuteAsync("queue", "", ct)).Notice == "队列为空", "image reached the next turn or queue");
            return;
        }
        throw new InvalidOperationException("late image capability check was accepted for a different turn");
    }

    private static async Task CheckFileProtectionsAsync(ProjectFiles files, FileAttachment screen, string dir, AppDataLayout layout,
        byte[] png, byte[] jpeg, CancellationToken ct)
    {
        var path = Path.Combine(dir, "screen.PNG");
        await File.WriteAllBytesAsync(path, jpeg, ct);
        await Reject<InvalidDataException>(() => files.PrepareAsync("", [screen], ct));
        await File.WriteAllBytesAsync(path, png[..^3], ct);
        await Reject<InvalidDataException>(() => files.ReadAttachmentAsync(screen.Path, ct));
        await File.WriteAllBytesAsync(path, png, ct);
        var editedPath = Path.Combine(dir, "edited.jpg");
        await File.WriteAllBytesAsync(editedPath, jpeg, ct);
        var edited = await files.ReadAttachmentAsync("images/edited.jpg", ct);
        await File.WriteAllBytesAsync(editedPath, [.. jpeg[..2], 0xff, 0xfe, 0, 3, 42, .. jpeg[2..]], ct);
        await Reject<IOException>(() => files.PrepareAsync("", [edited], ct));
        await File.WriteAllTextAsync(Path.Combine(dir, "fake.jpg"), "this is text", ct);
        await Reject<InvalidDataException>(() => files.ReadAttachmentAsync("images/fake.jpg", ct));
        await File.WriteAllBytesAsync(Path.Combine(dir, "short.jpg"), [0xff, 0xd8, 0xff, 0xd9], ct);
        await Reject<InvalidDataException>(() => files.ReadAttachmentAsync("images/short.jpg", ct));
        await Reject<UnauthorizedAccessException>(() => files.ReadAttachmentAsync("../outside.png", ct));
        await Reject<ArgumentException>(() => files.ReadAttachmentAsync(path, ct));
        await Reject<InvalidOperationException>(() => files.PrepareAsync("", [screen, screen, screen, screen, screen], ct));
        await Reject<OperationCanceledException>(() => files.PrepareAsync("", [screen], new CancellationToken(true)));

        var policies = new SecurityPolicyStore(layout.Root);
        var original = policies.Current;
        try
        {
            foreach (var action in new[] { SecurityAction.Ask, SecurityAction.Deny })
            {
                policies.Update(s => s with { FileRules = [new(screen.Path, action), .. original.FileRules] });
                await Reject<UnauthorizedAccessException>(() => files.ReadAttachmentAsync(screen.Path, ct));
                await Reject<UnauthorizedAccessException>(() => files.PrepareAsync("", [screen], ct));
            }
        }
        finally { policies.Update(_ => original); }
        var alias = Path.Combine(dir, "alias");
        Directory.CreateDirectory(alias);
        var aliasFile = Path.Combine(alias, "screen.PNG");
        await File.WriteAllBytesAsync(aliasFile, png, ct);
        var prior = await files.ReadAttachmentAsync("images/alias/screen.PNG", ct);
        File.Delete(aliasFile);
        Directory.Delete(alias);
        try { Directory.CreateSymbolicLink(alias, dir); }
        catch (IOException e) when (OperatingSystem.IsWindows() && (e.HResult & 0xffff) == 1314)
        { Console.WriteLine("SKIP image symlink fixture: Windows did not grant symbolic link creation"); }
        if (Directory.Exists(alias))
        {
            try
            {
                await Reject<UnauthorizedAccessException>(() => files.ReadAttachmentAsync(prior.Path, ct));
                await Reject<UnauthorizedAccessException>(() => files.PrepareAsync("", [prior], ct));
            }
            finally { Directory.Delete(alias); }
        }

        var largePath = Path.Combine(dir, "large.jpg");
        using (var output = File.Create(largePath))
        {
            output.Write(jpeg, 0, 2);
            var comment = new byte[65537]; // FF FE, a JPEG segment length of 65535, then the comment payload.
            comment[0] = comment[2] = comment[3] = 0xff; comment[1] = 0xfe;
            while (output.Length < ProjectFiles.MaxAttachmentBytes / 2) output.Write(comment);
            output.Write(jpeg, 2, jpeg.Length - 2);
        }
        var large = await files.ReadAttachmentAsync("images/large.jpg", ct);
        var secondPath = Path.Combine(dir, "second.jpg");
        File.Copy(largePath, secondPath);
        var second = await files.ReadAttachmentAsync("images/second.jpg", ct);
        await Reject<IOException>(() => files.PrepareAsync("", [large, second], ct));
        using (var output = File.OpenWrite(largePath)) output.SetLength(ProjectFiles.MaxAttachmentBytes + 1L);
        await Reject<IOException>(() => files.ReadAttachmentAsync("images/large.jpg", ct));
    }

    private static async Task RejectUnsupported(IChatAttachments media, FuiPrompt prompt, CancellationToken ct)
    {
        try { await media.SendWithImagesAsync(prompt.Text, prompt.Images, ct); }
        catch (InvalidOperationException e) when (e.Message.Contains("fixture-text") && e.Message.Contains("不支持图片")) { return; }
        throw new InvalidOperationException("missing model-specific image refusal");
    }

    private static JsonElement LastUser(JsonElement array) => array.EnumerateArray()
        .Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content");

    private static void CheckParts(JsonElement content, string text, ChatImage[] expected)
    {
        var parts = content.EnumerateArray().ToArray();
        var offset = text.Length == 0 ? 0 : 1;
        Check(parts.Length == expected.Length + offset, "HTTP image message had missing or extra parts");
        if (offset != 0) Check(parts[0].GetProperty("type").GetString() == "text" && parts[0].GetProperty("text").GetString() == text,
            "HTTP user text differs from the draft");
        for (var i = 0; i < expected.Length; i++)
        {
            var part = parts[i + offset];
            Check(part.GetProperty("type").GetString() == "image_url" &&
                part.GetProperty("image_url").GetProperty("url").GetString() == $"data:{expected[i].MimeType};base64,{expected[i].Data}",
                "HTTP image MIME or base64 differs from the selected bytes");
        }
    }
}
