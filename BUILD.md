# How To Build and Install Modular

## Prerequisites

- .NET SDK 8.0 or later
  - Check with: `dotnet --version`
  - Install on Arch/Artix: `sudo pacman -S dotnet-sdk`

### Additional Prerequisites for GUI

- X11 libraries (usually already installed on desktop environments)
  - If needed: `sudo pacman -S libx11 libxcursor libxrandr libxi`
- Font rendering libraries (usually already installed)
  - If needed: `sudo pacman -S fontconfig freetype2`

## Building

From the project root directory:

```bash
# Build all projects
dotnet build -c Release

# Build CLI only
dotnet build src/Modular.Cli/Modular.Cli.csproj -c Release

# Build GUI only
dotnet build src/Modular.Gui/Modular.Gui.csproj -c Release
```

## Installing the CLI

### Option 1: Self-Contained (Recommended)

```bash
# Publish as self-contained for your platform
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r linux-x64 --self-contained -o ~/.local/share/modular

# Create symlink in ~/.local/bin
ln -sf ~/.local/share/modular/modular ~/.local/bin/modular
chmod +x ~/.local/bin/modular
```

### Option 2: Framework-Dependent

```bash
# Publish the CLI project to ~/.local/share
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -o ~/.local/share/modular --self-contained false

# Create launcher script in ~/.local/bin
cat > ~/.local/bin/modular << 'EOF'
#!/bin/bash
exec dotnet "$HOME/.local/share/modular/modular.dll" "$@"
EOF

# Make it executable
chmod +x ~/.local/bin/modular
```

### Verify CLI Installation

```bash
modular --version
modular --help
```

## Installing the GUI

### Standard Installation

```bash
# Publish the GUI as self-contained
dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r linux-x64 --self-contained -o ~/.local/share/modular-gui

# Create symlink in ~/.local/bin
ln -sf ~/.local/share/modular-gui/Modular.Gui ~/.local/bin/modular-gui
chmod +x ~/.local/bin/modular-gui
```

### Single-File Executable (Optional)

```bash
# Publish as a single portable file
dotnet publish src/Modular.Gui/Modular.Gui.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ~/.local/share/modular-gui

# Create symlink
ln -sf ~/.local/share/modular-gui/Modular.Gui ~/.local/bin/modular-gui
```

### Desktop Integration (Optional)

Create a desktop entry for application launchers:

```bash
cat > ~/.local/share/applications/modular.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=Modular Mod Manager
Comment=Next-generation mod manager with plugin support
Exec=/home/$USER/.local/bin/modular-gui
Icon=applications-games
Terminal=false
Categories=Game;Utility;
Keywords=mod;manager;game;
EOF

update-desktop-database ~/.local/share/applications
```

### Verify GUI Installation

```bash
# Launch from terminal
modular-gui

# Or launch from your application menu if you installed the desktop entry
```

## Uninstalling

### Uninstall CLI

```bash
rm ~/.local/bin/modular
rm -rf ~/.local/share/modular
```

### Uninstall GUI

```bash
rm ~/.local/bin/modular-gui
rm -rf ~/.local/share/modular-gui
rm ~/.local/share/applications/modular.desktop  # if you created it
update-desktop-database ~/.local/share/applications
```

### Remove Configuration and Data (Optional)

```bash
# This removes all settings, plugins, and cached data
rm -rf ~/.config/Modular
```

## Platform-Specific Notes

### Linux

- Ensure `~/.local/bin` is in your PATH
- Add to `~/.bashrc` or `~/.zshrc` if needed: `export PATH="$HOME/.local/bin:$PATH"`
- The GUI requires a running X11 or Wayland session
- Both CLI and GUI share the same configuration directory: `~/.config/Modular/`

### Windows

Replace `linux-x64` with `win-x64` in publish commands:

```powershell
# CLI
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r win-x64 --self-contained -o %LOCALAPPDATA%\Modular

# GUI
dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r win-x64 --self-contained -o %LOCALAPPDATA%\Modular-GUI
```

### macOS

Replace `linux-x64` with `osx-x64` (Intel) or `osx-arm64` (Apple Silicon):

```bash
# CLI (Intel)
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r osx-x64 --self-contained -o ~/Library/Application\ Support/Modular

# GUI (Apple Silicon)
dotnet publish src/Modular.Gui/Modular.Gui.csproj -c Release -r osx-arm64 --self-contained -o ~/Library/Application\ Support/Modular-GUI
```

## Development Builds

For development and testing:

```bash
# Debug build (faster, includes debug symbols)
dotnet build -c Debug

# Run CLI directly without installing
dotnet run --project src/Modular.Cli/Modular.Cli.csproj -- --help

# Run GUI directly without installing
dotnet run --project src/Modular.Gui/Modular.Gui.csproj
```

## Troubleshooting

### CLI won't run

- Verify .NET runtime is installed: `dotnet --version`
- Check permissions: `ls -l ~/.local/bin/modular`
- Verify PATH includes `~/.local/bin`: `echo $PATH`

### GUI won't launch

- Check for missing libraries: `ldd ~/.local/share/modular-gui/Modular.Gui`
- Ensure X11 is running: `echo $DISPLAY`
- Check for error messages in terminal when launching
- Install missing dependencies (see Prerequisites section)

### "Command not found" error

```bash
# Add to your shell profile (~/.bashrc, ~/.zshrc, etc.)
export PATH="$HOME/.local/bin:$PATH"

# Reload your shell
source ~/.bashrc  # or ~/.zshrc
```
