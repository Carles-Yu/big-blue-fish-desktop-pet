param([string]$SdkPath,[string]$InstalledDirectory='C:\Program Files\蓝色大肥鱼',[ValidateSet('Ocr','Vision')][string]$InputMode='Ocr',[switch]$Fast)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/tests/PowerShellEncodingVerification.ps1"
    if(!$SdkPath -and $env:LOCALAPPDATA){
        $devSdk=Join-Path $env:LOCALAPPDATA 'BigBlueFish-dev\dotnet\dotnet.exe'
        if(Test-Path -LiteralPath $devSdk){$SdkPath=$devSdk}
    }
    $sdk=& "$PSScriptRoot/tools/Resolve-Sdk.ps1" -SdkPath $SdkPath
    $sandbox=Join-Path $PSScriptRoot '.verification'
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    $runnerOut=(Join-Path $sandbox "runner-out-$InputMode") + '\'
    New-Item -ItemType Directory -Force -Path $runnerOut | Out-Null

    function Invoke-Checked([string]$Failure,[scriptblock]$Action){
        & $Action
        if($LASTEXITCODE -ne 0){throw $Failure}
    }
    function Invoke-Runner([string]$Failure,[string[]]$TestArgs){
        Invoke-Checked $Failure {
            & $sdk run --project "$PSScriptRoot/tests/RunnerVerification/RunnerVerification.csproj" -c Release -p:Platform=x64 "-p:CompanionInputMode=$InputMode" "-p:OutputPath=$runnerOut" -- @TestArgs
        }
    }

    Invoke-Checked '动画规则验证失败' {
        & $sdk run --project "$PSScriptRoot/tests/AnimationVerification/AnimationVerification.csproj" -c Release -- "$PSScriptRoot/BigBlueFish.Windows/assets/fish"
    }
    Invoke-Checked '记忆验证失败' {
        & $sdk run --project "$PSScriptRoot/tests/MemoryVerification/MemoryVerification.csproj" -c Release
    }
    Invoke-Runner '聊天搜索验证失败' @('--chat-search')
    Invoke-Runner 'Cursor 额度验证失败' @('--cursor-quota')
    Invoke-Runner 'Gemini 额度验证失败' @('--gemini-quota')
    Invoke-Runner 'Codex 额度验证失败' @('--codex-quota',(Join-Path $sandbox "codex-quota-$InputMode"))
    Invoke-Runner '监工界面验证失败' @('--agent-ui',(Join-Path $sandbox "agent-ui-$InputMode"))

    $mcpOut=Join-Path $sandbox 'mcp-bridge'
    New-Item -ItemType Directory -Force -Path $mcpOut | Out-Null
    Invoke-Checked 'MCP 桥接程序编译失败' {
        & $sdk publish "$PSScriptRoot/BigBlueFish.Mcp/BigBlueFish.Mcp.csproj" -c Release -o $mcpOut
    }
    Invoke-Checked '监工协议验证失败' {
        & $sdk run --project "$PSScriptRoot/tests/McpProtocolVerification/McpProtocolVerification.csproj" -c Release -- (Join-Path $mcpOut 'BigBlueFish.Mcp.exe')
    }
    & "$PSScriptRoot/tests/ModelScriptsVerification.ps1"

    if($Fast){return}

    if(Get-CimInstance Win32_Process -Filter "Name='BigBlueFish.exe' OR Name='VPet-Simulator.Windows.exe'"){throw '请先从托盘菜单退出桌宠，再执行实窗测试。'}
    foreach($name in @('assets','runtime','models')){
        $target=if($name -eq 'assets'){Join-Path $PSScriptRoot 'BigBlueFish.Windows/assets'}else{Join-Path $InstalledDirectory $name}
        $link=Join-Path $sandbox $name
        if(!(Test-Path -LiteralPath $link)){New-Item -ItemType Junction -Path $link -Target $target | Out-Null}
    }
    Invoke-Runner '云端与安静时段验证失败' @('--cloud-quiet',(Join-Path $sandbox "cloud-quiet-$InputMode"))
    Invoke-Runner '天气缓存验证失败' @('--weather-cache',(Join-Path $sandbox "weather-$InputMode"))
    Invoke-Runner 'Runner故障恢复或提示词验证失败' @((Join-Path $sandbox "runner-$InputMode"))
    Invoke-Checked '前台窗口验证失败' {
        & $sdk run --project "$PSScriptRoot/tests/ForegroundVerification/ForegroundVerification.csproj" -c Release -p:Platform=x64 "-p:CompanionInputMode=$InputMode"
    }
    Invoke-Checked '动画与设置实窗验证失败' {
        & $sdk run --project "$PSScriptRoot/tests/AnimationUiVerification/AnimationUiVerification.csproj" -c Release -p:Platform=x64 "-p:CompanionInputMode=$InputMode" -- $sandbox
    }
} finally {Pop-Location}
