using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AudioCpp.Bindings.Gui;

/// <summary>A min/max envelope of generated audio. Enough to see that a run produced sound.</summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<float[]> SamplesProperty =
        AvaloniaProperty.Register<WaveformView, float[]>(nameof(Samples), []);

    static WaveformView() => AffectsRender<WaveformView>(SamplesProperty);

    public float[] Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x24)), new Rect(0, 0, width, height));

        var midline = new Pen(new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x44)));
        context.DrawLine(midline, new Point(0, height / 2), new Point(width, height / 2));

        var samples = Samples;
        if (samples.Length == 0) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x6c, 0xc6, 0x9a)), 1);
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
    }
}
