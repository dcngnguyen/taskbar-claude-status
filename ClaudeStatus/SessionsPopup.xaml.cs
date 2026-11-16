using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeStatus;

/// <summary>The window that opens above the bar on hover: one row per session.</summary>
public partial class SessionsPopup : Window
{
    private sealed record Row(Grid Panel, SparkIcon Dot, TextBlock Project, TextBlock Activity, TextBlock Age);

    private readonly Dictionary<string, Row> _rows = new();
    private double _centerX, _bottom;

    public bool AllowClose { get; set; }

    public SessionsPopup()
    {
        InitializeComponent();
        Native.MakeToolWindow(new WindowInteropHelper(this).EnsureHandle(), noActivate: true);
        // The height follows the number of sessions; keep the bottom edge where it was.
        SizeChanged += (_, _) => PlaceAbove(_centerX, _bottom);
        IsVisibleChanged += (_, _) =>
        {
            foreach (var row in _rows.Values) row.Dot.Paused = !IsVisible;
        };
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true; // Alt+F4 just hides it
            HideAnimated();
        };
    }

    /// <summary>Updates the rows in place, so the dots keep animating smoothly.</summary>
    public void ShowSessions(IReadOnlyList<SessionView> sessions)
    {
        var now = DateTime.UtcNow;
        foreach (var id in _rows.Keys.Where(id => sessions.All(s => s.Id != id)).ToList())
        {
            Rows.Children.Remove(_rows[id].Panel);
            _rows.Remove(id);
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (!_rows.TryGetValue(s.Id, out var row))
            {
                row = MakeRow();
                row.Dot.Paused = !IsVisible;
                _rows[s.Id] = row;
            }
            if (Rows.Children.IndexOf(row.Panel) != i)
            {
                Rows.Children.Remove(row.Panel);
                Rows.Children.Insert(i, row.Panel);
            }

            row.Dot.SetState(s.State);
            row.Project.Text = s.Project;
            row.Activity.Text = s.Detail.Length > 0 ? $"{s.Activity} {s.Detail}" : s.Activity;
            row.Age.Text = Age(now - s.SinceUtc);
        }
    }

    private Row MakeRow()
    {
        var panel = new Grid { Margin = new Thickness(12, 4, 16, 4) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new SparkIcon(14) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        var project = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var activity = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var age = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(project);
        text.Children.Add(activity);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(age, 2);
        panel.Children.Add(dot);
        panel.Children.Add(text);
        panel.Children.Add(age);
        return new Row(panel, dot, project, activity, age);
    }

    /// <summary>How long ago the session last changed: "now", "4 min", "2 h".</summary>
    private static string Age(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "now",
        < 60 => $"{(int)span.TotalMinutes} min",
        < 60 * 48 => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d",
    };

    // ---------- Opening and closing ----------

    private bool _closing;
    private int _animationVersion;

    /// <summary>Shown and not on its way out.</summary>
    public bool IsOpen => IsVisible && !_closing;

    /// <summary>Opens centered on <paramref name="centerX"/> with its bottom edge at <paramref name="bottom"/> (screen DIPs).</summary>
    public void ShowAbove(double centerX, double bottom)
    {
        bool fresh = !IsVisible;
        _closing = false;
        _animationVersion++;
        PlaceAbove(centerX, bottom);
        if (fresh) Show(); // not activated, so hovering never takes keyboard focus from what you're doing
        // Fade in while sliding up. If it was mid-close, it turns around from where it is.
        Animate(to: 1, shift: 0, fresh ? 0 : null, fresh ? 12 : null, EasingMode.EaseOut, null);
    }

    public void PlaceAbove(double centerX, double bottom)
    {
        (_centerX, _bottom) = (centerX, bottom);
        Left = centerX - Width / 2;
        Top = bottom - (ActualHeight > 0 ? ActualHeight : 80);
    }

    /// <summary>Fades out while sliding down, then hides: the opening animation in reverse.</summary>
    public void HideAnimated()
    {
        if (!IsOpen) return;
        _closing = true;
        int version = ++_animationVersion;
        Animate(to: 0, shift: 12, null, null, EasingMode.EaseIn, () =>
        {
            if (version != _animationVersion) return; // reopened while fading out
            _closing = false;
            Hide();
        });
    }

    private void Animate(double to, double shift, double? fromOpacity, double? fromShift, EasingMode mode, Action? done)
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = mode };
        var fade = new DoubleAnimation { From = fromOpacity, To = to, Duration = duration, EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        Root.BeginAnimation(OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation { From = fromShift, To = shift, Duration = duration, EasingFunction = ease });
    }
}
