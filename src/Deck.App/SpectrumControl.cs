using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Deck.App;

/// <summary>Exposes the drawn spectrum to assistive technology as a read-only description.</summary>
internal sealed class SpectrumAutomationPeer(SpectrumControl owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override string GetClassNameCore() => "Spectrum";

    protected override string GetNameCore() => ((SpectrumControl)Owner).AutomationDescription;

    protected override bool IsControlElementCore() => true;
}

/// <summary>
/// The spectrum display (B9).
/// <para>
/// Drawn straight onto the element rather than built out of shapes: twenty-four rectangles rebuilt
/// twenty times a second through the visual tree would allocate constantly, and this sits in the
/// same window as an encoder that must not be interrupted.
/// </para>
/// </summary>
public sealed class SpectrumControl : FrameworkElement
{
    private static readonly Brush TrackBrush = Frozen("#22808090");
    private static readonly Brush BarBrush = Frozen("#FF3B82D9");
    private static readonly Brush HighBrush = Frozen("#FF25B268");
    private static readonly Brush TickBrush = Frozen("#55808090");
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    /// <summary>Where to put a frequency label, in Hz, and what to write.</summary>
    private static readonly (double Hz, string Label)[] Ticks =
    [
        (100, "100"),
        (1000, "1k"),
        (10000, "10k"),
    ];

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(double[]), typeof(SpectrumControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EdgesHzProperty = DependencyProperty.Register(
        nameof(EdgesHz), typeof(double[]), typeof(SpectrumControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One value per bar, 0 to 1.</summary>
    public double[]? Bars
    {
        get => (double[]?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>The frequency each bar starts at, with one extra for the top edge.</summary>
    public double[]? EdgesHz
    {
        get => (double[]?)GetValue(EdgesHzProperty);
        set => SetValue(EdgesHzProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpectrumAutomationPeer(this);

    /// <summary>
    /// A picture of the spectrum is nothing to a screen reader, so it is summarised as where the
    /// energy is rather than as twenty-four numbers, which would be unreadable (I6).
    /// </summary>
    internal string AutomationDescription
    {
        get
        {
            if (Bars is not { Length: > 0 } bars || EdgesHz is not { Length: > 0 } edges) return "No sound";

            var loudest = 0;
            for (var i = 1; i < bars.Length; i++)
            {
                if (bars[i] > bars[loudest]) loudest = i;
            }

            if (bars[loudest] < 0.05) return "No sound";

            var hz = edges[Math.Min(loudest, edges.Length - 1)];
            return hz >= 1000
                ? $"Loudest around {hz / 1000:0.#} kilohertz"
                : $"Loudest around {hz:0} hertz";
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        const double scaleHeight = 13.0;
        var barsHeight = Math.Max(0, height - scaleHeight);

        drawingContext.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, barsHeight), 3, 3);

        var bars = Bars;
        if (bars is not { Length: > 0 }) return;

        var slot = width / bars.Length;
        var barWidth = Math.Max(1, slot - 2);

        for (var i = 0; i < bars.Length; i++)
        {
            var value = Math.Clamp(bars[i], 0, 1);
            var barHeight = value * barsHeight;
            if (barHeight < 1) continue;

            // The top of the range is tinted differently, so "how loud" is readable at a glance
            // without having to compare bar heights against a scale.
            var brush = value > 0.8 ? HighBrush : BarBrush;
            var x = i * slot + (slot - barWidth) / 2;

            drawingContext.DrawRectangle(brush, null, new Rect(x, barsHeight - barHeight, barWidth, barHeight));
        }

        DrawScale(drawingContext, width, barsHeight);
    }

    /// <summary>
    /// Labels at 100 Hz, 1 kHz and 10 kHz, positioned by finding which bar covers each. Working it
    /// out from the edges rather than assuming a spacing means the labels stay honest when the
    /// sample rate changes the bands underneath them.
    /// </summary>
    private void DrawScale(DrawingContext context, double width, double top)
    {
        if (Bars is not { Length: > 0 } bars || EdgesHz is not { Length: > 1 } edges) return;

        var pen = new Pen(TickBrush, 1);
        var slot = width / bars.Length;

        foreach (var (hz, label) in Ticks)
        {
            var band = -1;
            for (var i = 0; i < bars.Length && i + 1 < edges.Length; i++)
            {
                if (hz < edges[i] || hz >= edges[i + 1]) continue;

                band = i;
                break;
            }

            if (band < 0) continue;

            var x = Math.Round((band + 0.5) * slot) + 0.5;
            context.DrawLine(pen, new Point(x, top), new Point(x, top + 3));

            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                // Qualified: FrameworkElement has an instance property of the same name.
                System.Windows.FlowDirection.LeftToRight,
                LabelTypeface,
                9,
                TickBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var textX = Math.Clamp(x - (text.Width / 2), 0, Math.Max(0, width - text.Width));
            context.DrawText(text, new Point(textX, top + 3));
        }
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
