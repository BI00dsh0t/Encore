# App icons

These icons are generated from the brand kit in [`branding/`](../../../branding/). Don't edit them by hand.

| File | Use |
|---|---|
| `encore.ico` | The App and engine exe icons, and the installer |
| `tray-idle-dark.ico` | Tray, not capturing, on a dark taskbar: the mark in Mist |
| `tray-idle-light.ico` | Tray, not capturing, on a light taskbar: the mark in Ink |
| `tray-ready.ico` | Tray, ready to save Clips: the app tile, mint on charcoal |
| `tray-recording.ico` | Tray, recording: the app tile with a Record-red dot |
| `tray-error-dark.ico` | Tray, background service crashing, dark taskbar: the Mist mark with a Caution "!" |
| `tray-error-light.ico` | Tray, background service crashing, light taskbar: the Ink mark with a Caution "!" |

The tray icons have 16, 20, 24, 32, 40 and 48 px frames, and `encore.ico` adds 64, 128 and 256 px. Every frame below 256 px is a 32-bit DIB, so `System.Drawing.Icon` and the tray read them.

## Regenerate

Run this from the repository root on Windows when the brand changes, then commit the changed `.ico` files:

```
pwsh -STA -File branding/generate-assets.ps1
```

## Name and logo

The Encore name and logo are reserved: they aren't covered by the repository's GPL-3.0 license. See "Name and logo" in the main README.
