param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $projectRoot '.work/chat-preview'
$target = Join-Path $projectRoot 'code/BigBlueFish.Windows/bin/x64/Release/net10.0-windows10.0.26100.0'
$exe = Join-Path $target 'BigBlueFish.exe'
$active = Get-Process BigBlueFish -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($active) { throw 'Please exit the running fish from its tray menu, then run this script again.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'BigBlueFish.dll'))) { throw 'Chat build is missing.' }
foreach ($name in @('BigBlueFish.exe','BigBlueFish.dll','BigBlueFish.pdb','BigBlueFish.deps.json','BigBlueFish.runtimeconfig.json','BigBlueFish.Memory.dll','BigBlueFish.Control.dll','Tomlyn.dll')) {
    $file = Join-Path $source $name
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $target $name) -Force }
}
Start-Process -FilePath $exe -WorkingDirectory $target
