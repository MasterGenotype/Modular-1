@echo off
:: Cross-platform build script for Modular
:: Usage: build.cmd [target] [options]
::
:: This script will automatically install .NET 8 SDK if not found.

setlocal EnableDelayedExpansion

set SCRIPT_DIR=%~dp0
set BUILD_PROJECT=%SCRIPT_DIR%build\_build.csproj
set DOTNET_VERSION=8.0
set DOTNET_INSTALL_DIR=%USERPROFILE%\.dotnet

:: Check if dotnet is available and has the required version
call :check_dotnet
if %ERRORLEVEL% == 0 goto :run_build

echo [WARN] .NET %DOTNET_VERSION% SDK not found.
set /p INSTALL="Would you like to install it automatically? [Y/n] "
if /i "%INSTALL%"=="n" (
    echo [ERROR] Build requires .NET %DOTNET_VERSION% SDK. Please install it manually.
    exit /b 1
)

call :install_dotnet
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Failed to install .NET SDK.
    exit /b 1
)

echo [INFO] .NET SDK installed successfully!

:run_build
:: Ensure dotnet is in PATH (for local installs)
if exist "%DOTNET_INSTALL_DIR%\dotnet.exe" (
    set "PATH=%DOTNET_INSTALL_DIR%;%PATH%"
    set "DOTNET_ROOT=%DOTNET_INSTALL_DIR%"
)

:: Run the Nuke build
dotnet run --project "%BUILD_PROJECT%" -- %*
exit /b %ERRORLEVEL%

:check_dotnet
where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 exit /b 1
for /f "tokens=1 delims=." %%v in ('dotnet --version 2^>nul') do (
    if %%v GEQ 8 exit /b 0
)
exit /b 1

:install_dotnet
echo [INFO] Installing .NET %DOTNET_VERSION% SDK...

set INSTALL_SCRIPT=%SCRIPT_DIR%.dotnet-install.ps1

:: Download the install script using PowerShell
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%INSTALL_SCRIPT%'"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Failed to download .NET install script.
    exit /b 1
)

:: Install .NET SDK
powershell -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_SCRIPT%" -Channel %DOTNET_VERSION% -InstallDir "%DOTNET_INSTALL_DIR%"
set INSTALL_RESULT=%ERRORLEVEL%

:: Clean up
del "%INSTALL_SCRIPT%" 2>nul

if %INSTALL_RESULT% neq 0 exit /b 1

:: Add to PATH for this session
set "PATH=%DOTNET_INSTALL_DIR%;%PATH%"
set "DOTNET_ROOT=%DOTNET_INSTALL_DIR%"

echo [INFO] .NET SDK installed to %DOTNET_INSTALL_DIR%
echo [WARN] To make this permanent, add %DOTNET_INSTALL_DIR% to your PATH environment variable.
exit /b 0
