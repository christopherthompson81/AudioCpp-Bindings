using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AudioCpp.Bindings.Gui;

/// <summary>
/// A min/max envelope of audio, with a playhead and click-to-seek.
/// </summary>
/// <remarks>
/// Colours come from the theme rather than the literals this started with, so
/// the control follows a light/dark switch like everything else.
/// </remarks>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<float[]> SamplesProperty =
        AvaloniaProperty.Register<WaveformView, float[]>(nameof(Samples), []);

    /// <summary>Playhead as a fraction 0..1, or negative to hide it.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress), -1);

    /// <summary>Raised with a fraction 0..1 when the user clicks or drags.</summary>
    public static readonly StyledProperty<Action<double>?> SeekProperty =
        AvaloniaProperty.Register<WaveformView, Action<double>?>(nameof(Seek));

    static WaveformView() => AffectsRender<WaveformView>(SamplesProperty, ProgressProperty);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Colours are read at draw time, so a variant change needs a repaint;
        // without this the control keeps whichever palette it first drew with.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public float[] Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Action<double>? Seek
    {
        get => GetValue(SeekProperty);
        set => SetValue(SeekProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        SeekTo(e.GetPosition(this).X);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this)) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void SeekTo(double x)
    {
        if (Samples.Length == 0 || Bounds.Width <= 0) return;
        Seek?.Invoke(Math.Clamp(x / Bounds.Width, 0, 1));
    }

    /// <summary>
    /// Resolve a theme brush for the variant actually in force.
    /// </summary>
    /// <remarks>
    /// The variant matters: TryFindResource without one does not pick the
    /// light or dark dictionary, so the waveform stayed dark navy on a light
    /// background — invisible until the light theme was first rendered.
    /// </remarks>
    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush
            ? brush : new SolidColorBrush(fallback);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        context.FillRectangle(Brush("AppCode", Color.FromRgb(0x07, 0x12, 0x20)), new Rect(0, 0, width, height));

        var midline = new Pen(Brush("AppLine", Color.FromRgb(0x23, 0x36, 0x52)));
        context.DrawLine(midline, new Point(0, height / 2), new Point(width, height / 2));

        var samples = Samples;
        if (samples.Length == 0) return;

        var pen = new Pen(Brush("AppAccent", Color.FromRgb(0x42, 0xe8, 0xd5)), 1);
        var columns = (int)Math.Min(width, 4000);
        var perColumn = Math.Max(1, samples.Length / Math.Max(columns, 1));

        for (var column = 0; column < columns; column++)
        {
            var start = (int)((long)column * samples.Length / columns);
            var end = Math.Min(start + perColumn, samples.Length);
            if (start >= end) continue;

            float low = 0f, high = 0f;
            for (var i = start; i < end; i++)
            {
                if (samples[i] < low) low = samples[i];
                if (samples[i] > high) high = samples[i];
            }

            var x = column * width / columns;
            var top = height / 2 - high * height / 2;
            var bottom = height / 2 - low * height / 2;
            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }

        var progress = Progress;
        if (progress >= 0)
        {
            var cursorX = Math.Clamp(progress, 0, 1) * width;
            // Drawn over the envelope, and in the danger colour so it reads as a
            // position rather than another waveform.
            context.DrawLine(new Pen(Brush("AppDanger", Color.FromRgb(0xff, 0x77, 0x8b)), 1.5),
                             new Point(cursorX, 0), new Point(cursorX, height));
        }
    }
}
