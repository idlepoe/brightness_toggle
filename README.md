# Brightness Toggle

A lightweight Windows desktop utility for switching screen gamma presets with one key. Built with C# and WinForms. Current version: **1.0.3**.

## Download and run

1. Download **BrightnessToggle-1.0.3-win-x64.zip** from the [latest release](https://github.com/idlepoe/brightness_toggle/releases/latest).
2. Extract the ZIP and run **BrightnessToggle.exe**.
3. Select your display, then press **F9** to cycle between **default (0%)** and **+80%**.

Windows 10/11 x64. No separate Java or .NET installation is required. The portable build opens no console window. Portable releases are unsigned, so Windows may display an unknown-publisher or SmartScreen warning. The default interface language is English. Use the US and South Korean flag buttons at the top of the window to switch between English and Korean. Your choice is saved.

## Presets and preview

- Select a preset in the list to edit it. Selection alone does not apply it.
- Click **추가 (Add)** to create a preset and edit its name.
- Adjust the slider from **-50% to +100%** gamma correction.
- With **조정 시 바로 미리보기 (Live preview)** checked, slider changes apply immediately. Otherwise changes are saved without changing the screen.
- Click **선택한 프리셋 적용 (Apply selected preset)** to apply the selected preset.
- **다음 프리셋 (Next preset)** and the global hotkey cycle through the list.
- **삭제 (Delete)** removes the selected custom preset. The fixed default preset cannot be edited or deleted.

The default preset uses **0% correction**, preserving the original screen. The first additional preset uses **+80% correction**. Settings from earlier versions are migrated: default returns to 0%, and an unmodified built-in +25% preset becomes +80%. Customized presets are preserved.

## Interface language

Use the flag buttons in the upper-right corner: the US flag selects **English**, and the South Korean flag selects **한국어**. Switching updates the window, menus, notifications, and subsequent dialogs immediately. Preset names you entered are preserved. English is the default for new and legacy settings without a saved language.

## Hotkey and display

Choose a display under **대상 디스플레이 (Target display)**. Changing the target restores the previous display first. Choose an F1–F11 hotkey under **전환 단축키 (Cycle hotkey)**; the default is F9. Holding the key does not repeatedly cycle. If another program owns a hotkey, choose a different one.

A notification in the lower-right corner of the target display shows the preset name and correction amount for approximately 2.2 seconds without taking input focus.

## System tray and exit

**The X button hides the window to the system tray; it does not exit the application.** A persistent note and a notification explain this behavior.

- Look for the **yellow sun icon** near the Windows clock. It may be inside the hidden-icons menu.
- Double-click the icon to cycle to the next preset.
- Right-click → **설정 열기 (Open settings)** to reopen the window.
- Right-click → **default (0%)** to restore the original gamma.
- Right-click → **종료 (원래 밝기 복원) (Exit and restore original brightness)** to fully exit.

Normal exit restores the original gamma tables. Forced termination may prevent restoration. Windows notification settings may suppress tray balloons.

## How brightness correction works

This app adjusts **SDR gamma**, not the monitor backlight or physical luminance. **+80% means a gamma correction factor of 1.8**, not an 80% increase in measured display brightness. All displayed percentages refer to correction relative to the captured original gamma table.

The app captures the original RGB gamma tables per display and calculates each preset from that baseline, so repeated switching does not accumulate corrections. Default and normal exit restore that baseline.

### Compatibility

- Start with **SDR and borderless-windowed mode**.
- HDR, exclusive fullscreen, game-controlled gamma, Night Light, and calibration tools can interfere with or overwrite corrections.
- The corner notification may not appear over exclusive-fullscreen games.
- The app does not inject code into games.
- Gamma tables are read back after applying changes. A failed check displays an error and attempts to restore the previous table. A matching table does not guarantee that a game uses the same output path.

See [Microsoft's SetDeviceGammaRamp documentation](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp).

## Troubleshooting

| Symptom | What to check |
|---|---|
| F9 does nothing | Confirm the tray app is running and choose a different hotkey if there is a conflict. |
| The game does not change | Check the target display, disable HDR, and try borderless-windowed mode. The game may override gamma. |
| The app remains running after X | This is expected. Use the tray Exit command. |
| Applying or restoring gamma fails | Read the error in the window and check the driver, HDR mode, and other calibration software. |
| An already-running dialog appears | Exit the existing tray instance before starting a replacement build. |

## Settings

Settings are saved automatically to:

```text
%LOCALAPPDATA%\BrightnessToggle\settings.json
```

To reset settings, exit normally, delete that file, and restart. The app does not register itself to run at Windows sign-in. Packaged Windows apps may have their local settings redirected by Windows.

## Build and test

Install the .NET 10 SDK, then run from the repository root:

```powershell
./scripts/build.ps1
./scripts/test.ps1
```

- Project: `src/BrightnessToggle.csproj`
- Portable output: `publish/BrightnessToggle.exe` (self-contained Windows x64)
- Test output: `TestResults/`

Tests use a fake display backend and do not change the real display. They cover default/+80% cycling, legacy settings migration, gamma calculations, add/remove behavior, failure state, tray hiding, restoration calls, and the corner notification. Actual gamma application and game compatibility require testing on the target system.

## Release history

- **1.0.3:** Added flag buttons for English/Korean, English by default, saved language choice, and Store MSIX packaging.
- **1.0.2:** Restored default to 0%, changed the first built-in additional preset to +80%, unified percentage displays, and migrated legacy defaults.
- **1.0.1:** Added application titles to dialogs. Its temporary 80% default interpretation was corrected in 1.0.2.
- **1.0.0:** Initial preset, hotkey, multi-display, preview, corner notification, and system-tray release.

## Microsoft Store packaging

**Version 1.0.3 was submitted to Microsoft Store certification on October 28, 2026.** The submission is in preprocessing/certification and is configured to publish automatically after approval. It is not yet confirmed publicly available. Store product: **Brightness Toggle**, ID `9NWT13R8QZG6`. [Store page](https://apps.microsoft.com/detail/9NWT13R8QZG6) (available after approval and propagation).

```powershell
./scripts/package-msix.ps1
```

Requires .NET 10 SDK and Windows SDK MakeAppx. The unsigned Store submission package is written to `publish/`. It is intended for Partner Center, not direct end-user installation. Microsoft signs the approved Store package. Store assets and the package identity are in `packaging/`.
