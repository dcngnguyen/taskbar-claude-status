using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeStatus;

/// <summary>One session as the bar shows it.</summary>
public sealed record SessionView(string Id, string State, string Activity, string Detail, string Project, DateTime SinceUtc);

/// <summary>
/// Reads the session files the hook writes and works out what each session is really doing.
/// Two things have no hook, so they are detected here:
/// pressing stop (Claude Code writes "[Request interrupted by user]" to the session's log)
/// and a session that closed or crashed (its Claude Code process is gone).
/// </summary>
public sealed partial class SessionWatcher
{
    public const string Stopped = "stopped";

    private static readonly TimeSpan ShowClosedFor = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProcessCheckEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TranscriptCheckEvery = TimeSpan.FromSeconds(1);
    // Without a known process there is no way to tell a closed session from a quiet one, so give up after a long time.
    private static readonly TimeSpan ForgetUnknownAfter = TimeSpan.FromHours(12);
    // Exactly as Claude Code writes it. Inside quoted text (a file Claude read, a reply mentioning it)
    // the quotes would be escaped as \", so this can't match there by accident.
    private const string InterruptMarker = "\"text\":\"[Request interrupted by user";
    private const int TailBytes = 512 * 1024;

    private sealed class Tracked
    {
        public required SessionRecord Record;
        public (DateTime, long) FileStamp;
        public DateTime FirstSeen;
        public DateTime? InterruptedUtc;
        public (DateTime, long) TranscriptStamp;
        public long TranscriptReadTo;
        public DateTime TranscriptCheckedAt;
        public DateTime ProcessCheckedAt;
        public bool ProcessGone;
        public DateTime? EndedAt;
    }

    private readonly Dictionary<string, Tracked> _sessions = new();

    /// <summary>Re-reads what changed and returns every session to show, oldest first.</summary>
    public List<SessionView> Poll()
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>();
        try
        {
            if (Directory.Exists(StatusPaths.SessionsDir))
            {
                foreach (var path in Directory.EnumerateFiles(StatusPaths.SessionsDir, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (!StatusPaths.IsSafeId(id)) continue;
                    seen.Add(id);
                    // Time and size together: two quick writes can share the same time stamp.
                    var info = new FileInfo(path);
                    var stamp = (info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
                    if (_sessions.TryGetValue(id, out var known) && known.FileStamp == stamp) continue;
                    if (SessionRecord.TryLoad(path) is not { } record) continue; // being replaced: try next time
                    if (known is null)
                    {
                        _sessions[id] = new Tracked { Record = record, FileStamp = stamp, FirstSeen = now };
                    }
                    else
                    {
                        known.Record = record;
                        known.FileStamp = stamp;
                        known.ProcessGone = false;
                        known.ProcessCheckedAt = default;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not read the sessions folder: " + ex.Message);
        }

        foreach (var id in _sessions.Keys.Where(id => !seen.Contains(id)).ToList()) _sessions.Remove(id);

        var views = new List<SessionView>();
        foreach (var (id, s) in _sessions.OrderBy(p => p.Value.FirstSeen).ToList())
        {
            var view = Describe(s, now);
            if (view.State == States.Ended)
            {
                s.EndedAt ??= now;
                if (now - s.EndedAt > ShowClosedFor)
                {
                    Forget(id);
                    continue;
                }
            }
            else
            {
                s.EndedAt = null;
            }
            views.Add(view);
        }
        return views;
    }

    private SessionView Describe(Tracked s, DateTime now)
    {
        var r = s.Record;
        string project = ProjectName(r.Cwd);

        if (r.State == States.Ended || IsProcessGone(s, now))
            return new(r.SessionId, States.Ended, "Closed", "", project, r.UpdatedUtc);

        if (r.State is States.Working or States.Waiting)
        {
            CheckForInterrupt(s, now);
            if (s.InterruptedUtc is { } at && at > r.UpdatedUtc)
                return new(r.SessionId, Stopped, "Stopped", "", project, at);
        }
        return new(r.SessionId, r.State, r.Activity, r.Detail, project, r.UpdatedUtc);
    }

    private static bool IsProcessGone(Tracked s, DateTime now)
    {
        if (s.Record.ClaudePid == 0) return now - s.Record.UpdatedUtc > ForgetUnknownAfter;
        if (now - s.ProcessCheckedAt >= ProcessCheckEvery)
        {
            s.ProcessCheckedAt = now;
            s.ProcessGone = !ProcessInfo.IsRunning(s.Record.ClaudePid, s.Record.ClaudeStartTime);
        }
        return s.ProcessGone;
    }

    /// <summary>Looks at the end of the session's log for the line Claude Code writes when you press stop.</summary>
    private static void CheckForInterrupt(Tracked s, DateTime now)
    {
        string path = s.Record.TranscriptPath;
        if (path.Length == 0 || now - s.TranscriptCheckedAt < TranscriptCheckEvery) return;
        s.TranscriptCheckedAt = now;
        try
        {
            var info = new FileInfo(path);
            var logStamp = (info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
            if (!info.Exists || logStamp == s.TranscriptStamp) return;

            string tail = ReadNew(path, ref s.TranscriptReadTo);
            s.TranscriptStamp = logStamp; // only once it was read, so a busy file is tried again
            int at = tail.LastIndexOf(InterruptMarker, StringComparison.Ordinal);
            if (at < 0) return;
            int lineStart = tail.LastIndexOf('\n', at) + 1;
            int lineEnd = tail.IndexOf('\n', at);
            string line = lineEnd < 0 ? tail[lineStart..] : tail[lineStart..lineEnd];
            var match = TimestampPattern().Match(line);
            if (match.Success && DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
            {
                s.InterruptedUtc = stamp;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The log may be busy or gone. Try again when it next changes.
        }
    }

    /// <summary>
    /// Reads what was added to the log since last time (plus a little overlap, so a line cut in half
    /// last time is seen whole), and at most the last 512 KB.
    /// </summary>
    private static string ReadNew(string path, ref long readTo)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - TailBytes);
        if (readTo <= stream.Length) start = Math.Max(start, readTo - 4096);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        readTo = start + read; // only after a successful read, so a failed one is retried
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>The project folder's name, like "taskbar-weather".</summary>
    private static string ProjectName(string cwd)
    {
        if (cwd.Length == 0) return "Claude Code";
        string name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : cwd;
    }

    /// <summary>Removes a closed session's file so it doesn't come back.</summary>
    private void Forget(string id)
    {
        _sessions.Remove(id);
        try { File.Delete(Path.Combine(StatusPaths.SessionsDir, id + ".json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Could not remove a closed session: " + ex.Message); }
    }

    [GeneratedRegex("\"timestamp\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex TimestampPattern();
}
