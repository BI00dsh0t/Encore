# Code notes

Comments in the code are one line each. This file holds the rules that need more room: which thread owns a class, what
must happen in what order, and the Windows or driver quirks behind a surprising line. There is one heading per class,
grouped by project.

## Encore.Platform.Desktop

### CaptureExclusion

- `ExcludeNewWindows` covers WPF windows, popups and native dialogs alike.
- Its hook is "out of context": events arrive only while the calling thread pumps messages, as the WPF UI thread does.
- A window may show for a moment before it is excluded.

### Monitors

- `OverlayPlanes` asks DXGI (`IDXGIOutput2::SupportsOverlays`) about the monitor holding the game's largest window.
- Why it matters: a topmost window over a borderless flip-model game kept the game in independent flip only when a
  hardware plane was free. Without one, DWM composed the game and about 7.5 % of the frame rate was lost.
- A later measurement showed the always-on dot without planes still left the game in independent flip.

### VideoSurface

- One BGRA texture has two views: Windows' media player copies each frame in through `Direct3DSurface`, and a WPF
  `D3DImage` shows it through `BackBuffer`. That is how the page's controls can draw over the video.
- It owns a D3D11 device and a D3D9Ex device on the App's window, made once. `Resize` remakes only the texture and its
  views, and releases the old views first.
- Every step checks its HRESULT and throws, so a caller sees a GPU failure and not a blank picture.
- `Dispose` releases every COM reference at once, with no wait for a garbage collection. The WinRT wrapper holds its own
  references (one native, and one per interface it was used through) even after `Close`, so each is released by hand.
- Create, use and dispose it on the UI thread.
