# Builds Claude Status, copies it to %LOCALAPPDATA%\Programs\ClaudeStatus, and starts it.
# Run again after any code change to update the installed copy.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeStatus'

Get-Process ClaudeStatus -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($project in 'ClaudeStatus\ClaudeStatus.csproj', 'ClaudeStatusHook\ClaudeStatusHook.csproj') {
    dotnet publish (Join-Path $PSScriptRoot $project) -c Release -r win-x64 --self-contained false -o $dest
    if ($LASTEXITCODE -ne 0) { throw "Build of $project failed, see the messages above. (If a file was in use, Claude was running the hook at that moment: just run it again.)" }
}

Start-Process (Join-Path $dest 'ClaudeStatus.exe')
Write-Host "Claude Status installed to $dest and started."
