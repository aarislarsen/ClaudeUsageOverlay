# Claude Usage Overlay

A small always-on-top panel for Windows 11 that shows how much of your Claude plan you have
used right now: the rolling 5-hour session window, the 7-day all-models window, and — if
your plan has one — the separate weekly Opus window. It lives in the system tray, updates
itself, and never asks you for anything.

```
┌───────────────────────────────────────────────────────────────┐
│ ▍ Claude                                                 Live │
│                                                               │
│ Session                    4% │ All models               18%  │
│ ▰▰░░░░░░░░░░░░░░░░░░░░░░░░░░░ │ ▰▰▰▰▰░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Reset 02:20  ·  in 2h 44m     │ Reset Sat 09:00  ·  in 5d 6h   │
│                                                               │
│ Updated 21:36                                                 │
└───────────────────────────────────────────────────────────────┘
```

---

## Building it

### What you need

- Windows 10 or 11, x64
- The **.NET 8 SDK**, which includes the Windows Desktop workload:
  <https://dotnet.microsoft.com/download/dotnet/8.0> — pick *SDK 8.0.x, x64*

Check it:

```powershell
dotnet --list-sdks
```

You should see an `8.0.x` line. Nothing else needs installing: the project has no NuGet
dependencies.

### Build a single executable

From the folder containing `ClaudeUsageOverlay.csproj`:

```powershell
dotnet publish -c Release -p:SelfContained=true -p:EnableCompressionInSingleFile=true
```

That produces one self-contained file, with the .NET runtime bundled inside it:

```
bin\Release\net8.0-windows\win-x64\publish\ClaudeUsageOverlay.exe
```

Roughly 90 MB compressed. It runs on any x64 Windows machine with nothing else installed.

**Smaller alternative.** If the machine already has the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), you can build
against it instead and get about 2 MB:

```powershell
dotnet publish -c Release
```

Same output path. This is the project's default.

**While developing**, skip publishing entirely:

```powershell
dotnet run
```

### Install it

The published `.exe` is self-contained and portable — copy it anywhere you like, for example
`C:\Users\<you>\Tools\ClaudeUsageOverlay.exe`, and run it. It writes nothing next to itself;
its settings and log go to `%APPDATA%\ClaudeUsageOverlay`.

To start it with Windows, press <kbd>Win</kbd>+<kbd>R</kbd>, run `shell:startup`, and put a
shortcut to the exe in the folder that opens.

### First run

1. Double-click the exe. The panel appears in the top-right corner of your primary monitor
   and fills in within a second or two.
2. **Find the tray icon.** Windows 11 hides new tray icons by default. Click the `^` chevron
   on the taskbar to see it, and to keep it visible go to *Settings › Personalization ›
   Taskbar › Other system tray icons* and switch `ClaudeUsageOverlay` on.

If nothing appears at all, see [Troubleshooting](#troubleshooting).

---

## Using it

### Reading the panel

Each column is one rate-limit window:

- **Session** — the rolling 5-hour window
- **All models** — the 7-day window across every model
- **Opus** — the separate weekly Opus pool, shown only if your plan has one

Under each name: the percentage used, a meter, and when that window resets, given both as a
wall-clock time and as time remaining. The countdown updates every ten seconds without
touching the network.

The word at the top right is the app's own state:

| | |
| --- | --- |
| `Live` | the last poll succeeded; the numbers are current |
| `Stale` | the last poll failed; you are looking at the last good numbers, and the footer says how old they are |
| `Sign in` | no usable credentials — use **Sign in with browser** in the tray menu |

### Colours

| Band | Colour | |
| --- | --- | --- |
| under 25% | `#2F97C4` blue | plenty left |
| 25–59% | `#3FBF74` green | working through it |
| 60–79% | `#F2872E` orange | worth knowing |
| 80% and up | `#FF5065` red | nearly out |

The meter, the percentage, and the tray icon all use the same colour, so a window's state is
legible from any of them. The low band is a deep blue rather than a bright cyan on purpose:
the panel spends most of its life reporting that nothing is wrong, and that should never read
as an alert.

The pip beside the title follows the worst of the windows, and turns orange when the data is
stale or red when a sign-in is needed.

The `severity` the server reports can raise a band but never lower it. The server calls
everything `normal` until you are nearly out, so on its own it would collapse the two quiet
bands into one — and a meter that only warns at the last moment is not a warning.

### The tray icon

The icon is a ring filled to your session percentage, coloured by the worst of the windows.
You can hide the panel entirely and still know where you stand. Hovering shows both figures;
double-clicking refreshes immediately.

### The tray menu

| Item | What it does |
| --- | --- |
| **Refresh now** | Fetches immediately. Double-clicking the tray icon does the same. |
| **Show overlay** | Hides or shows the panel. The tray icon keeps updating either way. |
| **Position ▸** | Puts the panel at any of eight anchor points on the chosen monitor: the four corners, the middle of the left and right edges, and the middle of the top and bottom edges. |
| **Position ▸ Locked** | On by default: the panel ignores the mouse completely. Turn it off to drag the panel anywhere; choosing an anchor again forgets the dragged position. |
| **Sign in with browser** | Fallback OAuth sign-in. You should not normally need it. |
| **Open settings file** | Opens `settings.json` in your default editor. |
| **Open log file** | Opens the app's log. |
| **Quit** | Exits immediately. |

### Moving the panel

By default the window is transparent to the mouse — you cannot click it, drag it, or
accidentally focus it, so it can never get in the way of what is underneath. Use
**Position ▸** to place it at one of the eight anchor points:

```
Top left        Top centre        Top right
Middle left                       Middle right
Bottom left     Bottom centre     Bottom right
```

The menu lists them in that order, in three separated groups, so the menu has the same shape
as the screen it is talking about.

If you want the panel somewhere else entirely, turn off **Position ▸ Locked**, drag it where
you want it, and turn Locked back on. The position is remembered. Choosing an anchor from the
menu clears it again.

---

## Where the numbers come from

The app reads `GET https://api.anthropic.com/api/oauth/usage` using your existing Claude Code
OAuth token. That is the same server-side figure Claude Code reports for `/usage`, so it
matches what actually counts against your limits, and reading it costs no model tokens.

Polling is every 60 seconds by default. On top of that, the app watches Claude Code's
transcript folder and re-reads a few seconds after real activity, so the numbers track your
actual work rather than only the clock. Whatever triggers a refresh, it will not call the
endpoint more than once every 15 seconds.

## Signing in

Normally there is nothing to do. The app looks for the token Claude Code already stores, in
this order:

1. `%USERPROFILE%\.claude\.credentials.json`
2. `%CLAUDE_CONFIG_DIR%\.credentials.json`, if that variable is set
3. any paths you list in `extraCredentialPaths` in settings
4. Claude Code installations inside WSL, discovered automatically under
   `\\wsl.localhost\<distro>\home\<user>\.claude\.credentials.json`
5. the app's own cache, written after a browser sign-in or a token refresh

Whichever of those is valid for longest is used. When a token nears expiry the app refreshes
it and stores the result in **its own** cache at
`%APPDATA%\ClaudeUsageOverlay\credentials.json`. It never writes to Claude Code's credentials
file, because Claude Code owns that file and rewrites it on its own schedule; a second writer
would race it.

If no usable token is found, the panel shows `Sign in`, and **Sign in with browser** runs the
standard OAuth flow in your normal browser. On a machine where you already use Claude Code,
you should never need it.

## Settings

`%APPDATA%\ClaudeUsageOverlay\settings.json`, re-read at startup:

| Key | Default | Meaning |
| --- | --- | --- |
| `anchor` | `TopRight` | `TopLeft`, `TopCentre`, `TopRight`, `MiddleLeft`, `MiddleRight`, `BottomLeft`, `BottomCentre`, `BottomRight` |
| `marginX`, `marginY` | `18` | Gap from the screen edge |
| `monitorIndex` | `0` | Which monitor, in the system's own order |
| `pollSeconds` | `60` | Seconds between polls, 15–3600 |
| `opacity` | `0.94` | Panel opacity, 0.35–1.0 |
| `scale` | `1.0` | Panel size, 0.7–2.0 |
| `clickThrough` | `true` | Panel ignores the mouse |
| `showOverlay` | `true` | Panel visible at start |
| `watchClaudeActivity` | `true` | Re-poll shortly after Claude Code writes a transcript |
| `extraCredentialPaths` | `[]` | Extra `.credentials.json` files to consider |
| `customLeft`, `customTop` | `null` | Set by dragging; cleared by choosing an anchor |

Most of these have a menu equivalent. The file is for the ones that do not: `scale`,
`opacity`, `pollSeconds`, `monitorIndex`, and the credential paths.

## Troubleshooting

Everything the app knows about its own failures is in:

```
%APPDATA%\ClaudeUsageOverlay\overlay.log
```

It records startup, where the panel was placed, and every failed poll. **Open log file** in
the tray menu opens it.

| Symptom | Cause |
| --- | --- |
| Nothing on screen, no tray icon | Windows 11 is hiding the tray icon — click the `^` chevron. If the log is empty too, the app never started; run it from a terminal to see the error. |
| Panel shows `Sign in` | No usable token. Run `claude` once to sign Claude Code in, then **Refresh now** — or use **Sign in with browser**. |
| Panel shows `Stale` and stays there | Network or endpoint failure; the log has the status code. The last good numbers stay on screen. |
| Panel is off-screen after changing monitors | Choose an anchor from **Position ▸**; that clears any dragged position and re-places it. |
| Two panels | Two copies running. The app allows only one, so this means one is a stale process — end `ClaudeUsageOverlay` in Task Manager. |
| Build error `MSB4025` or `CS0104` | You are building modified sources; both are covered by comments in `ClaudeUsageOverlay.csproj`. |

---

## Interface notes

The panel follows Jef Raskin's rules from *The Humane Interface*, which for something this
small mostly means restraint:

- **No modes.** There is one view. Nothing is hidden behind a hover, a click, or an expanded
  state, so there is never a state you have to get out of before you can read it.
- **It is never in the way.** By default the window is transparent to the mouse and never
  takes focus. It cannot swallow a click, a keystroke, or your attention.
- **No interruptions.** No dialogs, no balloons, no sounds, no animation, no blinking. A
  failed poll leaves the last good numbers on screen and quietly marks them `Stale`; errors
  go to the log, not to your face. The one exception is a failure during startup, where there
  is no panel and no tray icon yet, and silence would leave you with nothing at all.
- **Habituation is protected.** Columns keep a fixed order, a fixed width, and fixed
  positions. Percentages are whole numbers, because a flickering decimal pulls the eye for no
  gain. A column that appears is never removed while the app runs, so the layout you learned
  stays the layout you have.
- **No arithmetic left to the user.** Each window shows both the wall-clock reset time and
  the time remaining, and the countdown ticks locally between polls.
- **Nothing to confirm.** Quit quits. There is no unsaved state, so asking "are you sure"
  would only charge you for a decision you already made.
- **One control surface.** Everything the app can be told is in the tray menu, once, in one
  place — and the menu is painted in the panel's own palette, because a control surface that
  looks like it belongs to a different program makes you check whether it does.
- **Zero configuration to start.** It works the first time it runs, with no setup screen and
  no sign-in.

## Source layout

| File | Role |
| --- | --- |
| `App.xaml(.cs)` | Startup, polling loop, palette, wiring |
| `OverlayWindow.xaml(.cs)` | The panel, its state display, and its placement |
| `UsageRow.xaml(.cs)` | One metric column: label, percentage, meter, reset |
| `TrayIconHost.cs` | Tray icon, menu, and the drawn ring glyph |
| `DarkMenu.cs` | Dark renderer for the tray menu |
| `Services/UsageClient.cs` | Calls the usage endpoint and parses it |
| `Services/CredentialStore.cs` | Finds and refreshes OAuth tokens |
| `Services/OAuthPkce.cs` | Fallback browser sign-in |
| `Services/ActivityWatcher.cs` | Watches Claude Code transcripts to poll on real activity |
| `Services/AppSettings.cs` | Settings load and save |
| `Services/Log.cs` | Append-only log |
| `Models.cs` | Usage windows, severity bands, token records |
| `Native.cs` | Click-through and always-on-top window styles |
