@echo off
setlocal
title AppLauncher - portable build
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo [AppLauncher] .NET 8 SDK is not installed.
    echo.
    echo Install it with this command in Windows Terminal:
    echo winget install Microsoft.DotNet.SDK.8
    echo.
    echo Then run BUILD_PORTABLE.bat again.
    pause
    exit /b 1
)

echo.
echo Building portable AppLauncher for Windows x64...
rem Use project-relative paths here. Passing the absolute parent path through
rem MSBuild properties can break when an extracted folder contains characters
rem that MSBuild treats as property separators.
dotnet publish "AppLauncher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None --output "Portable"

if errorlevel 1 (
    echo.
    echo Build failed. Copy the error text and send it to ChatGPT.
    pause
    exit /b 1
)

if exist "Portable\AppLauncher.pdb" del /q "Portable\AppLauncher.pdb"

echo.
echo Done. Portable application:
echo %CD%\Portable\AppLauncher.exe
echo.
start "" "Portable"
pause
