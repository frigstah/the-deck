using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Sirs.Core.Audio;

namespace Sirs.App;

/// <summary>Exposes the drawn meter to assistive technology as a read-only text value.</summary>
internal sealed class LevelMeterAutomationPeer(LevelMeterControl owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override string GetClassNameCore() => "LevelMeter";

    protected override string GetNameCore() => ((LevelMeterControl)Owner).AutomationDescription;

    protected override bool IsControlElementCore() => true;
}

/// <summary>
/// Stereo peak meter (B1). Coloured by zone rather than by the current advice, so the green "aim
/// here" region is visible even while the level is somewhere else - which is what makes the meter
/// teach rather than just report.
/// <para>
/// Drawn as discrete segments rather than a continuous bar. A solid bar reads as a progress
/// indicator, which is the wrong idea entirely: a level is not a thing that fills up. Segments read
/// as a meter because that is what every piece of broadcast equipment in the world uses, and the
/// unlit segments keep the whole scale visible so the target zone can be seen from across a room.
/// </para>
/// </summary>
public sealed class LevelMeterControl : FrameworkElement
{
    private const float FloorDb = -60f;

    private static readonly Brush QuietBrush = Frozen("#FF8C8C9C");
    private static readonly Brush GoodBrush = Frozen("#FF25B268");
    private static readonly Brush LoudBrush = Frozen("#FFE0A32E");
    private static readonly Brush ClipBrush = Frozen("#FFE24545");

    // Unlit segments: the same hues at low opacity, so the scale is legible without competing with
    // the part that is actually lit.
    private static readonly Brush QuietOff = Frozen("#1A8C8C9C");
    private static readonly Brush GoodOff = Frozen("#2625B268");
    private static readonly Brush LoudOff = Frozen("#26E0A32E");
    private static readonly Brush ClipOff = Frozen("#30E24545");

    private static readonly Brush TickBrush = Frozen("#55808090");
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public static readonly DependencyProperty PeakDbLeftProperty = DependencyProperty.Register(
        nameof(PeakDbLeft), typeof(double), typeof(LevelMeterControl),
        new FrameworkPropertyMetadata(-90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakDbRightProperty = DependencyProperty.Register(
        nameof(PeakDbRight), typeof(double), typeof(LevelMeterControl),
        new FrameworkPropertyMetadata(-90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoldDbProperty = DependencyProperty.Register(
        nameof(HoldDb), typeof(double), typeof(LevelMeterControl),
        new FrameworkPropertyMetadata(-90.0, FrameworkPropertyMetadataOptions.AffectsRender, OnHoldDbChanged));

    private static void OnHoldDbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Tell assistive technology the level moved, so it can announce on request.
        if (d is LevelMeterControl meter)
        {
            UIElementAutomationPeer.FromElement(meter)?.RaiseAutomationEvent(AutomationEvents.PropertyChanged);
        }
    }

    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
        nameof(ShowScale), typeof(bool), typeof(LevelMeterControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakDbLeft
    {
        get => (double)GetValue(PeakDbLeftProperty);
        set => SetValue(PeakDbLeftProperty, value);
    }

    public double PeakDbRight
    {
        get => (double)GetValue(PeakDbRightProperty);
        set => SetValue(PeakDbRightProperty, value);
    }

    /// <summary>
    /// The loudest level over the last couple of seconds. Drawn as a hold marker so the bar and the
    /// number beside it tell the same story - the marker is what the coaching verdict is based on.
    /// </summary>
    public double HoldDb
    {
        get => (double)GetValue(HoldDbProperty);
        set => SetValue(HoldDbProperty, value);
    }

    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>
    /// A drawn meter is invisible to a screen reader, so the level is published as automation text
    /// and kept current as it changes (I6). Blind broadcasters are a real and underserved group.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new LevelMeterAutomationPeer(this);

    internal string AutomationDescription =>
        HoldDb <= AudioMath.MinDb
            ? "No sound"
            : $"Loudest {HoldDb:0} decibels";

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var scaleHeight = ShowScale ? 14.0 : 0.0;
        var barsHeight = Math.Max(0, height - scaleHeight);
        var gap = 3.0;
        var barHeight = Math.Max(4, (barsHeight - gap) / 2);

        DrawBar(drawingContext, new Rect(0, 0, width, barHeight), PeakDbLeft);
        DrawBar(drawingContext, new Rect(0, barHeight + gap, width, barHeight), PeakDbRight);

        DrawHold(drawingContext, width, barsHeight);

        if (ShowScale) DrawScale(drawingContext, width, barsHeight, height);
    }

    /// <summary>
    /// Segment count scaled to the width available. The mixer's faders get an 8-pixel-tall meter a
    /// fraction of the width of the main one, and forcing a fixed count on both would give one of
    /// them segments too fine to see or too coarse to read.
    /// </summary>
    private static int SegmentCount(double width) =>
        (int)Math.Clamp(Math.Round(width / 9.0), 10, 64);

    private static void DrawBar(DrawingContext context, Rect area, double peakDb)
    {
        if (area.Width <= 0 || area.Height <= 0) return;

        var count = SegmentCount(area.Width);
        var slot = area.Width / count;
        var gap = slot > 6 ? 2.0 : 1.0;
        var segmentWidth = Math.Max(1.0, slot - gap);

        var reached = AudioMath.DbToMeterScale((float)peakDb, FloorDb);

        for (var i = 0; i < count; i++)
        {
            // The middle of the segment decides both its colour and whether it is lit, so a segment
            // never lights up in a colour from the zone next door.
            var position = (float)((i + 0.5) / count);
            var lit = position <= reached;
            var db = AudioMath.MeterScaleToDb(position, FloorDb);

            context.DrawRectangle(
                lit ? LitBrush(db) : UnlitBrush(db),
                null,
                new Rect(area.X + (i * slot), area.Y, segmentWidth, area.Height));
        }
    }

    private static Brush LitBrush(float db) => db switch
    {
        >= -1f => ClipBrush,
        >= -4f => LoudBrush,
        >= -24f => GoodBrush,
        _ => QuietBrush,
    };

    private static Brush UnlitBrush(float db) => db switch
    {
        >= -1f => ClipOff,
        >= -4f => LoudOff,
        >= -24f => GoodOff,
        _ => QuietOff,
    };

    private void DrawHold(DrawingContext context, double width, double barsHeight)
    {
        var fraction = AudioMath.DbToMeterScale((float)HoldDb, FloorDb);
        if (fraction <= 0) return;

        var brush = HoldDb switch
        {
            >= -1f => ClipBrush,
            >= -4f => LoudBrush,
            >= -24f => GoodBrush,
            _ => QuietBrush,
        };

        var x = Math.Round(fraction * width) + 0.5;
        var pen = new Pen(brush, 2);
        pen.Freeze();
        context.DrawLine(pen, new Point(x, 0), new Point(x, barsHeight));
    }

    private void DrawScale(DrawingContext context, double width, double top, double height)
    {
        var pen = new Pen(TickBrush, 1);

        foreach (var db in new[] { -60f, -40f, -24f, -12f, -6f, 0f })
        {
            var x = Math.Round(AudioMath.DbToMeterScale(db, FloorDb) * width) + 0.5;
            context.DrawLine(pen, new Point(x, top), new Point(x, top + 3));

            var text = new FormattedText(
                db == 0 ? "0" : ((int)db).ToString(CultureInfo.InvariantCulture),
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
