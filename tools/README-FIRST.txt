RONIN: a free, open-source live wallpaper for Windows 10 / 11
=============================================================

START IT
  1. Unzip this whole folder somewhere permanent, for example
     Documents\Ronin (not inside the zip, and not in Downloads if you clean it out).
  2. Double-click "Start Ronin.cmd" (or app\RoninHost.exe).
  3. Windows may say "Windows protected your PC" because the app isn't code-signed.
     Click "More info" -> "Run anyway". It's open source, so you can read every line:
     https://github.com/HikaYoru/ronin-wallpaper

  The wallpaper appears behind your desktop icons. Ronin lives in the system tray
  (the gold diamond icon near the clock; click the ^ arrow if you don't see it).

TRAY ICON
  Left-click .............. Settings
  Right-click ............. Arrange layout, Tasks, Monitors, Layouts, Pause, Start with Windows, Quit

FIRST THINGS TO TRY
  - Right-click the tray icon -> "Start with Windows"
  - Settings -> Background: pick an image, or drop your own pictures, GIFs or videos
    into the ronin-wallpaper\images folder
  - Right-click -> "Arrange layout..." to drag the clock, quote and tasks where you want
  - Ctrl+Alt+T shows your task list over everything
  - Play some music: the gold ink line at the bottom moves with it

NEEDS
  Windows 10 (version 2004 or newer) or Windows 11, 64-bit.
  Microsoft Edge WebView2 Runtime, for the settings window (already on Windows 11;
  on Windows 10: https://developer.microsoft.com/microsoft-edge/webview2/).
  Nothing else: .NET is included.

UPDATES
  Ronin checks GitHub once a day and offers "Install update" in the tray menu.
  Your settings (%APPDATA%\RoninHost) and your own images are kept.

UNINSTALL
  Quit from the tray, untick "Start with Windows" first if you turned it on,
  then delete this folder and %APPDATA%\RoninHost.

LICENSE
  Public domain (The Unlicense): copy it, change it, re-release it, anything.
  The fonts are SIL Open Font License (see app\fonts).
