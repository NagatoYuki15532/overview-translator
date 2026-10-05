<#
.SYNOPSIS
    构建 Emby.Plugin.OverviewTranslator（net6.0 / net8.0 双目标）。

.DESCRIPTION
    本插件编译时需要 Emby 服务端的程序集（MediaBrowser.Common.dll /
    MediaBrowser.Controller.dll / MediaBrowser.Model.dll）——Emby 是部署好的应用，
    不是 NuGet 包，所以要从**已安装的 Emby** 的 system 目录里引用。

    Emby 目录的解析顺序：
      1. -EmbySystemDir 参数
      2. 环境变量 EMBY_SYSTEM_DIR
      3. 常见默认位置（Windows: %APPDATA%\Emby-Server\system；
         Linux: ~/.config/emby-server/system, /opt/emby-server/system, /var/lib/emby/system）

    目标框架与 Emby 版本的对应关系：
      net8.0 —— Emby Server 4.10 及更新版本
      net6.0 —— Emby Server 4.9.x 及更早版本
    请指向**与目标框架匹配的 Emby 安装**。例如给 net6.0 目标指向 Emby 4.10 的目录
    （那是 .NET 8 的程序集）也能编译通过，但会出现 MSB3277 版本冲突警告；
    干净的做法是给 net6.0 指 4.9 的 system 目录。

.PARAMETER EmbySystemDir
    已安装 Emby 的 system 目录。

.PARAMETER Configuration
    Debug / Release，默认 Release。

.PARAMETER TargetFramework
    只编译其中一个目标（net6.0 或 net8.0），默认两个都编。

.PARAMETER NoRestore
    跳过 NuGet 还原（--no-restore），离线或已还原过时可用。

.EXAMPLE
    pwsh ./build.ps1
    用自动探测到的 Emby 目录编译两个目标。

.EXAMPLE
    pwsh ./build.ps1 -EmbySystemDir 'D:\Emby-Server\system' -Configuration Release

.EXAMPLE
    pwsh ./build.ps1 -TargetFramework net8.0
    只编 net8.0（装到 Emby 4.10+）。
#>
[CmdletBinding()]
param(
    [string]$EmbySystemDir = $env:EMBY_SYSTEM_DIR,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('net6.0', 'net8.0')]
    [string]$TargetFramework,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'Emby.Plugin.OverviewTranslator.csproj'

function Get-EmbySystemDirCandidate {
    $candidates = @()
    if ($env:APPDATA) { $candidates += (Join-Path $env:APPDATA 'Emby-Server/system') }
    if ($env:HOME) { $candidates += (Join-Path $env:HOME '.config/emby-server/system') }
    $candidates += '/opt/emby-server/system'
    $candidates += '/var/lib/emby/system'
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'MediaBrowser.Common.dll'))) {
            return $candidate
        }
    }
    return $null
}

if (-not $EmbySystemDir) {
    $EmbySystemDir = Get-EmbySystemDirCandidate
}

if (-not $EmbySystemDir -or -not (Test-Path -LiteralPath (Join-Path $EmbySystemDir 'MediaBrowser.Common.dll'))) {
    Write-Host ''
    Write-Host '错误：找不到 Emby 服务端程序集。' -ForegroundColor Red
    Write-Host ''
    Write-Host '本插件在编译期需要 Emby 安装目录下 system 子目录里的 MediaBrowser.Common.dll 等程序集。'
    Write-Host '请用下列任一方式指定这个目录：'
    Write-Host ''
    Write-Host '  1) 命令行参数：'
    Write-Host '       pwsh ./build.ps1 -EmbySystemDir "C:\Users\<你>\AppData\Roaming\Emby-Server\system"' -ForegroundColor Cyan
    Write-Host '     （只有 Windows PowerShell 5.1 时用 powershell -File ./build.ps1 -EmbySystemDir "..."）'
    Write-Host ''
    Write-Host '  2) 环境变量：'
    Write-Host '       $env:EMBY_SYSTEM_DIR = "C:\Users\<你>\AppData\Roaming\Emby-Server\system"' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  3) 直接用 dotnet：'
    Write-Host '       dotnet build -c Release -p:EmbySystemDir="<system 目录>"' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '常见位置：'
    Write-Host '  Windows  %APPDATA%\Emby-Server\system'
    Write-Host '  Linux    /opt/emby-server/system 或 /var/lib/emby/system'
    Write-Host ''
    Write-Host "当前值：'$EmbySystemDir'"
    Write-Host '（如果不确定，可在 Emby 控制台 → 高级 → 关于，或日志 logs/embyserver.txt 里找到安装路径。）'
    exit 1
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host '错误：PATH 里找不到 dotnet。请先安装 .NET SDK 8.0（编译 net6.0 目标会自动下载 .NET 6 的引用包）。' -ForegroundColor Red
    exit 1
}

Write-Host "Emby system 目录 : $EmbySystemDir"
Write-Host "配置             : $Configuration"
Write-Host "目标框架         : $(if ($TargetFramework) { $TargetFramework } else { 'net6.0;net8.0（全部）' })"
Write-Host ''

$buildArgs = @(
    'build', $project,
    '-c', $Configuration,
    "-p:EmbySystemDir=$EmbySystemDir",
    '--nologo'
)
if ($TargetFramework) { $buildArgs += @('-f', $TargetFramework) }
if ($NoRestore) { $buildArgs += '--no-restore' }

# 原生命令往 stderr 写日志（编译器警告等）时，不应被 PowerShell 当成终止错误中断脚本。
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $dotnet.Source @buildArgs
$exitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference

if ($exitCode -eq 0) {
    $targets = if ($TargetFramework) { @($TargetFramework) } else { @('net6.0', 'net8.0') }
    Write-Host ''
    Write-Host '构建成功。产物：'
    foreach ($tfm in $targets) {
        $dll = Join-Path $repoRoot "artifacts/bin/$Configuration/$tfm/Emby.Plugin.OverviewTranslator.dll"
        if (Test-Path -LiteralPath $dll) {
            Write-Host "  $dll" -ForegroundColor Green
        } else {
            Write-Host "  未找到 $dll（该目标可能没有编译）" -ForegroundColor Yellow
        }
    }
    Write-Host ''
    Write-Host '安装：把对应 TFM 的 Emby.Plugin.OverviewTranslator.dll 放到 Emby 的'
    Write-Host '      <programdata>/plugins/ 目录，然后重启 Emby。'
}

exit $exitCode
