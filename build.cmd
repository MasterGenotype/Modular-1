@echo off
:: Cross-platform build script for Modular
:: Usage: build.cmd [target] [options]

setlocal
set SCRIPT_DIR=%~dp0
set BUILD_PROJECT=%SCRIPT_DIR%build\_build.csproj

dotnet run --project "%BUILD_PROJECT%" -- %*
