param(
    [string]$InstallDirectory = "$env:ProgramFiles\ConsoleSsh"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请使用「以管理员身份运行」的 PowerShell 执行卸载。"
}

$launcher = Join-Path $InstallDirectory "console-ssh-launcher.exe"
if (Test-Path -LiteralPath $launcher) {
    & $launcher uninstall
}

Write-Host "服务已停止并删除。安装文件保留在：$InstallDirectory"
Write-Host "ProgramData 中的配置和日志保留在：$env:ProgramData\ConsoleSsh"
