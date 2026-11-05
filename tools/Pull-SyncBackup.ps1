param(
    [string]$SshHost = 'bluefish',
    [string]$Destination = (Join-Path $env:USERPROFILE '.codex/private-backups/bluefish/snapshots')
)
$ErrorActionPreference = 'Stop'
if ($SshHost -notmatch '^[A-Za-z0-9._-]+$') { throw 'Use an SSH config alias without spaces or shell syntax.' }
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$remoteCommand = 'python3 -c ''from pathlib import Path; import json,hashlib; files=sorted(Path("/var/backups/bigbluefish-sync").glob("fish-*.pgdump.aesgcm")); p=files[-1] if files else None; print(json.dumps({"path":str(p),"sha256":hashlib.sha256(p.read_bytes()).hexdigest()})) if p else exit(2)'''
$result = & ssh -o BatchMode=yes -o ConnectTimeout=12 $SshHost $remoteCommand
if ($LASTEXITCODE -ne 0) { throw 'Cannot read server backup metadata; existing local copies are unchanged.' }
$metadata = ($result -join "`n") | ConvertFrom-Json
if ($metadata.path -notmatch '^/var/backups/bigbluefish-sync/(fish-[0-9TZ]+-[a-f0-9]+\.pgdump\.aesgcm)$' -or $metadata.sha256 -notmatch '^[a-f0-9]{64}$') {
    throw 'Server returned unexpected backup metadata.'
}
$filename = [IO.Path]::GetFileName($metadata.path)
$target = Join-Path $Destination $filename
if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $metadata.sha256) {
    Write-Output 'The latest encrypted backup is already saved and verified.'
    return
}
$partial = $target + '.' + [guid]::NewGuid().ToString('N') + '.partial'
try {
    & scp -o BatchMode=yes -o ConnectTimeout=12 "${SshHost}:$($metadata.path)" $partial
    if ($LASTEXITCODE -ne 0) { throw 'Encrypted backup transfer failed.' }
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $metadata.sha256) { throw 'Encrypted backup checksum mismatch.' }
    [IO.File]::Move($partial, $target, $true)
    Write-Output "Encrypted backup saved and verified: $filename"
}
finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
}
