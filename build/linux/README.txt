Meridian for Linux
==================

Run ./Meridian, or ./install.sh to add it to your applications menu and open
.meridian / .sysml files with it (./install.sh --uninstall removes that again).

Other formats on the release page: the .AppImage (one file: chmod +x and run) and
the .deb (sudo apt install ./Meridian-<version>-linux-x64.deb).

Requirements: x64 Linux with WebKitGTK 4.1 and GTK 3, e.g.
  Debian/Ubuntu:  sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0
  Fedora:         sudo dnf install webkit2gtk4.1 gtk3

On first launch choose "This computer" (offline, single user, data stored in
~/.config/Meridian) or "Team server" (enter your Meridian server's address).
