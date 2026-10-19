param(
    [string]$SourceDirectory = "",
    [string]$InstallDirectory = "$env:ProgramFiles\ConsoleSsh",
    [int]$Port = 2222
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "console-ssh.exe")) {
        $SourceDirectory = $PSScriptRoot
    }
    else {
        $SourceDirectory = Join-Path $repoRoot "dist\ConsoleSsh"
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请使用「以管理员身份运行」的 PowerShell 执行安装。"
}

if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory "console-ssh.exe"))) {
    throw "发布目录无效：$SourceDirectory"
}

$existingService = Get-Service -Name ConsoleSsh -ErrorAction SilentlyContinue
if ($existingService -and $existingService.Status -ne "Stopped") {
    Stop-Service -Name ConsoleSsh -Force -ErrorAction SilentlyContinue
    try {
        $existingService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(15))
    }
    catch {
        throw "现有 ConsoleSsh 服务未能在 15 秒内停止，请手动停止后重试。"
    }
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $SourceDirectory -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallDirectory -Recurse -Force
}

$launcher = Join-Path $InstallDirectory "console-ssh-launcher.exe"
if (-not (Test-Path -LiteralPath $launcher)) {
    throw "安装目录缺少 console-ssh-launcher.exe。"
}

& $launcher install --port $Port
if ($LASTEXITCODE -ne 0) {
    throw "服务安装命令失败，退出码 $LASTEXITCODE。"
}

$authorizedKeys = Join-Path $env:USERPROFILE ".ssh\authorized_keys"
if (-not (Test-Path -LiteralPath $authorizedKeys) -or (Get-Item -LiteralPath $authorizedKeys).Length -eq 0) {
    Write-Warning "当前用户的 authorized_keys 为空。远程登录前，请把客户端公钥写入：$authorizedKeys"
}

Write-Host ""
Write-Host "安装完成。"
Write-Host "用户：$env:USERNAME"
Write-Host "端口：$Port"
Write-Host "连接示例：ssh -p $Port $env:USERNAME@<本机IP>"
Write-Host "注意：只支持经典控制台窗口；Windows Terminal 标签页无法附着。"
