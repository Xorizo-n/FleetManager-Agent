# Installation

The enrollment token is required for automatic registration. Create it through `POST /agent/enrollment-tokens` as an administrator, then run PowerShell as Administrator:

```powershell
.\install.ps1 -ServerUrl https://fleet.example -EnrollmentToken '<raw-token>' -SshLogin 'RTF\s.u.mirzagitov'
```

`SshLogin` is optional. If omitted, the installer uses the current interactive
domain account (`DOMAIN\user`) and sends that login during agent registration.
The same value is also sent with each heartbeat, so an existing registration can
be repaired by updating `SshLogin` in `%ProgramData%\FleetManagerAgent\agent.json`
and restarting the service.

The installer writes the token to `%ProgramData%\FleetManagerAgent\agent.json`, registers the service, quotes the tray path in the HKLM Run key, and starts the tray in the interactive user's Explorer context. If the current session has no Explorer process, the tray starts after the next logon.

After registration the service installs the server-generated public SSH key in `C:\ProgramData\ssh\administrators_authorized_keys`. The matching private key is kept by Fleet Manager in the encrypted Key Store. The service retries this installation on every synchronization and reports `icacls.exe` errors instead of silently continuing, so Ansible cannot start using a key that was never accepted by Windows OpenSSH. `uninstall.ps1` stops both the service and the tray process, calls the server cleanup endpoint, removes the local authorized key, and then deletes the service and local data. If the server/API cleanup fails, the script exits with the HTTP error and keeps the configuration so the operation can be retried. For an intentional local-only removal, run `.\uninstall.ps1 -SkipRemoteCleanup` from an elevated PowerShell session.
