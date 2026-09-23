# Win81Layer

A Windows 8.1 style desktop shell for Windows 10 and 11. It runs as a normal
user program and draws its own Start screen, taskbar, charms bar, app switcher,
context menus and related UI on top of the current desktop. It does not replace
the Windows shell files and it does not modify the operating system.

Built with C# and WPF on .NET 10.

## Requirements

- Windows 10 or Windows 11, 64 bit
- .NET 10 SDK to build, or the .NET 10 Desktop Runtime to run a build

## Build

```
dotnet build src/Win81Layer/Win81Layer.csproj -c Release
```

## Run

```
dotnet run --project src/Win81Layer/Win81Layer.csproj -c Release
```

You can also run the built `Win81Layer.exe` from the build output folder.

To close the shell overlay and return to the normal desktop, press
`Ctrl + Alt + Shift + Backspace`.

## Assets

This repository contains source code only. The Windows 8.1 icons, cursors,
themes and wallpapers are not included, because they are Microsoft material and
cannot be redistributed. Without them the app runs but looks plain.

To get the full look, put your own Windows 8.1 asset files under
`src/Win81Layer/assets` before you build. The project reads any files placed
there. The layout the code expects is:

- `assets/Cursors81` for `.cur` and `.ani` cursors
- `assets/Windows81` for the icon library and its `.json` manifests
- `assets/Win81Icons`, `assets/Wallpapers`, `assets/Weather` for the rest

## Notes

- This code was produced by decompiling and cleaning an earlier build, so some
  compiler generated names and comments are still present.
- The Google Calendar and Gmail tiles are optional. They use your own Google
  OAuth client id and secret, which you enter at runtime. No credentials are
  stored in this repository.
- The app changes only per user settings and its own files. It never edits
  system files.

## License

MIT. See the LICENSE file.
