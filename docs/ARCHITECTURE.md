# Architecture

## Process and session model

Windows services run in Session 0 and cannot see user desktop windows from the
active console session. Console SSH therefore uses a small process hierarchy:

```text
Session 0
  console-ssh-launcher.exe service
    console-ssh.exe service
      |- reads ProgramData configuration
      |- finds WTSGetActiveConsoleSessionId()
      `- CreateProcessAsUser()
           |
           v
Session N (interactive user)
  console-ssh-launcher.exe agent
    console-ssh.exe agent
      |- manages host key / sshd_config
      |- starts bundled sshd.exe
      `- sshd ForceCommand
           |
           v
       console-ssh.exe attach
         |- enumerates ConsoleWindowClass windows
         |- renders the selection menu over SSH
         `- starts one worker per selected window
              |
              v
          console-ssh.exe worker --pid <pid>
            |- FreeConsole()
            |- AttachConsole(pid)
            |- ReadConsoleOutputW()
            `- WriteConsoleInputW()
```

The service keeps one agent for the active physical console session. Fast user
switching changes the session ID; the service stops the old job and starts a new
agent in the new session.

## Why a separate worker is required

A Windows process can be attached to only one console. The SSH endpoint may itself
have a ConPTY/console supplied by `ssh-shellhost.exe`. Calling `AttachConsole` in
that process would detach it from the SSH terminal. The worker is launched without
inheriting that console, then attaches to the selected target PID while its
standard input/output remain redirected pipes.

## Worker protocol

The worker and SSH endpoint exchange little-endian frames:

| Direction | Frame | Payload |
| --- | --- | --- |
| worker -> parent | `0x01` snapshot | width, height, cursor, rows, attributes |
| worker -> parent | `0x02` error | UTF-8 string |
| worker -> parent | `0x03` exit | none |
| parent -> worker | `'I'` | byte length + raw terminal input |
| parent -> worker | `'Q'` | none |

Snapshots contain only the visible viewport. The parent renderer compares rows and
rewrites changed rows with ANSI cursor addressing. This keeps bandwidth low while
preserving colors and cursor position.

## Input path

1. SSH terminal bytes arrive at the attach endpoint.
2. The endpoint forwards them immediately to the worker and updates a local
   `输入:`/`已提交:` status preview.
3. `InputTranslator` decodes UTF-8 and translates common CSI/SS3 sequences
   (`arrows`, Home/End, Delete, function keys) to virtual-key events.
4. Printable characters become `KEY_EVENT` records with `UnicodeChar`.
5. Ctrl+C uses `GenerateConsoleCtrlEvent` because writing a Ctrl+C key record does
   not reliably produce a console control event.

Forwarding input immediately is important for `ssh -T` clients: the target console
provides the visible echo instead of waiting for a complete line.

## OpenSSH configuration

The agent generates `sshd_config` on every start. Authentication is public-key only;
TCP forwarding, agent forwarding, X11, tunnels, and password login are disabled.

`ForceCommand` uses the portable launcher and prefers an 8.3 path when the install
directory contains spaces. Some Windows OpenSSH PTY code paths lose the normal
quoting around the first executable token.

## Supported consoles

Classic `ConsoleWindowClass` windows expose a real console screen buffer and input
buffer. Windows Terminal tabs use ConPTY and do not expose the same attachable
console objects, so they are listed only as a compatibility warning.
