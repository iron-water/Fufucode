using System;
using System.Linq;
using Avalonia;
using Fufucode.App.ViewModels;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private AttachedFile[] _previewFiles = [];
    private MainViewModel? _previewOwner;
    private InputViewModel? _attachmentInput;

    private void InitializeAttachmentReferences()
    {
        DataContextChanged += (_, _) => AttachReferenceInput();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_attachmentInput is not null) _attachmentInput.AttachmentAdded -= InsertAttachmentReference;
            _attachmentInput = null;
        };
        AttachedToVisualTree += (_, _) => AttachReferenceInput();
        AttachReferenceInput();
    }

    private void AttachReferenceInput()
    {
        if (_attachmentInput is not null) _attachmentInput.AttachmentAdded -= InsertAttachmentReference;
        _attachmentInput = (DataContext as MainViewModel)?.Input;
        if (_attachmentInput is not null) _attachmentInput.AttachmentAdded += InsertAttachmentReference;
    }

    private void InsertAttachmentReference(AttachedFile file, int number)
    {
        if (DataContext is not MainViewModel current) return;
        var text = current.Input.DraftText;
        var position = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        var marker = $"〔▧ {number:00}〕";
        // Insert at the caret; attaching a file must never replace selected user prose.
        current.Input.DraftText = text[..position] + marker + text[position..];
        InputBox.CaretIndex = position + marker.Length;
        InputBox.SelectionStart = InputBox.SelectionEnd = InputBox.CaretIndex;
    }

    private void RefreshAttachmentPreviews(MainViewModel vm)
    {
        var files = vm.Input.Attachments.ToArray();
        if (ReferenceEquals(_previewOwner, vm) && files.SequenceEqual(_previewFiles)) return;
        _previewOwner = vm;
        _previewFiles = files;
        AttachedFiles.Children.Clear();
        if (files.Length == 0) return;
        AttachedFiles.Children.Add(new AttachmentWallet
        {
            Files = files, Margin = new Thickness(0, 0, 0, 5),
            RemoveFile = file => vm.Input.RemoveAttachmentCommand.Execute(file),
            ReferenceFile = (file, number) => { InsertAttachmentReference(file, number); InputBox.Focus(); },
        });
    }
}
