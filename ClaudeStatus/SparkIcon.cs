using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeStatus;

/// <summary>
/// A small orange spark that shows one session's state through how it moves:
/// working = its rays grow and shrink in a ripple, waiting = amber heartbeat pulse,
/// done = one bloom then still, stopped = rays folded in and grey, closed = shrinks away.
/// </summary>
public sealed class SparkIcon : FrameworkElement
{
    private const int Rays = 10;
    // Slightly uneven ray lengths give it a hand-drawn feel.
    private static readonly double[] Base = [1, .82, .95, .78, 1, .86, .92, .8, .97, .84];
    private static readonly Color Orange = Color.FromRgb(217, 119, 87);
    private static readonly Color Amber = Color.FromRgb(240, 180, 60);
    private static readonly Color Grey = Color.FromRgb(140, 140, 140);
    private static readonly TimeSpan Bloom = TimeSpan.FromMilliseconds(700);

    // One shared 20-frames-a-second clock for every spark (plenty for something this small); it stops when nothing is moving.
    private static readonly List<SparkIcon> Live = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DispatcherTimer Ticker = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    private static double _lastTick;

    static SparkIcon()
    {
        Ticker.Tick += (_, _) => TickAll();
    }

    private string _state = States.Idle, _previous = States.Idle;
    private double _since, _scale = 1, _opacity = 1;
    private readonly double[] _length = (double[])Base.Clone();
    private Color _color = Orange;
    private bool _paused;

    public SparkIcon(double size)
    {
        Width = Height = size;
        Loaded += (_, _) => { if (!Live.Contains(this)) Live.Add(this); Wake(); };
        Unloaded += (_, _) => Live.Remove(this);
        IsVisibleChanged += (_, _) => { if (IsVisible) Wake(); };
    }

    public string State => _state;

    public void SetState(string state)
    {
        if (state == _state) return;
        (_previous, _state, _since) = (_state, state, Clock.Elapsed.TotalSeconds);
        Wake();
    }

    /// <summary>Freezes the spark while nothing can see it (e.g. during a full-screen game).</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) Wake();
        }
    }

    private static void Wake()
    {
        if (Ticker.IsEnabled) return;
        _lastTick = Clock.Elapsed.TotalSeconds;
        Ticker.Start();
    }

    private static void TickAll()
    {
        double t = Clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, t - _lastTick);
        _lastTick = t;
        bool anyMoving = false;
        foreach (var spark in Live)
        {
            if (spark._paused || !spark.IsVisible) continue;
            if (spark.Step(t, dt)) anyMoving = true;
            spark.InvalidateVisual();
        }
        if (!anyMoving) Ticker.Stop();
    }

    /// <summary>Moves one frame toward the state's look. Returns false once it has settled and can rest.</summary>
    private bool Step(double t, double dt)
    {
        double age = t - _since;
        double k = 1 - Math.Exp(-dt * 9); // smooth easing toward each target
        double targetScale = 1, targetOpacity = 1, fold = 1;
        bool endless = false;
        Color targetColor = Orange;

        switch (_state)
        {
            case States.Working:
                endless = true;
                break;
            case States.Waiting:
            {
                // A soft "thump" every 1.5 seconds.
                double beat = Math.Pow(Math.Max(0, Math.Sin(t * Math.PI / 0.75)), 6);
                targetScale = 0.88 + 0.2 * beat;
                targetColor = Amber;
                endless = true;
                break;
            }
            case SessionWatcher.Stopped:
                fold = 0.28;
                targetScale = 0.9;
                targetColor = Grey;
                break;
            case States.Ended:
                fold = 0.28;
                targetScale = 0;
                targetOpacity = 0;
                targetColor = Grey;
                break;
        }

        bool blooming = _state == States.Idle && _previous is States.Working or States.Waiting && age < Bloom.TotalSeconds;
        if (blooming) _scale = 1 + 0.4 * Math.Sin(Math.PI * age / Bloom.TotalSeconds);
        else _scale += (targetScale - _scale) * k;
        _opacity += (targetOpacity - _opacity) * k;
        _color = Mix(_color, targetColor, k);

        double change = 0;
        for (int i = 0; i < Rays; i++)
        {
            double target = _state == States.Working
                ? Base[i] * (0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(t * 4.2 - i * 4 * Math.PI / Rays))) // a ripple running around
                : Base[i] * fold;
            double before = _length[i];
            _length[i] += (target - _length[i]) * k;
            change += Math.Abs(_length[i] - before);
        }

        bool settled = !endless && !blooming && change < 0.001 &&
                       Math.Abs(_scale - targetScale) < 0.002 && Math.Abs(_opacity - targetOpacity) < 0.002 && _color == targetColor;
        if (settled) (_scale, _opacity) = (targetScale, targetOpacity);
        return !settled;
    }

    private static Color Mix(Color from, Color to, double k)
    {
        byte M(byte a, byte b)
        {
            double v = a + (b - a) * k;
            return (byte)Math.Round(Math.Abs(b - v) < 1 ? b : v);
        }
        return Color.FromRgb(M(from.R, to.R), M(from.G, to.G), M(from.B, to.B));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_opacity <= 0.01 || _scale <= 0.01) return;
        double radius = Math.Min(ActualWidth, ActualHeight) / 2 * 0.92;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(new SolidColorBrush(_color), radius * 0.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        dc.PushOpacity(_opacity);
        dc.PushTransform(new ScaleTransform(_scale, _scale, center.X, center.Y));
        double inner = radius * 0.18;
        for (int i = 0; i < Rays; i++)
        {
            double angle = i * 2 * Math.PI / Rays;
            double outer = inner + (radius - inner) * _length[i];
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            dc.DrawLine(pen, center + direction * inner, center + direction * outer);
        }
        dc.Pop();
        dc.Pop();
    }
}
