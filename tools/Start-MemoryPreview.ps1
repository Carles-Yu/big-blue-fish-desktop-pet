param()
$ErrorActionPreference = 'Stop'
$preview = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.work/memory-preview-windows'))
$memory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.work/memory-preview-user'))
$executable = Join-Path $preview 'BigBlueFish.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the memory preview before starting it; see docs/MEMORY_SYNC_ACCEPTANCE.md.' }
$preferences = Join-Path $preview 'preferences.json'
if (-not (Test-Path -LiteralPath $preferences)) {
    $initial = @{
        ModelEnabled = $false; PublicInfo = $false; ForegroundEnabled = $false
        CheckUpdatesOnStartup = $false; MemoryEnabled = $true; SyncEnabled = $false; McpEnabled = $false
        SyncServerUrl = 'https://hcss-ecs-1b1e.tailc95d8b.ts.net'
    }
    [IO.File]::WriteAllText($preferences, ($initial | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
$launch = [Diagnostics.ProcessStartInfo]::new($executable)
$launch.UseShellExecute = $false
$launch.WorkingDirectory = $preview
$launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
$launch.Environment['BIGBLUEFISH_MEMORY'] = $memory
[Diagnostics.Process]::Start($launch) | Out-Null
Write-Output 'Memory preview started with a separate data directory. Existing installation and memory were not changed.'
