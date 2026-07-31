# Neolink.NET Desktop (Windows)

> The same web UI, in a window that is always there. It sits in the system
> tray, starts with Windows, and raises real Windows notifications when your
> cameras see something — including while it is minimised and while you are on
> some other page.

Download the MSI from the [releases page](https://github.com/borexola/neolink.net/releases),
run it, and point it at your server. It is a **client**: the server keeps
running wherever it already runs (Docker, the Home Assistant add-on, a NAS,
bare metal). Nothing about your server setup changes.

### "Windows protected your PC"

The installer is **not code-signed**, so Windows has no publisher to check it
against and warns twice: SmartScreen's blue *"Windows protected your PC"*
screen, then a UAC prompt that says *Publisher: Unknown*. Neither means
anything was found wrong with the file — only that it is unrecognised.

To install anyway: on the blue screen click **More info** → **Run anyway**,
then accept the UAC prompt. You can also clear the download mark first —
right-click the MSI → **Properties** → tick **Unblock** → OK, or:

```powershell
Unblock-File .\Neolink.NET.Desktop-*.msi
```

If you would rather verify the download than trust it, every release MSI is
built in public by GitHub Actions from the tagged source — the run that
produced it is linked from the release, and you can rebuild it yourself with
`installer/build-msi.ps1`.

## Why not just the browser?

The web UI is already installable as a PWA, and for watching cameras that is
enough. Two things a browser tab cannot do:

- **Alert you when you are not looking at it.** The web UI's notifications ride
  a poll on its dashboard page. Navigate to Timeline and they stop; close the
  tab and they stop. The desktop app runs its own alert connection that does not
  care what the window is showing, or whether the window exists.
- **Notify at all over plain http.** The browser `Notification` API only exists
  in a secure context, so a LAN server on `http://` gets no browser alerts ever.
  The desktop app has no such restriction.

Everything else is identical, and stays identical: the window renders the live
UI from your server, so a feature that ships on the server appears here the same
day, with no separate app update.

## Setting it up

On first run it asks for:

- **Server address** — what you type in a browser: `10.1.0.60:8655`,
  `neolink.lan`, `https://cams.example.com`. A bare address is assumed to be
  `http://`.
- **Username and password** — leave blank if your server has no accounts. The
  password is stored encrypted with your Windows account (DPAPI): unreadable by
  other users on the PC, and useless if the file is copied elsewhere.
- **Accept an untrusted TLS certificate** — only tick this for a self-signed
  server on your own network. It turns off the check that would catch someone
  impersonating it.

It will not accept the dialog until the connection actually works, so you never
end up with a tray icon quietly talking to nothing.

## The tray

Closing the window hides it — the app keeps running and keeps alerting. Quit is
on the tray menu, which also has:

- **Notifications on this PC** — a quick mute for this machine
- **Notification settings** — the full panel, below
- **Start with Windows** — see below
- **Pause video when hidden** — on by default: while the window sits in the
  tray or minimised, live streams stop entirely (no bandwidth, no decoding)
  and resume the moment the window shows. Notifications are unaffected — the
  shell watches for events on its own, outside the page. Turn it off to keep
  streams warm for an instant picture on open.
- **Server connection** — change server or account
- **Reload** (F5), **Full reload** (Ctrl+F5), **Quit**

Inside the window, F5 / Ctrl+R reload the page the browser way. Ctrl+F5 (also
Shift+F5 or Ctrl+Shift+R) is a full reload: it unregisters the web UI's service
worker, drops its caches, and refetches everything from the server bypassing
the HTTP cache — the way out when a server update leaves the app shell showing
a stale UI.

## Notifications

The settings window splits into two halves, and which half a setting is in
matters:

**Your account** (saved on the server, shared with the web UI — change it here
and the browser agrees, and the other way round):

- which cameras alert, and for which detections (person, vehicle, animal,
  package, doorbell, crying, line-crossing, intrusion, loitering, motion)
- per-camera **offline** alerts
- server alerts: storage full, server overloaded, recording write failures
- the repeat cooldown

**This PC** (never leaves the machine):

- master switch for notifications here
- **quiet hours**, with a choice about whether they also silence camera and
  server faults — off by default, because a disk filling up at 3am is worth
  waking for
- sound, event thumbnail, whether clicking opens the event
- how often to check the server (default every 10s)

**Show a test notification** proves the path end to end. There is also a
command-line version for when nothing is arriving:

```bash
"C:\Program Files\Neolink.NET Desktop\Neolink.Desktop.exe" --test-notification
```

It says whether Windows accepted the notification. If it did and you saw
nothing, the problem is Windows' own settings — Focus assist / Do not disturb,
or the app switched off under Settings → System → Notifications.

### When a notification doesn't appear

The app keeps a decision log at `%LOCALAPPDATA%\Neolink.NET\desktop.log`: one
line per new event saying whether it alerted or why not (labels not selected
for that camera, older than 3 minutes on arrival, cooldown, alerts off), plus
quiet-hours suppressions, web-UI duplicates, connection state changes and
toast-to-balloon fallbacks. If the log says an alert was shown but nothing
appeared on screen, Windows swallowed it — check Focus assist / Do not
disturb (fullscreen games turn it on automatically) and the Action Center;
`--test-notification` reports whether Windows accepts toasts at all.

### Toasts vs tray balloons

Windows only grants an unpackaged app rich toasts (thumbnail, Action Center
history) if a Start Menu shortcut carrying its AppUserModelID exists. The
installer creates one, so an installed copy gets real toasts. A copy run
straight from a build folder creates its own on first launch, and falls back to
tray balloons if that fails. The notification settings window says which one is
in use.

### No duplicates

Both the shell and the web UI inside it can decide to raise the same alert. They
tag notifications with the event id, and the shell collapses matching tags, so
one event notifies once.

### Clicking a notification

Opens the app on that event — from the live banner, from the Action Center
sidebar hours later, and even if the app was quit in between (the click starts
it). This works through a per-user `neolink-desktop:` link protocol the app
registers and keeps repaired automatically; no setup needed. "Clicking opens
the event" can be turned off in Notification settings.

## Start with Windows

The toggle writes a per-user `Run` entry — no administrator rights, no scheduled
task, and it starts minimised to the tray. The entry is rewritten on every
launch, so an upgrade that moves the executable cannot leave it pointing at a
file that no longer exists. After an uninstall a leftover entry points at
nothing and Windows skips it silently; turn the toggle off before uninstalling
if you want the registry spotless.

## Installing and upgrading

The MSI is per-machine and self-contained: it carries the .NET runtime, so
there is no prerequisite to chase. It needs the **Microsoft Edge WebView2
Runtime**, which ships with Windows 11 and reaches Windows 10 through Edge
updates; if it is somehow missing, the app says so and names it.

Installing a newer version over an older one removes the old one first — there
is never a second entry in Add/Remove Programs. Because this is a tray app that
starts with Windows, it is usually **running** during an upgrade, so the
installer shuts it down first: Windows' Restart Manager asks it to exit the way
a logoff would (which it honours, unlike the tray's own ✕), and anything that
will not go is terminated. No reboot, no "files in use".

Your settings live in `%APPDATA%\Neolink.NET\` and are left alone by both
upgrade and uninstall.

## Building it yourself

```bash
dotnet build src/Neolink.Desktop/Neolink.Desktop.csproj -c Release
```

It sits in `Neolink.sln` for Visual Studio's sake, but it is the only
Windows-only project in the tree — building the whole solution on Linux fails
on its `net10.0-windows` target, so Linux builds (CI, docker, contributors)
target `src/Neolink.Server` directly, as they always have. The server does not
reference it, so the product's "no third-party dependencies" rule is untouched
— WebView2 is a dependency of the shell alone.

For the installer you need the WiX v5 CLI:

```bash
dotnet tool install --global wix --version 5.0.2
```

then `wix extension add -g WixToolset.Util.wixext/5.0.2`, the same for
`WixToolset.UI.wixext/5.0.2`, and:

```bash
pwsh installer/build-msi.ps1
```

WiX v5 rather than a newer one on purpose: from v6 the toolset requires
accepting the Open Source Maintenance Fee EULA, which is a licensing decision
for the project owner rather than a build detail.

Nobody has to run that by hand for a release, though. CI proves the app,
selftest and installer still build on every pull request (`ci.yml`), and the
release workflow (`docker.yml`) builds the real thing: a `vX.Y.Z` tag runs the
desktop selftest, packages `Neolink.NET.Desktop-X.Y.Z-win-x64.msi` and attaches
it to that tag's GitHub release — creating the release as a draft when the tag
arrives first, so publishing stays a human decision. Beta pushes build an MSI
stamped `X.Y.Z-beta.<run>` and keep it as a 30-day workflow artifact: testable
from the run page, never offered to users.

## What about Linux and macOS?

Not yet. The alerting, settings and rule-matching code is deliberately free of
Windows APIs, so the parts worth porting already are — what is Windows-specific
is the window (WebView2), the tray, the toasts and the autostart entry. A GTK or
Photino host with libnotify and a `.desktop` autostart file would cover Linux;
the tray is the weak spot there, since GNOME needs an extension for one.

Until then, Linux and macOS have the PWA: install the web UI from the browser
for a windowed app, without the tray or the always-on alerting.
