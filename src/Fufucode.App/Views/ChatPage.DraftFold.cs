using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Fufucode.App.Views;

public partial class ChatPage
{
    private bool _draftUnfolded;
    private bool _updatingDraftFold;
    private const double FoldedDraftHeight = 160;

    private void InitializeDraftFold()
    {
        InputBox.GotFocus += (_, _) =>
        {
            if (DraftFoldButton.IsVisible) _draftUnfolded = true;
            UpdateDraftFold();
        };
        InputBox.LostFocus += (_, _) => UpdateDraftFold();
        InputBox.TextChanged += (_, _) => UpdateDraftFold();
        InputBox.SizeChanged += (_, _) => UpdateDraftFold();
        InputBox.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.CaretIndexProperty && InputBox.IsKeyboardFocusWithin &&
                DraftFoldButton.IsVisible && !_draftUnfolded)
            {
                _draftUnfolded = true;
                UpdateDraftFold();
            }
        };
        UpdateDraftFold();
    }

    private void DraftFoldButton_Click(object? sender, RoutedEventArgs e)
    {
        _draftUnfolded = !_draftUnfolded;
        UpdateDraftFold();
        e.Handled = true;
    }

    private void UpdateDraftFold()
    {
        if (_updatingDraftFold) return;
        _updatingDraftFold = true;
        try
        {
            var width = InputBox.Bounds.Width;
            if (width <= 0) return;
            var measure = new TextBlock
            {
                Text = InputBox.Text ?? string.Empty,
                FontFamily = InputBox.FontFamily,
                FontSize = InputBox.FontSize,
                TextWrapping = TextWrapping.Wrap,
            };
            measure.Measure(new Size(Math.Max(1, width - InputBox.Padding.Left - InputBox.Padding.Right), double.PositiveInfinity));
            var longDraft = measure.DesiredSize.Height + InputBox.Padding.Top + InputBox.Padding.Bottom > FoldedDraftHeight;
            if (!longDraft) _draftUnfolded = false;
            DraftFoldButton.IsVisible = longDraft;
            DraftBlurBand.IsVisible = longDraft && !_draftUnfolded && !InputBox.IsKeyboardFocusWithin;
            DraftBlurText.FontFamily = InputBox.FontFamily;
            DraftBlurText.FontSize = InputBox.FontSize;
            DraftBlurText.Padding = InputBox.Padding;
            DraftFoldButton.Content = _draftUnfolded ? "收起" : "展开";
            InputBox.MaxHeight = _draftUnfolded ? 420 : FoldedDraftHeight;
            InputBox.OpacityMask = longDraft && !_draftUnfolded
                ? new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = new GradientStops
                    {
                        new GradientStop(Colors.White, 0),
                        new GradientStop(Colors.White, .58),
                        new GradientStop(Colors.Transparent, 1),
                    },
                }
                : null;
        }
        finally { _updatingDraftFold = false; }
    }
}
