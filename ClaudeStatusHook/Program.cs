using System.IO;
using System.Text.Json;
using ClaudeStatus;

// Claude Code runs this at moments like "you sent a message", "about to use a tool" and "finished".
// It gets the details as JSON on standard input and saves the session's new state to a small file
// that the taskbar app watches. It must never get in Claude's way: no output, always exit code 0.

var startedUtc = DateTime.UtcNow;
try
{
    Run(Console.In.ReadToEnd(), startedUtc);
}
catch (Exception ex)
{
    try
    {
        Directory.CreateDirectory(StatusPaths.DataDir);
        File.AppendAllText(Path.Combine(StatusPaths.DataDir, "hook-errors.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Message}{Environment.NewLine}");
    }
    catch
    {
        // Nothing more we can do, and Claude must not be disturbed.
    }
}
return 0;

static void Run(string input, DateTime startedUtc)
{
    using var doc = JsonDocument.Parse(input);
    var root = doc.RootElement;
    string id = Text(root, "session_id");
    if (!StatusPaths.IsSafeId(id)) return;

    // Events can finish out of order (most run in the background), so order them by when Claude Code
    // launched them, not by when this program got going: that can differ by a tenth of a second.
    var (claudePid, claudeStart, launchedAt) = ProcessInfo.FindClaudeProcess();
    var eventUtc = launchedAt > 0 ? DateTime.FromFileTimeUtc(launchedAt) : startedUtc;

    Directory.CreateDirectory(StatusPaths.SessionsDir);
    string path = Path.Combine(StatusPaths.SessionsDir, id + ".json");

    // One writer at a time per session, so two quick events can't mix their files.
    using var gate = new Mutex(false, @"Local\ClaudeStatus.Session." + id);
    bool owned;
    try { owned = gate.WaitOne(TimeSpan.FromSeconds(4)); }
    catch (AbandonedMutexException) { owned = true; }
    if (!owned) return;
    try
    {
        var existing = SessionRecord.TryLoad(path);
        // A late event for a session whose Claude Code has already closed (and whose file the app
        // has cleaned up) must not bring it back as a ghost.
        if (existing is null && claudePid == 0 && Text(root, "hook_event_name") is not ("SessionStart" or "UserPromptSubmit")) return;
        var record = existing ?? new SessionRecord { SessionId = id };
        if (record.UpdatedUtc > eventUtc) return; // a later event already wrote

        if (!Apply(record, root, eventUtc)) return;
        record.UpdatedUtc = eventUtc;
        if (Text(root, "cwd") is { Length: > 0 } cwd) record.Cwd = cwd;
        if (Text(root, "transcript_path") is { Length: > 0 } transcript) record.TranscriptPath = transcript;
        if (claudePid != 0) (record.ClaudePid, record.ClaudeStartTime) = (claudePid, claudeStart);

        Save(path, record);
    }
    finally
    {
        gate.ReleaseMutex();
    }
}

/// <summary>Writes the file in one step, so the app never sees a half-written one.</summary>
static void Save(string path, SessionRecord record)
{
    string temp = path + "." + Environment.ProcessId + ".tmp";
    try
    {
        File.WriteAllText(temp, JsonSerializer.Serialize(record, StatusJson.Default.SessionRecord));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50); // e.g. a virus scanner has the file open for a moment
            }
        }
    }
    finally
    {
        if (File.Exists(temp)) File.Delete(temp);
    }
}

/// <summary>Updates the record for this event. Returns false when the event shouldn't change anything.</summary>
static bool Apply(SessionRecord r, JsonElement root, DateTime eventUtc)
{
    // Tool calls made by helper agents. They never start or end the session's work,
    // but one may answer the permission prompt it was waiting on.
    bool fromAgent = Text(root, "agent_id").Length > 0;
    bool waitingOnTool = r.State == States.Waiting && r.WaitingFor.Length > 0;

    switch (Text(root, "hook_event_name"))
    {
        case "SessionStart":
            // After compacting, Claude may still be in the middle of its work.
            if (Text(root, "source") == "compact") return false;
            return Set(r, States.Idle, "Ready");
        case "UserPromptSubmit":
            return Set(r, States.Working, "Thinking");
        case "PreToolUse":
        {
            // Another tool starting right as a prompt appears doesn't end the wait. One starting
            // a while later does: you answered (e.g. said no, with a note) and Claude moved on.
            if (fromAgent || (waitingOnTool && eventUtc - r.UpdatedUtc < TimeSpan.FromSeconds(1))) return false;
            var (state, activity, detail) = DescribeTool(root);
            return Set(r, state, activity, detail, state == States.Waiting ? ToolKey(root) : "");
        }
        case "PostToolUse":
            // Only the tool the prompt was about ends the wait; others finishing meanwhile don't.
            if (waitingOnTool && r.WaitingFor != ToolKey(root)) return false;
            if (fromAgent && !waitingOnTool) return false;
            return Set(r, States.Working, "Thinking");
        case "PermissionRequest":
            return Set(r, States.Waiting, "Needs permission", ToolDetail(root), ToolKey(root));
        case "Notification":
            if (r.State == States.Waiting) return false; // already showing it
            return Text(root, "notification_type") switch
            {
                "permission_prompt" => Set(r, States.Waiting, "Needs permission"),
                "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input" => Set(r, States.Waiting, "Needs you"),
                _ => false, // e.g. "idle_prompt": still idle, nothing new
            };
        case "Stop":
            return Set(r, States.Idle, "Done");
        case "StopFailure":
            return Set(r, States.Idle, "Stopped by an error");
        case "SessionEnd":
            return Set(r, States.Ended, "Closed");
        default:
            return false;
    }
}

static bool Set(SessionRecord r, string state, string activity, string detail = "", string waitingFor = "")
{
    (r.State, r.Activity, r.Detail, r.WaitingFor) = (state, activity, detail, waitingFor);
    return true;
}

static (string, string, string) DescribeTool(JsonElement root)
{
    string tool = Text(root, "tool_name");
    string detail = ToolDetail(root);
    return tool switch
    {
        "AskUserQuestion" => (States.Waiting, "Question for you", ""),
        "ExitPlanMode" => (States.Waiting, "Plan ready", ""),
        "Read" => (States.Working, "Reading", detail),
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => (States.Working, "Editing", detail),
        "Bash" or "PowerShell" => (States.Working, "Running a command", ""),
        "Grep" or "Glob" or "LSP" => (States.Working, "Searching", ""),
        "WebFetch" or "WebSearch" => (States.Working, "Searching the web", ""),
        "Agent" or "Task" or "Workflow" => (States.Working, "Running agents", ""),
        "TodoWrite" or "TaskCreate" or "TaskUpdate" => (States.Working, "Planning", ""),
        _ => (States.Working, "Working", ""),
    };
}

/// <summary>Identifies one tool call: its name plus exactly what it was asked to do.</summary>
static string ToolKey(JsonElement root)
{
    string input = root.TryGetProperty("tool_input", out var value) ? value.GetRawText() : "";
    // Stored as a short fingerprint: the input can be a whole file's contents.
    var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Text(root, "tool_name") + "|" + input));
    return Convert.ToHexString(hash, 0, 12);
}

/// <summary>The file name a tool is working on, if any ("BarWindow.cs").</summary>
static string ToolDetail(JsonElement root)
{
    if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return "";
    foreach (var key in new[] { "file_path", "notebook_path" })
    {
        if (Text(input, key) is { Length: > 0 } file)
        {
            try { return Path.GetFileName(file); }
            catch (ArgumentException) { return ""; }
        }
    }
    return "";
}

static string Text(JsonElement obj, string name) =>
    obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : "";
