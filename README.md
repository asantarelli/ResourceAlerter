# ResourceAlerter

Windows service that watches a server's health — CPU, RAM, CPU temperature, PSU rail
voltages, disk space, and network microcuts — and e-mails an alert when something goes out
of range, with no external monitoring infrastructure (no Zabbix/PRTG/Docker/Linux). Built
with .NET 8 (`Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Hosting.WindowsServices`)
and [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
for sensors.

## How it behaves

- One central polling loop (`Worker`, default every 10s) runs each monitor and feeds the
  result into that monitor's `AlertStateTracker`.
- Anti-spam state machine per monitored subject (e.g. per disk drive, per PSU rail):
  1. Out of range must be **sustained** for a configurable window (default 60s) before it
     counts as a real event — a single spike does not trigger a mail.
  2. Once confirmed, an **initial alert mail** is sent.
  3. While still out of range, a **reminder mail** is sent every N minutes (default 20),
     not every poll cycle.
  4. Once back in range and **sustained** there for a window (default 60s), a **resolved**
     mail is sent with the total event duration.
- Every mail includes machine name, monitor, detected value, threshold, and timing.
- A "service started on `<machine>` at `<time>`" mail is sent on every startup — an indirect
  signal that the box rebooted. It also lists exactly what's being actively monitored (with
  each threshold) and what got skipped on this particular machine, so you know your actual
  sensor coverage without having to dig through logs.
- Everything is also written to a local rotating log file (`logs/`) so you can diagnose
  without depending on mail delivery.
- If a temperature/voltage sensor isn't exposed by the hardware (or a PSU rail's sensor name
  doesn't match, see `SensorNameOverrides` below), it is **silently ignored**: logged once
  locally for diagnostics, no alert mail ever, and it shows up under "not monitored" in the
  startup mail. Checking still happens every cycle in case the sensor becomes available later
  (e.g. after installing the service elevated), which self-heals with no special handling.

## Project layout

```
src/ResourceAlerter/
  Program.cs                    Host/DI wiring, Windows Service registration
  Worker.cs                     The central polling loop
  Configuration/                Strongly-typed appsettings.json sections
  Monitors/                     One IHealthMonitor implementation per check
  Alerting/                     AlertStateTracker (anti-spam) + SMTP sender
  Logging/                      Rotating file logger provider
  appsettings.json              Example/default configuration
scripts/
  install.ps1 / uninstall.ps1   Register/remove the Windows service
```

## Build

Requires the .NET 8 SDK (or later; the project targets `net8.0-windows`).

```powershell
cd src/ResourceAlerter
dotnet build
```

## Publish for deployment

The same build gets copied to every monitored server, so publish once and copy the output
folder around. Self-contained + single-file is recommended so servers don't need the .NET
runtime pre-installed (the published `ResourceAlerter.exe` is ~70 MB, which is fine for an
internal file copy):

```powershell
cd src/ResourceAlerter
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\..\publish\ResourceAlerter
```

If every target server already has the matching .NET runtime installed, a smaller
framework-dependent publish also works:

```powershell
dotnet publish -c Release -o ..\..\publish\ResourceAlerter-fx
```

Copy the resulting folder to each server (e.g. `C:\Apps\ResourceAlerter`).

## Configure

Edit `appsettings.json` next to `ResourceAlerter.exe`, or create
`appsettings.<MACHINE-NAME>.json` in the same folder to override just a few values per
server without touching the shared base file (both are loaded; the machine-specific one
wins). At minimum, set:

- `Smtp.Host` / `Smtp.Port` / `Smtp.UseSsl` / `Smtp.RequiresAuthentication` /
  `Smtp.Username` / `Smtp.Password` / `Smtp.FromAddress` — your relay.
- `Smtp.Recipients` — who gets the alerts.
- `Monitoring.Disk.Drives` — add extra drive letters if the Clarion DB lives on `D:` etc.
  (leave empty to only watch the system drive).
- `Monitoring.Network.TargetHost` — leave `null` to auto-detect the default gateway
  (falls back to `Monitoring.Network.FallbackHost`, default `8.8.8.8`), or pin an explicit
  host.

All thresholds, sustained/recovery windows, reminder interval, and polling interval are
configurable per monitor — see the comments (`"//"` keys) in `appsettings.json`.

## Install via MSI (recommended for rolling out to multiple servers)

`installer/` contains a WiX v5 project that builds a proper `.msi` — double-click to install,
or script it silently across the fleet. It registers the service (LocalSystem, Automatic
delayed-start) for you; there's no separate script to run afterward.

Build it (on a dev machine with the .NET SDK):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2
cd installer
.\build-installer.ps1 -Version 1.0.0
```

This publishes the app and produces `installer\bin\ResourceAlerterSetup-<version>.msi`.
**Do not install WiX v7+** for this project — v7 requires accepting an "Open Source
Maintenance Fee" EULA, which this project intentionally avoids by pinning v5.

Copy the `.msi` to each server and, elevated:

```powershell
msiexec /i ResourceAlerterSetup-1.0.0.msi          # interactive
msiexec /i ResourceAlerterSetup-1.0.0.msi /quiet    # silent, for scripted rollout
```

Installs to `C:\Program Files\ResourceAlerter` (fixed path, not user-selectable, so the
whole fleet stays consistent). **Upgrades are safe**: re-running a newer version's installer
over an existing install replaces the binaries but never overwrites an already-configured
`appsettings.json` — only a fresh install writes the default one. Bump `-Version` for each
release you build (the `UpgradeCode` in `installer\Product.wxs` stays fixed across versions
so Windows Installer recognizes it as an upgrade, not a conflicting product).

Uninstall like any other Windows app: `msiexec /x ResourceAlerterSetup-1.0.0.msi`, or via
Settings → Apps → ResourceAlerter → Uninstall.

## Install as a Windows service (alternative: scripts, no MSI)

From an elevated PowerShell prompt, on the target server, in the published folder:

```powershell
cd C:\Apps\ResourceAlerter
.\scripts\install.ps1
```

(or `.\install.ps1 -PublishDir C:\Apps\ResourceAlerter` if running the script from
somewhere else). This registers the service as **LocalSystem** — required so
LibreHardwareMonitor's driver can access hardware sensors — with **Automatic (Delayed
Start)**, then starts it.

Verify:

```powershell
Get-Service ResourceAlerter
```

and confirm the "service started" e-mail arrives, and `logs\resourcealerter-*.log` is being
written.

Uninstall:

```powershell
.\scripts\uninstall.ps1
```

Stops and removes the service. The application folder, config, and logs are left in place.

## Viewing logs

Plain text, one file per day at `logs\resourcealerter-yyyyMMdd.log` next to the exe
(rolls to `_1`, `_2`, ... within a day if it exceeds `FileLogging.MaxFileSizeMb`). Files
older than `FileLogging.RetentionDays` (default 30) are pruned automatically. Configure
verbosity under the standard `Logging.LogLevel` section.

## Diagnosing sensors (`--list-sensors`)

Rail/sensor names reported by LibreHardwareMonitor vary a lot between Super I/O chips, so
`VoltageMonitor`'s automatic matcher (comparing nominal-voltage digits and standby-ness)
won't always find a rail. Run this **elevated** (as Administrator, or it won't see much) on
the actual server to list every temperature/voltage sensor LibreHardwareMonitor can see:

```powershell
.\ResourceAlerter.exe --list-sensors
```

If a configured rail doesn't auto-match, pin its exact sensor name via
`Monitoring.Voltage.SensorNameOverrides` in that machine's `appsettings.<MACHINE-NAME>.json`,
e.g. on a board with a Nuvoton NCT6687D Super I/O chip, the 3.3V rail shows up as `AVCC3`
rather than anything containing "3.3":

```json
{
  "Monitoring": {
    "Voltage": {
      "SensorNameOverrides": {
        "+3.3V": "AVCC3"
      }
    }
  }
}
```

If a rail truly isn't exposed at all by the chip (e.g. many Nuvoton chips have no +5V
standby sensor, only a ~3.3V "VSB"/"AVSB" standby rail that isn't the same thing), there's
nothing to configure — it's silently skipped (see "How it behaves" above) and shows up
under "not monitored" in the startup mail.

## Testing before you trust it on a real server

This was built and tested end-to-end on a real Windows box (`AESANTARELLI`, Intel i5-11600K,
Nuvoton NCT6687D Super I/O), both un-elevated (console) and elevated, confirming:

- The central polling loop starts, logs to console + file, and the CPU/Memory/Disk/Network
  monitors read real values without throwing.
- SMTP retry/backoff and graceful give-up work end-to-end (verified against an
  intentionally invalid host: 3 attempts with growing backoff, then a logged error — the
  service keeps running).
- The "sensor unavailable" path is silent by design: no mail, just a single local log line,
  and the startup mail correctly lists it under "not monitored" (see "How it behaves" above).
- `--list-sensors` and `SensorNameOverrides` were used to find and fix a real mismatch: this
  board's +12V/+5V matched automatically, but +3.3V reported as `AVCC3` (not auto-matched
  until pinned via override) and +5V standby genuinely isn't exposed by this chip at all.

Still to verify per-machine before rollout (do this on **each** server, hardware varies):

1. **Run elevated / install as the service** and use `--list-sensors` to confirm
   LibreHardwareMonitor can read CPU temperature and PSU voltage sensors on that specific
   motherboard — not every board exposes all of them, especially voltage rails, which depend
   on the Super I/O chip. A rail/sensor that's genuinely absent is a hardware/driver
   limitation, not a bug — there's no generic workaround, just silent skip.
2. **SMTP against your real relay**: point `Smtp.*` at your actual server/relay and confirm
   the startup mail arrives — check its body lists the sensors you expect under "Actively
   monitoring" and nothing important under "Not monitored".
3. **Sustained-window / reminder / resolved flow under real load**: generate CPU/memory
   load for over a minute (e.g. `Start-Job { 1..8 | ForEach-Object { Start-Job { while($true){} } } }` or any stress tool) and confirm: no mail on a
   short spike, one alert mail once sustained past the window, a reminder if the load runs
   longer than the reminder interval, and a resolved mail with duration once load stops.
4. **Reboot test**: confirm the service is `Automatic (Delayed Start)` and comes back up
   and sends its startup mail after a real reboot.

## Troubleshooting

- **No sensors ever, on every machine**: the service isn't running as LocalSystem (check
  `sc.exe qc ResourceAlerter`), or LibreHardwareMonitor's driver failed to load — check
  `logs\` for "Failed to open LibreHardwareMonitor" at startup.
- **Mail never arrives, but log shows "sent"**: check spam/relay filtering; the log only
  confirms `SmtpClient` accepted the message for delivery, not that it reached the inbox.
- **Mail never arrives and log shows repeated retries**: check `Smtp.Host`/`Port`/`UseSsl`
  and that the server can reach the relay (firewall, DNS).
- **A monitor is too noisy**: widen `SustainedWindowSeconds`/`RecoveryWindowSeconds` or the
  threshold for that monitor in `appsettings.json`; increase `ReminderIntervalMinutes` to
  reduce reminder frequency.
- **Disable a monitor entirely** (e.g. this hardware has no PSU voltage sensors at all and
  you don't even want it listed as "not monitored" in the startup mail): set
  `Monitoring.<Name>.Enabled` to `false`. Note missing sensors already send no mail on their
  own — this is only for suppressing the informational log line and startup-mail listing.
