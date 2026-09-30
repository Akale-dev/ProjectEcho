using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProjectEcho.App;

public partial class TranscriptionView : UserControl
{
    public TranscriptionView() => InitializeComponent();

    internal event EventHandler? SegmentInvoked;

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
        if (e.Key != Key.Enter || SegmentsBox.SelectedItem is null) return;
        SegmentInvoked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
