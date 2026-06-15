# Playnite FPS Limiter

FPS Limiter is a Playnite extension for setting per-game framerate caps when launching games through Playnite. It is mainly intended for PC games, but it can also be used with emulated games or custom entries imported into Playnite.

It uses RivaTuner Statistics Server (RTSS) to apply the cap. The cap is applied when Playnite launches the game and the previous RTSS profile state is restored when Playnite detects that the game has stopped, so the setting is intended to affect Playnite launches rather than permanently changing the game.

## Requirements

- Playnite desktop or fullscreen mode
- RivaTuner Statistics Server (RTSS)
- Normal, non-admin Playnite is supported after RTSS profile access is set up once

## Installation

1. Download the latest `.pext` from the GitHub Releases page.
2. Open the `.pext` file, or drag it into Playnite.
3. Restart Playnite if prompted.
4. Open the extension settings and press `Test RTSS profile access`.

If the test fails, press `Set up RTSS access...` in settings. Windows will show one UAC prompt, then FPS Limiter will grant your current Windows user access to RTSS profile files.

## Usage

### Desktop Mode

Right-click one or more games and open:

`FPS Limiter`

From there you can:

- Choose one of your configured presets
- Type a custom FPS cap
- Disable the cap for the selected game
- Set or clear a manual target executable

Manual target executable selection is useful for games that launch through another executable, launchers, or emulator setups.

### Fullscreen Mode

Select a game, press the menu button, then open:

`Extensions > FPS Limiter`

Fullscreen mode shows:

- Your configured preset caps
- A custom cap option
- Disable FPS cap

The custom cap option opens Playnite's keyboard input dialog, so it works with a controller-focused setup.

## Settings

- `RTSS executable path`: FPS Limiter fills this with the detected `RTSS.exe` path when it can. You can edit it manually, or clear it to let auto-detection run again.
- `Start RTSS automatically when applying a cap`: Starts RTSS if it is not already running.
- `Use RTSS Global profile while capped games are running`: Uses the RTSS Global profile for the temporary cap. This is the most reliable mode and is enabled by default. The previous Global profile state is restored when the game stops.
- `FPS presets`: Comma-separated values shown in Playnite menus. Spaces and trailing commas are fine; values are normalized when settings are saved.

Default presets are:

```text
30, 60, 120
```

## RTSS Detection

FPS Limiter looks for RTSS in this order:

1. The configured `RTSS.exe` path, if set and valid.
2. A running `RTSS.exe` process.
3. The default install path:

```text
C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe
```

RTSS does not need to be running if it is installed in the default location or if the path is configured manually.

## Troubleshooting

### FPS cap does not apply

Open the extension settings and run `Test RTSS profile access`.

If access fails, run the built-in access setup. This allows normal, non-admin Playnite to write RTSS profile files without running Playnite as administrator.

### Permission warning appears when launching a game

Choose `Set up RTSS access` to run the one-time permission setup, or choose `Continue without cap` to launch the game normally.

### Wrong executable is detected

In desktop mode, right-click the game and use:

`FPS Limiter > Target executable > Choose target executable...`

Fullscreen mode intentionally hides target executable controls because they are better suited to desktop setup.

## Build

Open `FPSLimiter/FPSLimiter.sln` in Visual Studio 2022, or run:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'H:\Tools\playnite-fps-limiter\FPSLimiter\FPSLimiter.sln' `
  /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

## Package

```powershell
& 'C:\Playnite\Toolbox.exe' pack `
  'H:\Tools\playnite-fps-limiter\FPSLimiter\bin\Release' `
  'H:\Tools\playnite-fps-limiter'
```

Install the generated `.pext` in Playnite and restart Playnite if prompted.
