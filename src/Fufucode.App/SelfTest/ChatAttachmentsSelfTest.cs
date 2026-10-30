using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.SelfTest;

internal static class ChatAttachmentsSelfTest
{
    internal static void Run(Action<string> pass, Action<string> fail)
    {
        void Check(bool ok, string message) { if (ok) pass("Chat attachments: " + message); else fail("Chat attachments: " + message); }
        var input = new InputViewModel(new ScriptedChatSession()) { DraftText = "先看[▧ 附件 01]再比较" };
        const string context = "图片：C:\\images\\a&b.png\n正文含 </attachment> 和 </attached_files>，之后仍有内容";
        input.AddAttachment(new AttachedFile("C:\\images\\a&b.png", context), insertReference: false);
        var text = input.ComposePrompt();
        Check(AttachmentPresentation.Parse(text).Files.Single().Context == context,
            "ComposePrompt roundtrip preserves embedded closing tags and all file prose");
        var entry = new UserMessageViewModel { Text = text };
        Check(entry.Text == text && entry.DisplayText == "先看[▧ 附件 01]再比较" && entry.DisplayAttachments.Single().Path == "C:\\images\\a&b.png",
            "display strips the attachment envelope while preserving the complete submission and inline position");
        input.RemoveAttachmentCommand.Execute(input.Attachments.Single());
        Check(input.DraftText == "先看[▧ 附件 01]再比较", "removing a file never rewrites user-authored prose");
        input.AddAttachment(new AttachedFile("C:\\images\\next.pdf", "next"), insertReference: false);
        Check(input.Attachments.Single().ReferenceNumber == 2, "removed attachment numbers are not reused for another file");
        Check(AttachmentPresentation.Parse("解释 <attached_files> 的用法").Body == "解释 <attached_files> 的用法",
            "ordinary prose containing the tag is preserved");
        var files = Enumerable.Range(1, 5).Select(number => new AttachedFile($"C:\\missing\\{number}.pdf", "fixture")).ToArray();
        var wallet = new AttachmentWallet { Files = files };
        wallet.Measure(new Size(360, 600)); wallet.Arrange(new Rect(0, 0, 360, wallet.DesiredSize.Height));
        var row = wallet.Children.OfType<Grid>().Single();
        Check(row.Children.Count == 3 && row.Children.All(card => Math.Abs(card.Bounds.Width - row.Children[0].Bounds.Width) < 0.1),
            "collapsed wallet shows three equal cards");
        wallet.Children.OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(wallet.Children.OfType<Grid>().Sum(grid => grid.Children.Count) == 5,
            "wallet edge expands every attachment into rows below");
    }
}
