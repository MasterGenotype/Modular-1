#!/usr/bin/env bash
# Cross-platform build script for Modular
# Usage: ./build.sh [target] [options]
#
# This script will automatically install .NET 8 SDK if not found.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_PROJECT="$SCRIPT_DIR/build/_build.csproj"
DOTNET_VERSION="8.0"
DOTNET_INSTALL_DIR="$HOME/.dotnet"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

info() { echo -e "${GREEN}[INFO]${NC} $1"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Check if dotnet is available and has the required version
check_dotnet() {
    if command -v dotnet &> /dev/null; then
        local version
        version=$(dotnet --version 2>/dev/null || echo "0.0.0")
        local major_version
        major_version=$(echo "$version" | cut -d. -f1)
        if [[ "$major_version" -ge 8 ]]; then
            return 0
        fi
    fi
    return 1
}

# Install .NET SDK using the official install script
install_dotnet() {
    info "Installing .NET $DOTNET_VERSION SDK..."
    
    local install_script="$SCRIPT_DIR/.dotnet-install.sh"
    
    # Download the install script
    if command -v curl &> /dev/null; then
        curl -sSL https://dot.net/v1/dotnet-install.sh -o "$install_script"
    elif command -v wget &> /dev/null; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$install_script"
    else
        error "Neither curl nor wget found. Please install one of them."
        exit 1
    fi
    
    chmod +x "$install_script"
    
    # Install .NET SDK
    "$install_script" --channel "$DOTNET_VERSION" --install-dir "$DOTNET_INSTALL_DIR"
    
    # Clean up
    rm -f "$install_script"
    
    # Add to PATH for this session
    export PATH="$DOTNET_INSTALL_DIR:$PATH"
    export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
    
    info ".NET SDK installed to $DOTNET_INSTALL_DIR"
    warn "To make this permanent, add these lines to your shell profile (~/.bashrc, ~/.zshrc, etc.):"
    echo "  export DOTNET_ROOT=\"$DOTNET_INSTALL_DIR\""
    echo "  export PATH=\"\$DOTNET_ROOT:\$PATH\""
    echo ""
}

# Main logic
if ! check_dotnet; then
    warn ".NET $DOTNET_VERSION SDK not found."
    
    # Check if running interactively
    if [[ -t 0 ]]; then
        read -p "Would you like to install it automatically? [Y/n] " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Nn]$ ]]; then
            error "Build requires .NET $DOTNET_VERSION SDK. Please install it manually."
            exit 1
        fi
    fi
    
    install_dotnet
    
    # Verify installation
    if ! check_dotnet; then
        error "Failed to install .NET SDK."
        exit 1
    fi
    
    info ".NET SDK installed successfully!"
fi

# Ensure dotnet is in PATH (for local installs)
if [[ -d "$DOTNET_INSTALL_DIR" ]] && [[ ":$PATH:" != *":$DOTNET_INSTALL_DIR:"* ]]; then
    export PATH="$DOTNET_INSTALL_DIR:$PATH"
    export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
fi

# Run the Nuke build
dotnet run --project "$BUILD_PROJECT" -- "$@"
