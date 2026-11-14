# Dial for Windows

A Windows tray app that turns the Smart Dial on a CMF Buds Pro 2 case into a control surface for your PC. A port of [athulrajt/dial](https://github.com/athulrajt/dial) (macOS, MIT).

- **Press** (once): play / pause your media.
- **Double press**: opens the mode wheel. Turn to pick, then hold still for a second (a ring fills) or press to choose.
- **Triple press**: back to Volume mode.
- Every turn then drives the chosen mode.

Modes: Volume, Brightness (laptops), Apps (wheel of open windows), Task View, Desktops, Tabs, Scroll, Undo/Redo, Slides, Screenshot.

## Setup

1. In the **Nothing X** app, set the Smart Dial: press → Play/Pause, double press → Next track, rotate → Volume.
2. Connect the buds and make them the Windows sound output. Keep the case lid **closed**.
3. Build and run:

```
dotnet build -c Release
.\bin\Release\net8.0-windows\Dial.exe
```

The Dial icon appears in the tray (check the ^ overflow). Right-click it for the menu.

## Try it without the buds

| Keys | Dial event |
|---|---|
| Ctrl+Alt+← / → | turn |
| Ctrl+Alt+↓ | press |
| Ctrl+Alt+D | double press |

## Settings and log

Both live in `%APPDATA%\Dial\` (tray menu → Open settings / Open log).

- `DeviceNameContains`: only react to the output device whose name contains this. Default `CMF`.
- `ParkLevel`: where volume is parked in non-Volume modes. Default 0.5. Your old volume comes back when you return to Volume mode.
- `VolumePress`: `playpause`, `mute` or `none`.
- `VerboseLog`: logs every media key, volume change and event.

## Notes

- While Dial is running it swallows Play/Pause and Next-track keys, including the ones on your keyboard. Use tray → **Pause Dial** to hand them back.
- Windows only forwards the buds' volume to the PC if Bluetooth absolute volume is on (the default).

## Credit

Inspired by [athulrajt/dial](https://github.com/athulrajt/dial), the macOS app (MIT). This is a separate Windows implementation in C#; no code was copied.
