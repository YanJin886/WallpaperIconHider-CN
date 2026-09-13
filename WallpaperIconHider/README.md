# Wallpaper Icon Hider

A lightweight Windows utility that automatically hides desktop icons after a configurable period of mouse inactivity while Wallpaper Engine is being used.

## Features

* Automatically hides Windows desktop icons after mouse inactivity.
* Configurable inactivity delay from 1 to 10 seconds.
* Desktop icons reappear immediately when the mouse moves.
* Optional mouse cursor hiding together with desktop icons.
* Prevents icon hiding while a maximized or fullscreen window covers the screen.
* Enable / disable surveillance from the system tray.
* Optional launch with Windows.
* Automatically detects Wallpaper Engine in Steam libraries.
* Custom Windows-style tray menu.
* Startup notification.
* Installer with selectable installation folder.
* No administrator rights required.
* Self-contained Windows x64 build.

## Requirements

* Windows 10 or Windows 11.
* Wallpaper Engine installed through Steam.
* Wallpaper Engine must be available on the computer for icon show/hide commands to work.

## Configuration

Wallpaper Icon Hider stores its local configuration in:

`%LOCALAPPDATA%\\\\WallpaperIconHider\\\\settings.json`

The diagnostic log is stored in:

`%LOCALAPPDATA%\\\\WallpaperIconHider\\\\log.txt`

The log is automatically recreated when it reaches 200 MB.

## Installation

Run `WallpaperIconHider.exe`.

On first launch, the installer lets you choose the installation directory. You can also choose whether to create a desktop shortcut and launch the application immediately.

The application does not modify Wallpaper Engine itself.

## How it works

Wallpaper Icon Hider monitors mouse movement and the foreground window.

When the configured inactivity delay is reached, it asks Wallpaper Engine to hide the desktop icons. When the mouse moves again, it asks Wallpaper Engine to show them.

If a maximized or fullscreen window covers the monitor, the icon-hiding action is blocked so that the utility does not interfere with normal application use.

Wallpaper Engine is detected automatically by checking Steam's installed library locations for:

`steamapps\\\\common\\\\wallpaper\\\_engine\\\\wallpaper64.exe`

## Build

The project uses:

* .NET 10
* Windows Forms
* Target framework: `net10.0-windows`
* Runtime: `win-x64`

The recommended release build is self-contained and single-file.

## Security / Privacy

Wallpaper Icon Hider does not require an Internet connection and does not require administrator privileges.

It only launches Wallpaper Engine with the fixed control commands used to show or hide desktop icons.

No arbitrary commands are executed from external data.

## License

Wallpaper Icon Hider is released under the MIT License.

See the \[LICENSE](LICENSE) file for the full license text.

