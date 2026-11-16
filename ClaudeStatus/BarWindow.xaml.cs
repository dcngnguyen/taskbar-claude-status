using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeStatus;

/// <summary>
/// The small status bar on the taskbar: an animated spark per Claude Code session.
/// It sits just left of Weather Bar (or Music Bar, or the tray icons), and only shows while a session is open.
/// </summary>
public partial class BarWindow : Window
{
    private const double BarHeight = 40, Gap = 8, PopupGap = 8, SidePadding = 10;

    private readonly IntPtr _hwnd;
    private readonly SessionsPopup _popup = new();
    private readonly SessionWatcher _watcher = new();
    private readonly Dictionary<string, SparkIcon> _dots = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _openDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _closeDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Native.WinEventProc _foregroundChanged;
    private IntPtr _foregroundHook;
    private double _scale = 1;
    private bool _hasSessions;
    private int _tick;

    public BarWindow()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.MakeToolWindow(_hwnd, noActivate: true);
        HoverGlow.Attach(Bar, Dots);

        // The sparks only animate while you can see them (not during full-screen games).
        IsVisibleChanged += (_, _) =>
        {
            foreach (var dot in _dots.Values) dot.Paused = !IsVisible;
        };
        Application.Current.SessionEnding += (_, _) => _quitting = _popup.AllowClose = true;

        // Hover over the bar to see every session; it closes once the mouse has left both windows.
        Bar.MouseEnter += (_, _) =>
        {
            _closeDelay.Stop();
            if (!_popup.IsOpen) _openDelay.Start();
        };
        Bar.MouseLeave += (_, _) =>
        {
            _openDelay.Stop();
            if (_popup.IsOpen) _closeDelay.Start();
        };
        _popup.MouseEnter += (_, _) => _closeDelay.Stop();
        _popup.MouseLeave += (_, _) => _closeDelay.Start();
        _openDelay.Tick += (_, _) =>
        {
            _openDelay.Stop();
            if (Bar.IsMouseOver && IsVisible) OpenPopup();
        };
        _closeDelay.Tick += (_, _) =>
        {
            _closeDelay.Stop();
            if (!Bar.IsMouseOver && !_popup.IsMouseOver) _popup.HideAnimated();
        };

        _foregroundChanged = (_, _, _, _, _, _, _) => UpdatePlacement();
        _timer.Tick += (_, _) =>
        {
            // Sessions twice a second, placement four times a second.
            if (_tick++ % 2 == 0) ShowSessions();
            UpdatePlacement();
        };
    }

    public void Start()
    {
        ShowSessions();
        UpdatePlacement();
        _timer.Start();
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundChanged, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    // ---------- Sessions ----------

    private void ShowSessions()
    {
        List<SessionView> sessions;
        try
        {
            sessions = _watcher.Poll();
        }
        catch (Exception ex)
        {
            Log.Write("Could not check sessions: " + ex);
            return;
        }

        // Sparks for sessions that are gone shrink away; new ones grow in at the end.
        foreach (var id in _dots.Keys.Where(id => sessions.All(s => s.Id != id)).ToList())
        {
            RemoveDot(_dots[id]);
            _dots.Remove(id);
        }
        foreach (var s in sessions)
        {
            if (!_dots.TryGetValue(s.Id, out var dot))
            {
                dot = new SparkIcon(18) { Paused = !IsVisible, Margin = new Thickness(1, 0, 1, 0) };
                _dots[s.Id] = dot;
                Dots.Children.Add(dot);
                var appear = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                dot.BeginAnimation(OpacityProperty, appear);
            }
            dot.SetState(s.State);
        }

        _hasSessions = sessions.Count > 0;
        if (_popup.IsVisible) _popup.ShowSessions(sessions);
        _sessions = sessions;
    }

    private List<SessionView> _sessions = [];

    private void RemoveDot(SparkIcon dot)
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) =>
        {
            Dots.Children.Remove(dot);
            UpdatePlacement();
        };
        dot.BeginAnimation(OpacityProperty, fade);
        dot.BeginAnimation(WidthProperty, new DoubleAnimation(0, duration));
    }

    // ---------- Placement ----------

    /// <summary>Keeps the bar on the taskbar, above it in the stack, and hidden when there's nothing to show.</summary>
    private void UpdatePlacement()
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (!_hasSessions || taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out var tb) ||
            Native.IsFullscreenAppActive() || !Native.IsTaskbarShown(taskbar, tb))
        {
            if (IsVisible && !_barHiding) FadeBar(show: false);
            if (_popup.IsVisible) _popup.Hide();
            return;
        }
        if (_barHiding) FadeBar(show: true); // came back before the fade-out finished

        uint dpi = Native.GetDpiForWindow(taskbar);
        double scale = dpi > 0 ? dpi / 96.0 : 1;
        int Px(double dip) => (int)Math.Round(dip * scale);
        _scale = scale;

        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr) && tr.Width > 0
            ? tr.Left - Px(Gap)
            : tb.Right - Px(360);

        // Sit to the left of Weather Bar, or Music Bar, whichever is showing furthest left on this taskbar.
        foreach (var title in new[] { "Music Bar", "Weather Bar" })
        {
            var other = Native.FindWindow(null, title);
            if (other != IntPtr.Zero && Native.IsWindowVisible(other) && Native.GetWindowRect(other, out var ob) &&
                ob.Width > 0 && ob.Top < tb.Bottom && ob.Bottom > tb.Top && ob.Left <= right)
            {
                right = ob.Left - Px(Gap);
            }
        }

        BarContent.InvalidateMeasure();
        BarContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = Px(BarContent.DesiredSize.Width + SidePadding * 2 + 2);
        int height = Px(BarHeight);
        int x = right - width;
        int y = tb.Top + (tb.Height - height) / 2;

        bool firstShow = !IsVisible;
        if (firstShow)
        {
            Left = x / scale;
            Top = y / scale;
            Opacity = 0;
            Show();
            FadeBar(show: true);
        }

        // Clicking the taskbar lifts it over us, so lift the bar back on top when that happens.
        uint flags = Native.SWP_NOACTIVATE;
        if (!firstShow && !Native.IsAbove(taskbar, _hwnd)) flags |= Native.SWP_NOZORDER;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, flags);
        if (_popup.IsOpen) _popup.PlaceAbove((x + width / 2.0) / scale, y / scale - PopupGap);
    }

    private bool _barHiding;
    private int _barFadeVersion;

    private void FadeBar(bool show)
    {
        int version = ++_barFadeVersion;
        _barHiding = !show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 220 : 150));
        if (!show)
        {
            fade.Completed += (_, _) =>
            {
                if (version != _barFadeVersion) return; // shown again meanwhile
                _barHiding = false;
                Hide();
            };
        }
        BeginAnimation(OpacityProperty, fade);
    }

    private void OpenPopup()
    {
        if (!Native.GetWindowRect(_hwnd, out var bar)) return;
        _popup.ShowSessions(_sessions);
        _popup.ShowAbove((bar.Left + bar.Right) / 2.0 / _scale, bar.Top / _scale - PopupGap);
    }

    // ---------- Menu and closing ----------

    /// <summary>The bar never takes focus, but a menu only notices clicks elsewhere when its window has focus.</summary>
    private void Menu_Opened(object sender, RoutedEventArgs e) => Native.SetForegroundWindow(_hwnd);

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        _popup.AllowClose = true;
        _quitting = true;
        Application.Current.Shutdown();
    }

    private bool _quitting;

    /// <summary>Alt+F4 must not destroy the bar while the app keeps running. Only "Quit" closes it.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting) e.Cancel = true;
        base.OnClosing(e);
    }
}
