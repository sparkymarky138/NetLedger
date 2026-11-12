# NetLedger

A small Windows desktop app that shows **which programs use your network, how much, and when**. It also lets you **block or allow any program** with one click.

- Live download and upload graph for the whole PC (last 5 minutes)
- Per-app list: current speed, today's total and total since start, plus an Allowed/Blocked switch
- Live connections (TCP/UDP, local and remote address, remote host name, state)
- History by hour, day, week or month, for all apps or a single app (saved every minute)
- Alerts when a program uses the network for the first time, with **Keep allowed** / **Block** buttons
- Runs in the notification area (tray) and keeps monitoring when the window is closed
- Optional start at sign-in

Everything is one file, `NetLedger.exe`. It runs on **Windows 10 (version 1607 or later) and Windows 11, 64-bit**. You do not need to install .NET; the runtime is inside the exe.

---

## Run it

1. Copy `NetLedger.exe` anywhere, for example `C:\Tools\NetLedger\NetLedger.exe`.
2. Double-click it and accept the **UAC prompt**. NetLedger needs administrator rights because Windows only lets administrators read kernel network events and change firewall rules.
3. The window opens on **Overview**. Closing the window leaves NetLedger running in the tray. To stop it, right-click the tray icon and choose **Exit (stop monitoring)**.

To start it automatically, open **Settings** and turn on **Start NetLedger when I sign in**. This creates a Task Scheduler task named `NetLedger` that starts the exe in the tray with highest privileges, so there is no UAC prompt at each sign-in.

### Blocking a program

Go to **Apps & access** and click the green **Allowed** pill next to a program. It turns red (**Blocked**).

NetLedger then adds two Windows Firewall rules for that exe, one inbound and one outbound, and resets the program's open IPv4 connections so the block applies immediately. Click the pill again to allow the program; NetLedger deletes its rules for that exe.

- Rules are grouped under **NetLedger** in Windows Firewall (`wf.msc`), so you can see them there.
- Blocking only works while Windows Firewall is switched on. NetLedger shows a red banner if it is off for the active network.
- When you block something inside `C:\Windows`, NetLedger asks first. Blocking Windows components can break updates or name lookups.
- "System (Windows kernel)" and "Unknown or exited process" have no program file, so they cannot be blocked.

### Snooze (pause alerts and blocking for a while)

Right-click the tray icon, choose **Snooze alerts and blocking**, and pick **For 15 minutes**, **For 1 hour** or **Until I resume**. The same buttons are under **Settings → Snooze**.

While snoozed:
- new-app alerts are muted;
- NetLedger's firewall rules are **switched off, not deleted**, so blocked apps can use the network;
- usage is still recorded;
- an amber banner shows when the snooze ends, with a **Resume now** button. The tray menu also offers **Resume now**.

When the time is up, NetLedger switches the rules back on, resets the blocked apps' open connections and shows a "Snooze is over" notification. An app you block during a snooze gets its rule switched on when the snooze ends.

Blocking always comes back on:
- **Exit during a snooze:** NetLedger switches the rules back on before it closes.
- **PC crash or power cut during a snooze:** NetLedger switches them back on the next time it starts, unless the snooze time has not run out yet.

### Quick test that blocking works

Open a normal Command Prompt and run:

```bash
curl -o NUL https://www.example.com
```

In NetLedger, find **The curl executable** under Apps & access and block it. Run the same command again. It now fails with `curl: (7) Failed to connect`. Allow it again and the command works.

---

## Remove it

Removing NetLedger fully means deleting its firewall rules, its sign-in task and (optionally) its history.

1. Open Command Prompt **as administrator** in the folder that contains the exe, and run:

   ```bash
   NetLedger.exe --uninstall
   ```

   This:
   - closes a running NetLedger,
   - deletes **every firewall rule NetLedger created** (all rules in the `NetLedger` group),
   - deletes the `NetLedger` Task Scheduler task,
   - stops NetLedger's trace session if one was left behind,
   - and shows a summary.

   Add `--purge` to also delete the usage history and settings in `%LOCALAPPDATA%\NetLedger`:

   ```bash
   NetLedger.exe --uninstall --purge
   ```

2. Delete `NetLedger.exe`.

NetLedger installs nothing else: no service, no driver, no registry entries.

Other ways to remove only the firewall rules:
- In the app: **Settings → Remove all NetLedger firewall rules**.
- From an admin prompt: `NetLedger.exe --remove-rules`.
- Manually, from an admin PowerShell: `Remove-NetFirewallRule -Group NetLedger`.

### Command-line switches

| Switch | What it does |
|---|---|
| *(none)* | Opens the window and starts monitoring |
| `--minimized` | Starts in the tray only (used by the sign-in task) |
| `--uninstall [--purge] [--quiet]` | Full removal as described above |
| `--remove-rules [--quiet]` | Deletes all NetLedger firewall rules only |
| `--selftest [logfile]` | End-to-end check (see below); exit code 0 means everything passed |
| `--snapshot <folder>` | Diagnostic: runs for 10 s, saves each page as a PNG, then exits |

---

## Build it from source

You need Windows 10 or 11 (64-bit) and two free tools:

```bash
winget install Microsoft.DotNet.SDK.8
```

```bash
winget install Git.Git
```

Open a **new** terminal so both are on the PATH. Then get the code and build it:

```bash
git clone https://github.com/sparkymarky138/NetLedger.git
```

```bash
cd NetLedger
```

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

The finished program is `publish\NetLedger.exe`, the only file you need. Copy it to any Windows 10/11 PC and double-click it.

No Git? On the GitHub page click **Code → Download ZIP**, unzip it, open a terminal in that folder, and run the `build.ps1` line above.

The script runs:

```bash
dotnet publish src\NetLedger.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

and leaves a single `publish\NetLedger.exe` of about 70 MB. Most of that is the bundled .NET runtime.

`nuget.config` in the repo points at nuget.org, so restore works even on a PC with no NuGet sources configured. `tools\make-icon.ps1` regenerates the app icon.

### Self-test

From an admin prompt:

```bash
publish\NetLedger.exe --selftest selftest.log
```

It copies `curl.exe` to a temp folder and downloads 3 MB with that copy. It then checks:
1. the bytes are attributed to that curl copy;
2. after blocking it, the next download fails;
3. with the rules switched off as in a snooze, the download works, and after switching them back on it fails again;
4. after allowing it, downloads work again;
5. removing all rules leaves none behind;
6. the connection table can be read;
7. the SQLite history works.

It only touches firewall rules for its own curl copy.

---

## How it works

| Part | Implementation |
|---|---|
| Per-app bytes | A real-time ETW session (`NetLedger-Network`) subscribed to **Microsoft-Windows-Kernel-Network** (TCP/UDP send/receive, IPv4 and IPv6, with the owning process ID) and **Microsoft-Windows-Kernel-Process** (process starts, so short-lived programs such as curl are still named after they exit). It is a small direct advapi32 consumer, with no native helper DLLs. Loopback traffic is ignored. |
| Process → program | `QueryFullProcessImageName`, with the image path from the process-start event as a fallback. Programs are identified by full exe path. |
| Connections | `GetExtendedTcpTable` / `GetExtendedUdpTable`; reverse DNS lookups for remote addresses. |
| Block / allow | Windows Firewall COM API (`HNetCfg.FwPolicy2`). Each blocked exe gets an inbound and an outbound block rule in group `NetLedger`, for all profiles and protocols. Live IPv4 TCP connections are reset with `SetTcpEntry`. |
| History | SQLite at `%LOCALAPPDATA%\NetLedger\netledger.db`: per-app, per-minute sent/received bytes, kept for about 13 months. |
| UI | WPF (.NET 8) with a custom chart control and a WinForms tray icon. |

Project layout:

```
NetLedger/
  build.ps1              one-command build of the single exe
  nuget.config
  src/
    NetLedger.csproj, app.manifest (requireAdministrator)
    App.xaml(.cs)        start-up, command-line switches, single instance, tray wiring
    Core/                EtwNetworkMonitor, ProcessResolver, MonitorEngine, FirewallManager,
                         ConnectionTable, UsageStore, Autostart, SelfTest
    UI/                  MainWindow, TrafficChart, TrayIcon, Theme, row models
    Assets/              icon and logo
  tools/make-icon.ps1
```

## Licence

MIT. See [LICENSE](LICENSE).

### Limitations

- Monitoring runs only while NetLedger runs in a signed-in session. It does not install a Windows service, so nothing is recorded before you sign in or after you exit from the tray.
- All Windows services that share one `svchost.exe` appear together as "Host Process for Windows Services".
- Apps that first appear during a snooze get no alert later. They are still listed under Apps & access.
- "New app" alerts appear after the program's first traffic. NetLedger does not hold back that first connection. On the very first run, alerts stay quiet for two minutes while NetLedger learns what is normal on the PC.
- Blocking relies on Windows Firewall. If another security product has replaced Windows Firewall, NetLedger's rules may have no effect.
