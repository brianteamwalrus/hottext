# HotText

A tray app that types configured text when you press a hotkey. It's built for sending text into a Windows App or Remote Desktop session from your local PC.

## Run
```
dotnet build -c Release
HotText\bin\Release\net48\HotText.exe            # uses %APPDATA%\HotText\hotkeys.json
HotText\bin\Release\net48\HotText.exe other.json # or a config file you choose
```
The exe is portable (.NET Framework 4.8, which is built into Windows), so you can copy `HotText.exe` anywhere.

Tray menu: Hotkeys (list), Pause, **Edit config** (or double-click the icon), Reload, Open config folder, Start with Windows, Exit.

## Config (`%APPDATA%\HotText\hotkeys.json`)
```json
{
  "enabled": true,
  "onlyWhenForeground": ["Windows365", "msrdc", "mstsc"],
  "keyDelayMs": 0,
  "sendMode": "unicode",
  "hotkeys": [
    { "keys": "Ctrl+1", "text": "text 1" },
    { "keys": "Ctrl+2", "text": "text 2" },
    { "keys": "Ctrl+Alt+S", "text": ["Kind regards,", "Your Name"] }
  ]
}
```
- **keys**: `Ctrl`, `Alt`, `Shift`, `Win` + one key, for example `1`, `A`, `F1`, `Num5`, `Esc`, `PgUp`, `Plus`, `;`. Modifiers must match exactly: Ctrl+Shift+1 does not trigger a Ctrl+1 entry.
- **text**: a string (`\n` = Enter, `\t` = Tab) or a list of lines, which are joined with Enter.
  Put `{delay 100}` anywhere in the text to pause 100 ms at that point (maximum 10000), e.g. `"user\t{delay 100}more text"`. Any other `{...}` is typed as-is.
- **onlyWhenForeground**: process names where hotkeys are active. Outside them, Ctrl+1 does what it normally does. `[]` makes hotkeys active everywhere.
- **keyDelayMs**: a pause between characters if the remote session drops characters (try 5–20).
- **sendMode**: `unicode` (the default; types any character) or `keys` (presses real keys from your layout; try this if an app in the Cloud PC ignores the text).

- **debug**: `true` also logs which app is in front and every Ctrl/Alt/Win key combination HotText receives. Plain typing is never logged. Use the tray menu's **Open log** (`%APPDATA%\HotText\hottext.log`) to see why a hotkey did or didn't fire.

Saving the file reloads it automatically. Problems show up as a tray notification, and the hotkeys that are valid keep working.

## How it works
- A low-level keyboard hook catches the hotkey and swallows it, so the Cloud PC never receives Ctrl+1. In full screen, Windows App installs its own keyboard hook, which would otherwise swallow keys before HotText sees them. So while a Remote Desktop window is in front, HotText reinstalls its hook every second to stay first. Windowed mode doesn't need this.
- The text is typed with `SendInput`. Windows App forwards those keystrokes to the Cloud PC. The clipboard isn't used, so it works even when clipboard redirection is disabled.
- Before typing, HotText waits for you to release Ctrl/Alt, so `text 1` doesn't arrive as Ctrl+T, Ctrl+E…
