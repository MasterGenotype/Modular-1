# How To Build and Install Modular

## Prerequisites

- .NET SDK 8.0 or later
  - Check with: `dotnet --version`
  - Install on Arch/Artix: `sudo pacman -S dotnet-sdk`

## Building

From the project root directory:

```bash
dotnet build -c Release
```

## Installing to ~/.local/bin

### Option 1: Framework-Dependent (Recommended)

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

### Option 2: Self-Contained (No .NET Runtime Required)

```bash
# Publish as self-contained for your platform
dotnet publish src/Modular.Cli/Modular.Cli.csproj -c Release -r linux-x64 --self-contained -o ~/.local/share/modular

# Copy the executable
cp ~/.local/share/modular/modular ~/.local/bin/modular
chmod +x ~/.local/bin/modular
```

## Verify Installation

```bash
modular --version
```

## Uninstalling

```bash
rm ~/.local/bin/modular
rm -rf ~/.local/share/modular
```

## Notes

- Ensure `~/.local/bin` is in your PATH
- Add to `~/.bashrc` or `~/.zshrc` if needed: `export PATH="$HOME/.local/bin:$PATH"`
