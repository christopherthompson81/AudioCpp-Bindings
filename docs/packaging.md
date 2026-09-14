# Packaging and installing

The app is a consumer of a native engine it does not build, so installing it is
not just a publish. Four things have to travel with it:

| | why it matters if missing |
|---|---|
| `libaudiocpp` | nothing works; the app reports the library was not found |
| the audioio shim | no capture and no playback |
| `model_specs` | the catalogue is empty, and safetensors packages fail with "model spec not found for family" |
| the silero_vad weights | VAD chunking silently falls back to fixed chunking — a quiet quality loss, not an error |

Every script lays these out under the install directory **exactly as they sit in
a source checkout**:

```
<prefix>/AudioCpp.Bindings.Gui
<prefix>/external/audio.cpp/build/bin/libaudiocpp.so
<prefix>/external/audio.cpp/model_specs/
<prefix>/external/audio.cpp/assets/framework/models/silero_vad/
<prefix>/native/audioio/build/libaudioio.so
```

That is deliberate, and it is why installing needs no code changes: both native
resolvers and both asset lookups walk up from the executable looking for those
paths, so an install directory laid out this way is found the same way a
checkout is.

## Before any of it

Build the engine and the audio shim first — the installers check for both and
refuse rather than producing a broken install:

```bash
./scripts/build-engine.sh -DENGINE_ENABLE_CUDA=ON   # or without, for CPU
./scripts/build-native.sh
```

## Linux

```bash
./install.sh                              # ~/.local/share/audiocpp-studio
./install.sh --prefix /opt/audiocpp-studio
./install.sh --no-engine                  # app only; needs AUDIOCPP_NATIVE_DIR

./uninstall.sh                            # leaves settings alone
./uninstall.sh --purge                    # also removes ~/.config/audiocpp-studio
```

It publishes self-contained, registers icons in the hicolor theme at eight
sizes, and writes a `.desktop` entry.

The entry sets `StartupWMClass=AudioCpp.Bindings.Gui`, which is what ties the
running window to the launcher entry — and so to the icon — in the taskbar and
the switcher. Avalonia sets `WM_CLASS` from the assembly name; without the line
the window shows a generic icon beside a correct one in the menu, which reads as
two different applications.

The uninstaller refuses to delete a `--prefix` that does not contain the app's
executable. `rm -rf` on a path the user typed does not get a second chance.

Downloaded models are never removed by either script: they live in whichever
models root you chose, they are large, and they are not the installer's to
delete.

## macOS

```bash
./package-macos.sh                        # dist/audio.cpp Studio.app
./package-macos.sh --framework-dependent  # smaller; needs .NET where Finder sees it
cp -R "dist/audio.cpp Studio.app" /Applications/   # -R, not -r: -r mangles bundles
```

A bundle is the only form macOS gives a real Dock icon, a real name in the menu
bar, and a double-clickable launcher.

It defaults to **self-contained**, which is not the usual preference: a
framework-dependent bundle launched from Finder inherits no `PATH` and finds
.NET only through `DOTNET_ROOT` or the official installer's location. On a
machine where `dotnet` came from Homebrew, double-clicking fails with "you must
install .NET" even though `dotnet run` works. An `.app` that cannot be
double-clicked has missed its point.

The bundle is ad-hoc signed. That is not notarization — `spctl` still rejects it
— but it avoids the "damaged and can't be opened" message an unsigned, quarantined
bundle produces. Ad-hoc signatures change on every rebuild, and macOS keys
privacy permissions to bundle ID plus signature, so a rebuilt app can lose its
microphone grant and need re-adding under Privacy & Security.

Running unbundled still shows the right Dock icon: the app sets it through
AppKit at startup, because a bare executable has no bundle to read one from.

## Windows

From PowerShell:

```powershell
.\install-windows.ps1
.\install-windows.ps1 -Prefix D:\Apps\audiocpp-studio -NoShortcuts

.\uninstall-windows.ps1
.\uninstall-windows.ps1 -Purge
```

It publishes self-contained to `%LOCALAPPDATA%\Programs\audiocpp-studio`, copies
everything beside `audiocpp.dll` — a CUDA build brings its own runtime DLLs and
the app will not start without them — and creates Start Menu and Desktop
shortcuts.

The shortcut icon comes from the `.exe` itself: `ApplicationIcon` writes it into
the PE resource, which is what Explorer, the Start Menu and the taskbar read.

The engine lookup handles multi-config generators. Visual Studio writes to
`build\bin\Release\`, not `build\bin\`, and a search of `bin\` alone finds
nothing on a tree where the DLL is sitting right there.

## The icons

There is no upstream artwork to copy. audio.cpp's web UI builds its badge in
CSS — a rounded square, a 135° cyan-to-blue gradient, a 900-weight `A`
(`webui/native/src/app.css`, `.brand .mark`) — so `scripts/make-icons.sh`
reproduces that rule rather than tracing a picture of it, and the committed PNGs
have a provenance instead of being an opaque binary.

```bash
./scripts/make-icons.sh                 # needs ImageMagick and Inter Black
./scripts/make-icons.sh --font /path/to/Inter-Black.otf
```

It writes `AppIcon.png` (1024², the master), `icon.ico` (seven sizes, for the PE
resource) and `icon-<size>.png` for the freedesktop theme.

Three separate mechanisms are needed, because no single one covers all three
platforms:

| | mechanism | covers |
|---|---|---|
| `ApplicationIcon` in the csproj | Windows PE resource | Explorer, taskbar, shortcuts |
| `Window.Icon` in MainWindow.axaml | `_NET_WM_ICON` | Linux taskbars and switchers |
| `MacDockIcon.Set` at startup | AppKit `setApplicationIconImage:` | the macOS Dock, bundled or not |

`ApplicationIcon` is a Windows-only PE resource and macOS cannot read `.ico` at
all; `Window.Icon` on macOS is the title-bar proxy icon, not the Dock tile. Only
the master PNG is embedded in the assembly — the per-size files are packaging
inputs read from disk, and embedding them too would add a third of a megabyte to
every build for nothing.
