#!/usr/bin/env bash
# Cross-platform build script for Modular
# Usage: ./build.sh [target] [options]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_PROJECT="$SCRIPT_DIR/build/_build.csproj"

# Run the Nuke build
dotnet run --project "$BUILD_PROJECT" -- "$@"
