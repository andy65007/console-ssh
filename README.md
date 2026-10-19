# Console SSH

一个 Windows 后台服务：远程用户通过 SSH 登录后，可以选择本机已经打开的经典控制台窗口（例如 `cmd.exe`），查看其屏幕并注入键盘输入。

## 文档

- [架构设计](docs/ARCHITECTURE.md)：进程/会话模型、Worker IPC、输入与渲染链路。
- [安全模型](docs/SECURITY.md)：信任边界、部署清单、磁盘数据和已知限制。

## 源码结构

```text
src/ConsoleSsh/
  Program.cs              命令入口
  ServiceCommand.cs       Windows 服务控制
  SessionLauncher.cs      Session 0 -> 用户会话
  AgentCommand.cs         OpenSSH 配置和 sshd 监督
  AttachCommand.cs        SSH 窗口选择与远端渲染
  WorkerCommand.cs        控制台附着 Worker 与 IPC
  InputTranslator.cs      终端字节/转义序列 -> 控制台输入
  ConsoleScreenReader.cs  控制台屏幕缓冲区读取
  NativeMethods.cs        Win32 P/Invoke
  launcher/               无系统 .NET 时的原生启动器
  openssh/                便携版 OpenSSH 与许可证
scripts/                   构建、安装、卸载
docs/                      架构与安全文档
```

## 工作方式

```text
SSH 客户端
    |
    v
便携版 OpenSSH（在交互式用户会话中运行）
    |
    +-- ForceCommand -> console-ssh attach
                           |
                           +-- 枚举 ConsoleWindowClass 窗口
                           +-- 为选中的 PID 启动 worker
                                  |
                                  +-- FreeConsole + AttachConsole
                                  +-- 读取屏幕缓冲区
                                  +-- WriteConsoleInput 注入键盘
```

Windows 服务本身运行在 Session 0，不能直接访问用户桌面，所以服务会把代理进程启动到当前活动控制台会话；OpenSSH 和附着进程都运行在该用户会话中。

## 支持范围

- 支持经典 Windows 控制台窗口：`cmd.exe`、Windows PowerShell、部分 REPL 等。
- 不支持 Windows Terminal 标签页。它们使用伪控制台（ConPTY），无法通过 `AttachConsole` 接入。请把“默认终端应用程序”改为“Windows 控制台宿主”，或者单独打开经典控制台窗口。
- 中等完整性进程不能附着到更高完整性的控制台。例如普通用户会话通常无法操作“以管理员身份运行”的 cmd 窗口。
- 服务跟随当前活动控制台会话（`WTSGetActiveConsoleSessionId`）；仅 RDP 登录而没有本地控制台会话时不启动代理。
- SSH 仅启用公钥认证，不提供密码认证。

## 构建

要求：

- Windows x64
- .NET 10 SDK
- 可选：MinGW-w64 `gcc`（重新编译原生启动器；仓库中已包含编译好的启动器）

```powershell
.\scripts\build.ps1 -DotnetExecutable "C:\path\to\dotnet.exe"
```

输出为 `dist\ConsoleSsh` 和 `dist\console-ssh-1.0.0-win-x64.zip`。发布目录包含：

- 应用本体
- 便携版 OpenSSH
- 最小 .NET 运行时
- `console-ssh-launcher.exe`，用于在没有系统级 .NET 运行时的情况下启动应用

## 安装

安装脚本会复制文件、注册名为 `ConsoleSsh` 的自动启动服务，并添加 TCP 端口防火墙规则。

```powershell
# 以管理员身份运行 PowerShell
.\scripts\install.ps1 -Port 2222
```

如果使用发布压缩包，解压后在应用目录中直接运行 `.\install.ps1 -Port 2222`。

也可以指定安装目录：

```powershell
.\scripts\install.ps1 -InstallDirectory "C:\ConsoleSsh" -Port 2222
```

安装后在当前交互用户的以下文件中放入远程客户端的公钥：

```text
%USERPROFILE%\.ssh\authorized_keys
```

示例：

```powershell
ssh-keygen -t ed25519
Get-Content "$env:USERPROFILE\.ssh\id_ed25519.pub" |
    Add-Content "$env:USERPROFILE\.ssh\authorized_keys"
```

如果服务已经启动后才加入公钥，不需要重启服务；下一次 SSH 登录会读取新文件。

## 连接

```powershell
ssh -tt -p 2222 <当前Windows用户名>@<本机IP>
```

建议始终加 `-tt`。它让 SSH 客户端分配 PTY 并进入交互模式，目标控制台可以逐字符回显。没有 PTY 时，是否逐字符发送和本地回显取决于客户端的终端模式；程序仍会把收到的输入实时转发，并在底部状态栏显示 `输入:`/`已提交:` 内容。

登录后会显示可附着的经典控制台窗口列表：

- `↑/↓` 或 `J/K` 选择
- `Enter` 连接
- 输入编号后按 `Enter` 连接
- `R` 刷新
- `Q` 退出

进入控制台后，键盘输入会原样转发。按 `Ctrl+]` 返回窗口列表。

如果客户端没有申请 PTY（例如 `ssh -T`），输入仍会按字节实时转发到目标控制台，因此目标 `cmd.exe` 会在你输入时立即回显字符。`Ctrl+]` 可返回窗口列表；需要完整的控制键、方向键和终端尺寸行为时，建议使用 `ssh -tt`。

## 配置与日志

- 全局配置：`C:\ProgramData\ConsoleSsh\config.json`
- 服务日志：`C:\ProgramData\ConsoleSsh\logs\service.log`
- 用户代理日志：`%LOCALAPPDATA%\ConsoleSsh\logs\`
- SSH 主机密钥：`%LOCALAPPDATA%\ConsoleSsh\hostkeys\`
- 生成的 sshd 配置：`%LOCALAPPDATA%\ConsoleSsh\runtime\sshd_config`

默认监听 `0.0.0.0:2222`。防火墙规则只对 `private` 和 `domain` 配置文件开放；如确实需要公网访问，应自行评估风险并配置网络安全组或路由器端口映射。

## 调试

前台启动代理：

```powershell
.\dist\ConsoleSsh\console-ssh-launcher.exe run --port 2222 --bind 127.0.0.1
```

使用自定义测试目录和授权公钥：

```powershell
.\dist\ConsoleSsh\console-ssh-launcher.exe run `
  --port 2222 `
  --bind 127.0.0.1 `
  --data-dir "C:\Temp\ConsoleSshTest" `
  --authorized-keys "C:\Temp\ConsoleSshTest\authorized_keys"
```

服务状态：

```powershell
sc.exe query ConsoleSsh
```

## 安全说明

- 服务只允许公钥认证，禁用密码、TCP 转发、X11 转发、代理转发和隧道。
- `sshd` 以当前交互用户身份运行，而不是 LocalSystem，因此远程会话只具有该用户的权限。
- 远程端能够读取和操作该用户可访问的经典控制台窗口，请只在可信网络和可信密钥范围内启用。
- 生产环境应使用受保护的私钥、限制防火墙来源地址，并定期更新随包附带的 OpenSSH。

随包 OpenSSH 的许可证和声明位于 `openssh\LICENSE.txt` 和 `openssh\NOTICE.txt`。
