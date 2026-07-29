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
/// Stereo peak meter (B1). The bar is coloured by zone rather than by the current advice, so the
/// green "aim here" region is visible even while the level is somewhere else - which is what makes
/// the meter teach rather than just report.
/// </summary>
public sealed class LevelMeterControl : FrameworkElement
{
    private const float FloorDb = -60f;

    private static readonly Brush QuietBrush = Frozen("#FF8C8C9C");
    private static readonly Brush GoodBrush = Frozen("#FF25B268");
    private static readonly Brush LoudBrush = Frozen("#FFE0A32E");
    private static readonly Brush ClipBrush = Frozen("#FFE24545");
    private static readonly Brush TrackBrush = Frozen("#33808090");
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

    private static void DrawBar(DrawingContext context, Rect area, double peakDb)
    {
        var radius = Math.Min(3, area.Height / 2);
        context.DrawRoundedRectangle(TrackBrush, null, area, radius, radius);

        var fraction = AudioMath.DbToMeterScale((float)peakDb, FloorDb);
        if (fraction <= 0) return;

        // Each zone is filled only as far as the level reaches, which produces the familiar
        // green-then-amber-then-red ramp without needing a gradient brush per frame.
        DrawZone(context, area, fraction, FloorDb, -24f, QuietBrush, radius);
        DrawZone(context, area, fraction, -24f, -4f, GoodBrush, radius);
        DrawZone(context, area, fraction, -4f, -1f, LoudBrush, radius);
        DrawZone(context, area, fraction, -1f, 0f, ClipBrush, radius);
    }

    private static void DrawZone(
        DrawingContext context,
        Rect area,
        float fillFraction,
        float fromDb,
        float toDb,
        Brush brush,
        double radius)
    {
        var zoneStart = AudioMath.DbToMeterScale(fromDb, FloorDb);
        var zoneEnd = AudioMath.DbToMeterScale(toDb, FloorDb);

        var start = Math.Max(zoneStart, 0);
        var end = Math.Min(zoneEnd, fillFraction);
        if (end <= start) return;

        var x = area.X + (start * area.Width);
        var w = (end - start) * area.Width;
        context.DrawRoundedRectangle(brush, null, new Rect(x, area.Y, w, area.Height), radius, radius);
    }

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
