# Playnite FPS Limiter

A Playnite generic plugin for setting temporary per-game framerate caps when games are launched through Playnite.

The first implementation uses RivaTuner Statistics Server (RTSS) as the limiter backend. Caps are applied to the target executable's RTSS profile before launch and restored when the game stops, so launching the same game outside Playnite keeps the normal behavior.

## Features

- Desktop game context menu: `FPS Limiter`
- Fullscreen menu: select a game, open `Extensions > FPS Limiter`
- Configurable FPS presets, defaulting to `30, 60, 120`
- Desktop-only custom typed cap values
- Manual target executable override for launchers and emulators
- RTSS auto-detection with configurable `RTSS.exe` path
- Restore previous RTSS profile state after the game exits

## Build

Open `FPSLimiter/FPSLimiter.sln` in Visual Studio 2022, or run:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'H:\Tools\playnite-fps-limiter\FPSLimiter\FPSLimiter.sln' `
  /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

The project uses the Playnite SDK NuGet package if restored. On this machine it can also fall back to `C:\Playnite\Playnite.SDK.dll`.

## Package

```powershell
& 'C:\Playnite\Toolbox.exe' pack `
  'H:\Tools\playnite-fps-limiter\FPSLimiter\bin\Release' `
  'H:\Tools\playnite-fps-limiter'
```

Install the generated `.pext` in Playnite and restart Playnite if prompted.
