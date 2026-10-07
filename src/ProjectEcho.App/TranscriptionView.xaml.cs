using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProjectEcho.App;

public partial class TranscriptionView : UserControl
{
    public TranscriptionView()
    {
        InitializeComponent();
        ReviewBodyScroller.SizeChanged += (_, _) => ResizeTranscriptList();
        ReviewHeaderPanel.SizeChanged += (_, _) => ResizeTranscriptList();
        ReviewEditorPanel.SizeChanged += (_, _) => ResizeTranscriptList();
    }

    private void ResizeTranscriptList()
    {
        // Constrain the nested list so virtualization remains effective. Small
        // windows scroll the editing body while transport and status stay fixed.
        double height = Math.Max(100, ReviewBodyScroller.ActualHeight - ReviewHeaderPanel.ActualHeight - ReviewEditorPanel.ActualHeight - 2);
        if (Math.Abs(SegmentsBox.Height - height) > 0.5) SegmentsBox.Height = height;
    }

    internal event EventHandler? SegmentInvoked;
    internal event EventHandler? SpeakerSegmentInvoked;
    internal event EventHandler? CopySegmentRequested;

    private void Segment_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || !item.IsEnabled) return;
        // Invoke on every click, including an already-selected row. SelectionChanged
        // is intentionally only for editing; loading/searching must not autoplay.
        SegmentsBox.SelectedItem = item.DataContext;
        SegmentInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void Segments_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SegmentsBox.SelectedItem is not null)
        { CopySegmentRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (e.Key != Key.Enter || SegmentsBox.SelectedItem is null) return;
        SegmentInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SpeakerSegment_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { IsEnabled: true } item) return;
        SpeakerSegmentsBox.SelectedItem = item.DataContext;
        SpeakerSegmentInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SpeakerSegments_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SpeakerSegmentsBox.SelectedItem is null) return;
        SpeakerSegmentInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
