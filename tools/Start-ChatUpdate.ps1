param()
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Model-Helpers.ps1"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $projectRoot '.work/chat-preview'
$target = Join-Path $projectRoot '.work/dev-run'
$exe = Join-Path $target 'BigBlueFish.exe'
if (Get-CimInstance Win32_Process -Filter "Name='BigBlueFish.exe' OR Name='VPet-Simulator.Windows.exe'") {
    throw 'Please exit the running fish from its tray menu, then run this script again.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'BigBlueFish.dll'))) { throw 'Chat build is missing.' }
Sync-DevelopmentRunDirectory $source $target (Join-Path $projectRoot 'code/BigBlueFish.Windows/assets')
Start-Process -FilePath $exe -WorkingDirectory $target
