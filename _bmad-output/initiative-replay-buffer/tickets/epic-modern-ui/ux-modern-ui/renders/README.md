# Renders of the 9.1 design

The renders show three scenes, each in three themes:

- **Scenes:** `library`, `settings` (the General page with the theme picker and a page error bar) and `surfaces` (dialog, tray menu, tooltip, Overlay message cards).
- **Themes:** `fluent-dark`, `fluent-light` and `gaming`.

They come from the scratch WPF prototype in `source/`. It's built with .NET 10, loads Fluent through `ThemeMode` and the Gaming dictionary from `source/gaming.xaml.txt`, and renders with `RenderTargetBitmap` at 2×. It isn't the App. DESIGN.md and EXPERIENCE.md win where a render differs.

What the renders don't show:

- **Mica,** which an off-screen bitmap can't capture. The Fluent shell shows `SolidBackgroundFillColorBase` in its place.
- **The window size.** It's 900×600, larger than the 720×440 default the spines keep (see EXPERIENCE.md's open question).
- **Fluent's accent.** It comes from the Windows accent colour, which is grey on the PC that rendered these. With a coloured accent, the Yes button, the nav pill, the checked box and the star take that colour.
- **Hover states,** except the "Open" menu item and the second Library row, which are drawn hovered. A static render can't show them. Story 2's control gallery shows every state live.
- **The Gaming templates are partial.** `source/gaming.xaml.txt` has rest and checked templates only. Story 2 adds the hover, pressed, disabled and focus states DESIGN.md lists.

To rebuild them: rename `Program.cs.txt` and `Render.csproj.txt`, `dotnet build -c Release`, then run `Render <fluent-dark|fluent-light|gaming> <library|settings|surfaces> <out.png>` with the `.xaml.txt` files beside the exe.
