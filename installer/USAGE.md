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

sshd listens on **port 22** and answers only the Fleet Manager server:

- the `FleetManager-Agent-SSH` firewall rule allows TCP 22 from the server's
  address only;
- every other enabled inbound allow rule for port 22 or `sshd.exe` (such as
  `OpenSSH-Server-In-TCP`, open to everyone) is disabled — Windows admits a
  connection if any allow rule matches, so leaving one would void the
  restriction;
- `sshd_config` gets a `Match Address *,!<server>` block with `DenyUsers *`,
  which still applies where Group Policy overrides local firewall rules;
- sshd moved to port 5022 by earlier installer builds is moved back to 22;
- PowerShell becomes the OpenSSH default shell (the server uses
  `ansible_shell_type=powershell`).

The server's address is taken from the `ServerUrl` host. Pass a comma-separated
IPv4/CIDR list to override it or to admit an admin subnet as well; the value is
kept in `agent.json` for later upgrades:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<raw-token>' /SshSourceAddress=10.40.240.154,10.40.0.0/24
```

sshd is restarted at the end of the install; when the installer runs inside an
SSH session (a remote update), the restart is deferred by two minutes through a
self-removing scheduled task so the update's own session is not cut.

## Provisioning a PC deployed from an image

AutoDomain installs the agent right after the domain join, before the reboot
that applies the new computer name:

```powershell
FleetManagerAgent-Setup.exe /VERYSILENT /ServerUrl=https://fleet.example /EnrollmentToken='<raw-token>' /SshLogin='DOMAIN\user' /DeferStart=1 /ResetSshHostKeys=1
```

- `/DeferStart=1` creates the service with delayed auto-start but does not
  start it (nor the tray), so the agent registers after the reboot, under the
  new name.
- `/ResetSshHostKeys=1` deletes the SSH host keys inherited from the reference
  machine; sshd generates this PC's own. Refused inside an SSH session.
- A fresh install (no `AgentToken` in `agent.json`) always drops a leftover
  `machine-id`, so clones of one image never share it.

The service itself also waits with registration while a computer rename is
pending a reboot, and retries a missing registration every minute.

The installer writes the token to `%ProgramData%\FleetManagerAgent\agent.json`, registers the service, quotes the tray path in the HKLM Run key, and starts the tray in the interactive user's Explorer context. If the current session has no Explorer process, the tray starts after the next logon.

After registration the service installs the server-generated public SSH key in `C:\ProgramData\ssh\administrators_authorized_keys`. The matching private key is kept by Fleet Manager in the encrypted Key Store. The service retries this installation on every synchronization and reports `icacls.exe` errors instead of silently continuing, so Ansible cannot start using a key that was never accepted by Windows OpenSSH. `uninstall.ps1` stops both the service and the tray process, calls the server cleanup endpoint, removes the local authorized key, and then deletes the service and local data. If the server/API cleanup fails, the script exits with the HTTP error and keeps the configuration so the operation can be retried. For an intentional local-only removal, run `.\uninstall.ps1 -SkipRemoteCleanup` from an elevated PowerShell session.
