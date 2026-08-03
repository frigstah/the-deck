using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Deck.Core;
using Deck.Core.Theming;

namespace Deck.App;

/// <summary>
/// The moving layer behind the deck, for the two palettes that have one (I5).
/// <para>
/// Three rules govern everything here, and they are all about the fact that this is a broadcast
/// encoder rather than a screensaver.
/// </para>
/// <para>
/// It must not cost a show. The work per frame is a few dozen filled shapes and no allocation of
/// geometry that can be built once; the timer runs well under the display's rate, because leaves
/// drifting and a swell rising are slow things and nothing here benefits from sixty frames a
/// second. It stops entirely when the window is not on screen.
/// </para>
/// <para>
/// It must not cost legibility. Every palette's text is measured against its background colour, and
/// a backdrop painted over that background silently invalidates the measurement - so nothing drawn
/// here is allowed to move a pixel further from the ground than <see cref="Palettes.MaximumBackdropDeviation"/>. The
/// check beside the palettes holds that number, which is the only reason the contrast figures for
/// Forest and Tide still mean what they say.
/// </para>
/// <para>
/// And it must not argue with somebody who has already said they do not want movement. Turning
/// motion off under Deck itself leaves the backdrop drawn but still - the palette keeps its
/// character, and nothing on the window moves.
/// </para>
/// </summary>
public sealed class DeckBackdrop : FrameworkElement
{
    /// <summary>
    /// Twelve a second. Fast enough that a leaf crossing the window in half a minute moves smoothly,
    /// slow enough that the cost does not show up next to an encoder.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1000.0 / 12);

    private readonly DispatcherTimer _timer;
    private readonly List<Leaf> _leaves = [];
    private SolidColorBrush[]? _waveBrushes;
    private double _phase;

    public DeckBackdrop()
    {
        IsHitTestVisible = false;

        // Below the timer's own priority: a frame of decoration must never be drawn ahead of the
        // meter, and Background yields to everything the window actually needs.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) =>
        {
            _phase += Interval.TotalSeconds;
            InvalidateVisual();
        };

        Loaded += (_, _) =>
        {
            App.PaletteChanged += OnPaletteChanged;
            Sync();
        };

        Unloaded += (_, _) =>
        {
            App.PaletteChanged -= OnPaletteChanged;
            _timer.Stop();
        };

        IsVisibleChanged += (_, _) => Sync();
    }

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(DeckBackdrop),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimateChanged));

    /// <summary>
    /// Whether it moves. False leaves the scene drawn and still, rather than blank: somebody who
    /// turned motion off chose stillness, not a different palette.
    /// </summary>
    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    private static void OnAnimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DeckBackdrop)d).Sync();

    private BackdropKind Kind => Palettes.Backdrop(App.CurrentPalette);

    private void OnPaletteChanged()
    {
        ForgetBrushes();
        Sync();
        InvalidateVisual();
    }

    private void Sync()
    {
        var wanted = Animate && IsVisible && Kind != BackdropKind.None;

        if (wanted && !_timer.IsEnabled) _timer.Start();
        else if (!wanted && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>
    /// The palette's own accent, held down to a weight that cannot disturb what is measured against
    /// the ground. Read per render rather than cached, because the palette is replaced wholesale
    /// when somebody changes it and a cached brush would keep the old one.
    /// </summary>
    private Color Ink(double weight)
    {
        var accent = Resource("AccentColor", Colors.Gray);
        var ground = Resource("BackgroundColor", Colors.Black);
        var t = Math.Clamp(weight, 0, 1) * Palettes.MaximumBackdropDeviation;

        return Color.FromRgb(
            (byte)Math.Round(ground.R + (accent.R - ground.R) * t),
            (byte)Math.Round(ground.G + (accent.G - ground.G) * t),
            (byte)Math.Round(ground.B + (accent.B - ground.B) * t));
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Color Resource(string key, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) is Color colour ? colour : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        switch (Kind)
        {
            case BackdropKind.Leaves:
                DrawLeaves(dc, w, h);
                break;
            case BackdropKind.Waves:
                DrawWaves(dc, w, h);
                break;
        }
    }

    // ------------------------------------------------------------------ leaves

    /// <summary>
    /// One leaf. A class with a brush and a transform it keeps, rather than a record rebuilt per
    /// frame: at twelve frames a second, twenty-six leaves and three transforms each was about a
    /// thousand short-lived objects every second, on a machine whose actual job is not to drop audio.
    /// </summary>
    private sealed class Leaf
    {
        public double Y, Size, Speed, Phase, Sway, Weight;
        public SolidColorBrush? Brush;
        public readonly MatrixTransform Transform = new();
    }

    /// <summary>
    /// One leaf, built once and reused under a transform. Twenty-six of them, laid out so no two
    /// share a lane, a speed or a size - a drift where everything moves together reads as a texture
    /// scrolling rather than as leaves in air.
    /// </summary>
    private static readonly StreamGeometry LeafShape = BuildLeaf();

    private static StreamGeometry BuildLeaf()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.BezierTo(new Point(0.42, -0.34), new Point(0.86, -0.20), new Point(1, 0), true, true);
            ctx.BezierTo(new Point(0.86, 0.20), new Point(0.42, 0.34), new Point(0, 0), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private void EnsureLeaves()
    {
        if (_leaves.Count > 0) return;

        // Fixed rather than random, so the scene is the same every run and can be looked at twice.
        var rng = new Random(20260801);
        for (var i = 0; i < 26; i++)
        {
            _leaves.Add(new Leaf
            {
                Y = (i + 0.5) / 26.0 + (rng.NextDouble() - 0.5) * 0.03,
                Size = 9 + rng.NextDouble() * 16,
                Speed = 0.006 + rng.NextDouble() * 0.014,
                Phase = rng.NextDouble(),
                Sway = rng.NextDouble() * Math.PI * 2,
                Weight = 0.35 + rng.NextDouble() * 0.65,
            });
        }
    }

    /// <summary>
    /// Dropped when the palette is replaced, and rebuilt on the next frame. A brush holds a colour
    /// resolved from a dictionary that no longer exists after a palette change, which is the same
    /// trap the view model's brushes fell into.
    /// </summary>
    private void ForgetBrushes()
    {
        foreach (var leaf in _leaves) leaf.Brush = null;
        _waveBrushes = null;
    }

    private void DrawLeaves(DrawingContext dc, double w, double h)
    {
        EnsureLeaves();

        foreach (var leaf in _leaves)
        {
            // Wrapped rather than respawned: a leaf that leaves the right edge is the same leaf
            // arriving at the left, so the population never changes and neither does the cost.
            var travel = (leaf.Phase + _phase * leaf.Speed) % 1.0;
            var x = -0.1 * w + travel * 1.2 * w;

            var sway = Math.Sin(_phase * 0.55 + leaf.Sway);
            var y = leaf.Y * h + sway * h * 0.035;
            var angle = (sway * 26 + travel * 90) * Math.PI / 180;

            leaf.Brush ??= Frozen(Ink(leaf.Weight));

            // Scale, then rotate, then translate, composed by hand into one matrix - three pushes
            // and three transform objects a leaf is the same picture at four times the litter.
            var cos = Math.Cos(angle) * leaf.Size;
            var sin = Math.Sin(angle) * leaf.Size;
            leaf.Transform.Matrix = new Matrix(cos, sin, -sin, cos, x, y);

            dc.PushTransform(leaf.Transform);
            dc.DrawGeometry(leaf.Brush, null, LeafShape);
            dc.Pop();
        }
    }

    // ------------------------------------------------------------------- waves

    /// <summary>
    /// Five bands of swell, each a filled curve running off both edges. They travel at different
    /// speeds and different wavelengths, which is what stops the set reading as one shape sliding:
    /// the interference between them is the whole effect.
    /// </summary>
    private void DrawWaves(DrawingContext dc, double w, double h)
    {
        const int Bands = 5;
        const int Steps = 44;

        for (var band = 0; band < Bands; band++)
        {
            var depth = (band + 1) / (double)Bands;

            var baseline = h * (0.34 + 0.17 * band);
            var amplitude = h * (0.020 + 0.012 * (Bands - band));
            var wavelength = 1.15 + band * 0.55;
            var speed = 0.30 + band * 0.16;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(-2, h + 2), isFilled: true, isClosed: true);

                for (var i = 0; i <= Steps; i++)
                {
                    var t = i / (double)Steps;
                    var x = -2 + t * (w + 4);
                    var y = baseline
                            + Math.Sin(t * Math.PI * 2 * wavelength + _phase * speed) * amplitude
                            + Math.Sin(t * Math.PI * 2 * (wavelength * 1.7) - _phase * speed * 0.6) * amplitude * 0.35;

                    ctx.LineTo(new Point(x, y), isStroked: false, isSmoothJoin: true);
                }

                ctx.LineTo(new Point(w + 2, h + 2), isStroked: false, isSmoothJoin: false);
            }

            geometry.Freeze();

            // Nearer bands are heavier, so the set reads as depth rather than as five equal lines.
            // The curve has to be rebuilt every frame - that is what a moving swell is - but the
            // five brushes do not.
            _waveBrushes ??= [.. Enumerable.Range(0, Bands)
                .Select(b => Frozen(Ink(0.22 + ((b + 1) / (double)Bands) * 0.5)))];

            dc.DrawGeometry(_waveBrushes[band], null, geometry);
        }
    }
}
