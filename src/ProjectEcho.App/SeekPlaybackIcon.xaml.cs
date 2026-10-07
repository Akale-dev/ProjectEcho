using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProjectEcho.App;

public partial class SeekPlaybackIcon : UserControl
{
    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(nameof(Seconds), typeof(int),
        typeof(SeekPlaybackIcon), new PropertyMetadata(5, Changed));
    public static readonly DependencyProperty ForwardProperty = DependencyProperty.Register(nameof(Forward), typeof(bool),
        typeof(SeekPlaybackIcon), new PropertyMetadata(true, Changed));
    public int Seconds { get => (int)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }
    public bool Forward { get => (bool)GetValue(ForwardProperty); set => SetValue(ForwardProperty, value); }
    public SeekPlaybackIcon() { InitializeComponent(); Refresh(); }
    private static void Changed(DependencyObject item, DependencyPropertyChangedEventArgs args) => ((SeekPlaybackIcon)item).Refresh();
    private void Refresh()
    {
        if (Arrow is null) return;
        Arrow.RenderTransform = new ScaleTransform(Forward ? 1 : -1, 1);
        // Center the actual digit ink, not the font's line box/baseline. Both
        // directions share the circle center (14,14); only the arrow is mirrored.
        var text = new FormattedText(Seconds.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            10, Brushes.Black, 1);
        Geometry glyph = text.BuildGeometry(new Point());
        Rect ink = glyph.Bounds;
        glyph.Transform = new TranslateTransform(14 - ink.Left - ink.Width / 2, 14 - ink.Top - ink.Height / 2);
        glyph.Freeze();
        NumberGlyph.Data = glyph;
    }
}
