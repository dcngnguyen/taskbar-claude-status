using System.IO;
using System.Text.Json.Serialization;

namespace ClaudeStatus;

/// <summary>
/// What one Claude Code session is doing. The hook writes one of these per session,
/// and the taskbar app reads them.
/// </summary>
public sealed class SessionRecord
{
    public string SessionId { get; set; } = "";

    /// <summary>"idle", "working", "waiting" or "ended" (see <see cref="States"/>).</summary>
    public string State { get; set; } = States.Idle;

    /// <summary>A short word for the bar, like "Editing" or "Thinking".</summary>
    public string Activity { get; set; } = "";

    /// <summary>Extra detail for the hover window, like the file being edited. Can be empty.</summary>
    public string Detail { get; set; } = "";

    /// <summary>The tool call a "waiting" state is waiting on (empty = any tool activity ends the wait).</summary>
    public string WaitingFor { get; set; } = "";

    public string Cwd { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The Claude Code process, so the app can tell when it has closed or crashed (0 = unknown).</summary>
    public int ClaudePid { get; set; }
    public long ClaudeStartTime { get; set; }

    /// <summary>Reads a session file, or returns null if it's missing, being replaced, or not valid.</summary>
    public static SessionRecord? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return System.Text.Json.JsonSerializer.Deserialize(stream, StatusJson.Default.SessionRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public static class States
{
    public const string Idle = "idle", Working = "working", Waiting = "waiting", Ended = "ended";
}

/// <summary>Where the session files live: %APPDATA%\ClaudeStatus\sessions.</summary>
public static class StatusPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeStatus");

    public static readonly string SessionsDir = Path.Combine(DataDir, "sessions");

    /// <summary>Session ids come from outside, so only allow plain ids (no slashes or dots that could escape the folder).</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

[JsonSerializable(typeof(SessionRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class StatusJson : JsonSerializerContext;
