param([string]$SshHost = 'bluefish')
$ErrorActionPreference = 'Stop'
if ($SshHost -notmatch '^[A-Za-z0-9._-]+$') { throw 'Use an SSH config alias without spaces or shell syntax.' }
& ssh -o BatchMode=yes -o ConnectTimeout=12 $SshHost 'systemd-run --quiet --pipe --wait --collect -p User=fishsync -p EnvironmentFile=/etc/bigbluefish-sync/server.env -p Environment=DOTNET_ROOT=/opt/bigbluefish-sync/dotnet /opt/bigbluefish-sync/current/BigBlueFish.SyncServer --pair'
if ($LASTEXITCODE -ne 0) { throw 'Could not generate a pairing code. Check SSH access to the private sync server.' }
