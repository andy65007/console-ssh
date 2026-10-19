# Security model

## Trust boundary

Anyone who authenticates with an allowed public key can read and type into classic
console windows owned by the interactive Windows user. Treat this as interactive
remote control of that user session, not as a read-only monitoring tool.

## Controls implemented

- Public-key authentication only; password and keyboard-interactive auth are off.
- `AllowUsers` is generated for the active interactive user.
- TCP forwarding, stream-local forwarding, agent forwarding, X11, and tunnels are
  disabled.
- The agent and sshd run as the interactive user, not LocalSystem.
- The Windows service is used only to start the agent in the correct session.
- SSH output is limited to the selected classic console viewport.
- `authorized_keys` is read from the active user's profile by default.

## Deployment checklist

- Use a dedicated, passphrase-protected client key.
- Restrict the firewall source address to the management network.
- Do not expose the port directly to the public Internet.
- Keep the bundled OpenSSH version current.
- Review `C:\ProgramData\ConsoleSsh\logs` and `%LOCALAPPDATA%\ConsoleSsh\logs`.
- Install to a path without spaces when possible. The launcher also attempts an
  NTFS 8.3 alias for compatibility.

## Known limitations

- Windows Terminal tabs are not attachable because they use ConPTY.
- A medium-integrity worker cannot attach to an elevated console window.
- Only the active physical console session is targeted by the service.
- No-PTY SSH clients control their own local terminal mode. If a client does not
  send bytes until Enter, the server cannot echo before Enter. Use `ssh -tt`.

## Data stored on disk

| Path | Purpose |
| --- | --- |
| `C:\ProgramData\ConsoleSsh\config.json` | Port, bind address, tuning |
| `C:\ProgramData\ConsoleSsh\logs` | Service log |
| `%LOCALAPPDATA%\ConsoleSsh\hostkeys` | Per-user SSH host key |
| `%LOCALAPPDATA%\ConsoleSsh\runtime` | Generated sshd configuration and PID file |
| `%USERPROFILE%\.ssh\authorized_keys` | Allowed client public keys |

No passwords or private client keys are stored by the application.
