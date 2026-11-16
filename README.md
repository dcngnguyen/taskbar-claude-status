# Claude Status

A small status bar on the Windows taskbar for Claude Code. Each open session gets a small orange spark that shows its state by how it moves:

- working: its rays grow and shrink in a ripple
- waiting for you (a permission prompt or a question): it turns amber and pulses like a slow heartbeat
- done: it blooms once, then rests
- stopped (you pressed stop): its rays fold in and it turns grey
- closed: it shrinks away

Hover over the bar to see every session with its project folder and what it's doing. The bar sits just left of Weather Bar (or Music Bar, or the tray icons), and only shows while a session is open.

It works for Claude Code in the terminal, in VS Code, and in the Code tab of the Claude desktop app. It can't see cloud sessions or normal chats.

## How it works

Claude Code runs `ClaudeStatusHook.exe` at moments like "you sent a message", "about to use a tool" and "finished" (these are called hooks). The hook saves each session's state to a small file in `%APPDATA%\ClaudeStatus\sessions`, and the bar watches those files. Two things have no hook, so the bar works them out itself: pressing stop (Claude Code writes "[Request interrupted by user]" to the session's log) and a session that closed or crashed (its Claude Code process is gone).

## Install or update

Needs the .NET 10 SDK. In PowerShell, from this folder:

```powershell
.\install.ps1
```

This builds both programs, copies them to `%LOCALAPPDATA%\Programs\ClaudeStatus`, starts the bar, and makes it start when you sign in.

Then add the hook to `~/.claude/settings.json`, under `"hooks"`, for each of these events: `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Notification`, `Stop`, `StopFailure`, `SessionEnd`:

```json
{ "hooks": [ { "type": "command", "command": "C:/Users/<you>/AppData/Local/Programs/ClaudeStatus/ClaudeStatusHook.exe", "async": true } ] }
```

`async` means Claude never waits for it. For `SessionEnd`, use `"timeout": 5` instead of `"async": true`, so it still runs while Claude Code is closing.

## Known limits

- After you approve a permission prompt, the spark stays amber until that tool finishes (there's no hook for "permission answered").
- Agents running in the background after Claude's reply has finished still show as done.

## Uninstall

1. Remove the `ClaudeStatusHook` entries from `~/.claude/settings.json` first (otherwise Claude Code reports a hook error on every step).
2. Right-click the bar and choose **Quit Claude Status**.
3. Delete `%LOCALAPPDATA%\Programs\ClaudeStatus` and `%APPDATA%\ClaudeStatus`.
4. Remove the `ClaudeStatus` value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (or disable it in Task Manager, Startup apps).
