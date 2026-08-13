# Installation

The enrollment token is required for automatic registration. Create it through `POST /agent/enrollment-tokens` as an administrator, then run the packaged installer as Administrator:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<raw-token>' /SshLogin='RTF\s.u.mirzagitov'
```

`SshLogin` is optional. If omitted, the installer uses the current interactive
domain account (`DOMAIN\user`) and sends that login during agent registration.
The same value is also sent with each heartbeat, so an existing registration can
be repaired by updating `SshLogin` in `%ProgramData%\FleetManagerAgent\agent.json`
and restarting the service.

There is no standalone `install.ps1` — all install steps (writing `agent.json`,
OpenSSH Server, firewall, the Windows service, the tray autorun key) are
generated and run from `installer\FleetManagerAgent.iss` (`CurStepChanged`)
when the EXE runs. Build it with `build-installer.ps1`.

## SSH port policy

By default the installer configures sshd to listen **only on port 5022** (the
Ansible management port) and removes any pre-existing inbound firewall rule
that allows port 22 — including the rule the OpenSSH Server Windows capability
creates for itself. Pass `/AllowPort22=1` to skip that check and leave
whatever port-22 firewall state already exists on the host untouched:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<raw-token>' /AllowPort22=1
```

The installer writes the token to `%ProgramData%\FleetManagerAgent\agent.json`, registers the service, quotes the tray path in the HKLM Run key, and starts the tray in the interactive user's Explorer context. If the current session has no Explorer process, the tray starts after the next logon.

After registration the service installs the server-generated public SSH key in `C:\ProgramData\ssh\administrators_authorized_keys`. The matching private key is kept by Fleet Manager in the encrypted Key Store. The service retries this installation on every synchronization and reports `icacls.exe` errors instead of silently continuing, so Ansible cannot start using a key that was never accepted by Windows OpenSSH. `uninstall.ps1` stops both the service and the tray process, calls the server cleanup endpoint, removes the local authorized key, and then deletes the service and local data. If the server/API cleanup fails, the script exits with the HTTP error and keeps the configuration so the operation can be retried. For an intentional local-only removal, run `.\uninstall.ps1 -SkipRemoteCleanup` from an elevated PowerShell session.
