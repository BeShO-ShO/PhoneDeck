# PhoneDeck: notes for Claude

PhoneDeck makes a Windows PC act as a Bluetooth LE keyboard, mouse and touchscreen for an Android
phone, whose screen is shown on the PC through a USB capture device. It is a derivative of
[windows-ble-hid](https://github.com/abhishek-raj/windows-ble-hid) by Abhishek Raj (MIT).

- [README.md](README.md): what PhoneDeck is, how it's used, and what it adds. This is the current source of truth.
- [DEVELOPMENT.md](DEVELOPMENT.md): the original author's Bluetooth constraints and research.
  **Read the relevant section before changing Bluetooth, capture or pacing behaviour.** Some
  paths and names in it predate the rename (for example `%LOCALAPPDATA%\BleHid`; PhoneDeck uses
  `%LOCALAPPDATA%\PhoneDeck`).

## Layout

- `src/PhoneDeck.Core`: GATT peripheral, HID descriptors and reports, input capture, touch
  mapping, pacing, diagnostics. Platform-independent logic belongs here so it can be tested.
- `src/PhoneDeck.App`: the WPF UI (WinForms is used only for the tray icon).
- `src/PhoneDeck.Cli`: the diagnostic and background-mode client.
- `tests/PhoneDeck.Core.Tests`: xunit tests for Core.
- There is no solution file. Build each project directly.

## Tooling on this machine

- The .NET 8 SDK is at `C:\Users\bduhe\AppData\Local\Microsoft\dotnet\dotnet.exe` (not on
  PATH). Build from a copy outside OneDrive so `bin`/`obj` don't sync:
  `robocopy <repo> C:\Users\bduhe\PhoneDeck-Build\work /MIR /XD .git bin obj /XF .git`, then
  run `dotnet test` and `dotnet publish ... -r win-arm64 --self-contained true
  -p:PublishSingleFile=true -p:DebugType=none -o out\arm64` there. Install a test build by
  copying `out\arm64` over `PhoneDeck-Build\app-arm64` (keep the previous one as a backup).
- The Surface Go 3 gets its x64 build through OneDrive: `Documents\PhoneDeck-backup\
  PhoneDeck-personal-win-x64.zip` (with `PhoneDeck-Build\INSTALL.txt` inside). When you
  replace it, update the hash in `Surface Go 3 install steps.txt` there.
- git and gh aren't on PATH. Use the bundled copies:
  - `C:\Users\bduhe\PhoneDeck-Build\tools\MinGit\cmd\git.exe`
  - `C:\Users\bduhe\PhoneDeck-Build\tools\gh\bin\gh.exe`
- `.git` is a file pointing to `C:\Users\bduhe\PhoneDeck-Build\git\PhoneDeck.git`, which keeps
  git data out of OneDrive. Never edit files inside that folder by hand.
- Remotes: `origin` is BeShO-ShO/PhoneDeck. `upstream` is abhishek-raj/windows-ble-hid and is
  fetch-only; never try to push to it.
- The repo itself lives in OneDrive. Don't put build output, logs or captures in it.

## Builds and releases

- Pushing a `v*` tag runs `release.yml`, which tests, then publishes self-contained single-file
  builds for `win-x64` and `win-arm64` and attaches the zips to a GitHub release. Always ask
  before creating or pushing a tag.
- Installed test builds live outside the repo, in `C:\Users\bduhe\PhoneDeck-Build\app-arm64`
  (this Surface Pro 11, ARM64) and `app-x64` (the Surface Go 3).
- Both machines share a OneDrive desktop. A shortcut named `<Name> - GO.lnk` is for the x64
  Surface Go 3 and looks broken here. Never delete or "fix" those shortcuts.

## Code rules that are easy to break

- Target `net8.0-windows10.0.22000.0` with `SupportedOSPlatformVersion` 19041. Keep Windows
  11-only calls behind runtime guards so Windows 10 still works.
- DPI awareness is declared in `app.manifest` (PerMonitorV2), not in code, because the
  low-level hook reports physical pixels. Keep `WFAC010` suppressed and don't move it to the
  WinForms property.
- `PhoneDeck.App.csproj` removes some WinForms implicit usings because they collide with WPF
  (`Application`, `MessageBox`, `Color`, `Size`). Don't add them back.
- `InputCapture` runs on its own MTA thread with its own message loop. Don't make it STA or run
  it on the UI thread.
- Mouse motion may be coalesced. Keyboard transitions and button releases must never be dropped
  or reordered, and held keys and buttons must be released on stop or target change.
- Only one process may own the peripheral (`SingleInstance`). The app closes to the tray
  because ending the process removes the GATT service.
- WPF-UI symbol names are only checked when a page loads. A typo builds fine and then crashes
  when the user navigates to that page.
- Touch, pen and the mouse-event filter all use `VideoRegion` (Core), which `MpvVideoLocator`
  keeps on mpv's picture. Coordinates are physical pixels of the main display.
- Several fingers go out as one report per finger ("hybrid" mode: the first carries the frame's
  contact count, the rest 0). Fingers held still are re-sent every 50 ms, because Linux releases
  a contact it hasn't heard about for 100 ms. Never reuse a contact id inside one frame.
- Any change to `HidDescriptors.ReportMapValue` needs phones to forget and re-pair. Say so.

## Testing and hardware

- CI has no Bluetooth radio. Pairing, capture, touch and reconnect can only be checked on real
  hardware by the user. List what needs a hardware check instead of claiming it works.
- Before any radio, pairing or Bluetooth-setting change, or before stopping a running
  PhoneDeck, ask first. Never send key reports unless the user confirms a text field on the
  phone has focus. Unfocused keys act as phone shortcuts.
- Add or update Core tests for logic changes. Tests exist for descriptors, reports, touch,
  key mapping, pacing, gestures and lock state.

## Commits and credit

- Keep Bluetooth addresses, bond keys, hostnames, btsnoop captures and bug reports out of
  commits. `.gitignore` covers `*.local.md`, `bugreport-*/` and `*.btsnoop`.
- Keep the original author's credit intact in README, LICENSE and history. PhoneDeck changes
  are credited to BeShO-ShO.
- Commit or push only when asked.
