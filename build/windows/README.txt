Meridian for Windows
====================

Installer: run Meridian-<version>-windows-x64-setup.exe. It installs for the current
user (no administrator rights) into %LOCALAPPDATA%\Programs\Meridian, adds Start menu
and desktop shortcuts, opens .meridian and .sysml files, and can be removed from
Settings > Apps.

Portable zip: run Meridian.exe. No installation is needed; keep this folder together.

Requirements: Windows 10 or 11 (x64) with the Microsoft Edge WebView2 Runtime
(preinstalled on Windows 11 and current Windows 10; otherwise get it from
https://developer.microsoft.com/microsoft-edge/webview2/).

On first launch choose "This computer" (offline, single user, data stored in
%APPDATA%\Meridian) or "Team server" (enter your Meridian server's address).
Unsigned builds make SmartScreen ask you to confirm (releases are signed once a
certificate is configured; see docs/RELEASING.md).
