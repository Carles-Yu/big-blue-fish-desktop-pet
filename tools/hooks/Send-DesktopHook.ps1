
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('cursor', 'codex', 'gemini')]
    [string]$Source
)
$ErrorActionPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'

function Write-GeminiAllow {
    if ($Source -ne 'gemini') { return }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $bytes = $utf8.GetBytes('{"decision":"allow"}' + [char]10)
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
}

try {
    $stdin = [Console]::In.ReadToEnd()
    $endpointPath = Join-Path $env:LOCALAPPDATA 'BigBlueFish\desktop-hooks\endpoint.json'
    if ([string]::IsNullOrWhiteSpace($stdin)) { return }
    if (-not (Test-Path -LiteralPath $endpointPath)) { return }

    $endpoint = Get-Content -LiteralPath $endpointPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $url = [string]$endpoint.url
    $token = [string]$endpoint.token
    if ($url -notmatch '^http://127\.0\.0\.1:\d+/hook$') { return }
    if ([string]::IsNullOrWhiteSpace($token)) { return }

    $payload = $null
    try { $payload = $stdin | ConvertFrom-Json } catch { return }

    $envMap = @{}
    foreach ($name in @('CURSOR_PROJECT_DIR', 'CURSOR_VERSION', 'CURSOR_TRANSCRIPT_PATH', 'CURSOR_CODE_REMOTE', 'CURSOR_PLUGIN_ROOT')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $envMap[$name] = $value }
    }

    $bodyObject = @{
        source  = $Source
        payload = $payload
        env     = $envMap
    }
    $body = $bodyObject | ConvertTo-Json -Compress -Depth 16
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)

    $request = [System.Net.HttpWebRequest]::Create($url)
    $request.Method = 'POST'
    $request.ContentType = 'application/json; charset=utf-8'
    $request.Headers.Add('Authorization', "Bearer $token")
    $request.Timeout = 800
    $request.ReadWriteTimeout = 800
    $request.KeepAlive = $false
    $request.ServicePoint.Expect100Continue = $false
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    try {
        $response = $request.GetResponse()
        $response.Close()
    } catch {
    }
}
catch {
}
finally {
    Write-GeminiAllow
    exit 0
}
