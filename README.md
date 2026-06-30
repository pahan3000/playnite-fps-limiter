This is a fork of [mjd4219/playnite-fps-limiter](https://github.com/mjd4219/playnite-fps-limiter) with several additions on top of the original, such as:
- **Fractional FPS caps** (e.g. `59.9`, `23.976`), not just whole numbers.
- **Separate Desktop / Fullscreen profiles** per game and for the global cap, instead of one shared setting.
- **Sync mode control** (Async / Front Edge Sync / Back Edge Sync) per game and for the global cap.
- **Global FPS limit** as a fallback cap for games without their own profile.
- **FPS cap applies on the on the fly**: cap, sync mode, and disable changes apply immediately to an already-running game instead of waiting for the next launch.
- **RTSS auto-close**: if FPS Limiter started RTSS itself, it closes it again once no caps are active.
- **VRR refresh-rate switching**: only for laptops without driver vrr support, that is unlocked via cru. MOST USERS DON'T NEED THIS. Off by default.
- **Match refresh rate to FPS cap**: optionally switches the display to a supported whole multiple of the cap (e.g. 30 FPS → 60 Hz) — for displays without VRR. Off by default; most users won't need this either.

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
- `FPS presets`: Comma-separated values shown in Playnite menus. Decimals are supported (e.g. `59.9`, `23.976`) for displays without VRR that need an exact fractional cap. Spaces and trailing commas are fine; values are normalized when settings are saved.

Desktop mode and Fullscreen mode each keep their own enabled/cap/sync-mode state, both for the global fallback cap and for each game's profile. Playnite detects which mode you're currently running and reads/writes that mode's values automatically; menu headings show `FPS Limiter (Desktop)` or `FPS Limiter (Fullscreen)` so it's clear which one you're editing.

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

## Build a .pext from source

You need Visual Studio (Community is fine) with the ".NET desktop development" workload, and the Playnite SDK NuGet packages (already referenced in `FPSLimiter.csproj`/`packages.config`).

1. Open `FPSLimiter/FPSLimiter.sln` in Visual Studio.
2. Let NuGet restore the referenced Playnite SDK packages (Build > Restore NuGet Packages if it doesn't happen automatically).
3. Set the configuration to `Release` and build (`Build > Build Solution`, or `Ctrl+Shift+B`).
4. This produces a `bin/Release` folder containing `FPSLimiter.dll`, `extension.yaml`, `icon.png`, and the `Localization` folder.
5. Find `Toolbox.exe` inside your Playnite install folder (same folder as `Playnite.DesktopApp.exe`).
6. Pack the extension from a terminal:

```powershell
& 'C:\Path\To\Playnite\Toolbox.exe' pack `
  'C:\Path\To\playnite-fps-limiter\FPSLimiter\bin\Release' `
  'C:\Path\To\output\folder'
```

   The first path is the build output folder from step 4, the second is where Toolbox should write the resulting `.pext` file.
7. Toolbox produces something like `FPSLimiter_4b308964-9a0d-4775-b7c2-78b92af4d7b6_1_0_0.pext` in the output folder.
8. Double-click the `.pext` (or drag it into Playnite) to install it, then restart Playnite if prompted. If you had a previous version of this extension installed, Playnite will update it in place using the GUID in `FPSLimiter.cs`.

If you only changed C# files (not `extension.yaml` or the manifest), you can skip straight to steps 3–8 after each edit.

## Build (command line only)

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'C:\Path\To\playnite-fps-limiter\FPSLimiter\FPSLimiter.sln' `
  /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

## Package (command line only)

```powershell
& 'C:\Path\To\Playnite\Toolbox.exe' pack `
  'C:\Path\To\playnite-fps-limiter\FPSLimiter\bin\Release' `
  'C:\Path\To\playnite-fps-limiter'
```

Install the generated `.pext` in Playnite and restart Playnite if prompted.
