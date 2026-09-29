# LiquidMonitor v0.1

Native Windows 11 network monitor prototype.

## Current prototype
- WinUI 3 / Windows App SDK 2.5.1
- Borderless rounded always-on-top-capable floating window
- Segoe UI Variable
- Floating compact window
- Live upload/download rate every 500 ms
- Bottom-right initial placement
- Three themes: Liquid Glass, Material 3 Expressive, Frosted Glass

> Note: there is no Mica backdrop and no "Minimal" theme in this version.
> The three themes are code-built gradient/solid brushes, not OS backdrops.

## Build
Install Visual Studio 2022 with WinUI 3 / Windows App SDK tooling and Windows 11 SDK. Open `LiquidMonitor.sln`, choose `x64`, then Build > Build Solution.

Or from a terminal:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## Run
The app is not a `dotnet run` project. Either:

- open `LiquidMonitor.sln`, choose platform **x64**, and press **F5** (launches unpackaged), or
- run the built executable directly:

```
LiquidMonitor.App\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\LiquidMonitor.App.exe
```

Double-click the window to toggle between compact (the speed pill) and the expanded
dashboard. Right-click for the context menu (mode, always-on-top, themes, close).

> The app runs **unpackaged** (`EnableMsixTooling=false`). MSIX packaging is not configured;
> there is no `Package.appxmanifest` or `Assets/` folder in this version.

