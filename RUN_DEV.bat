@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo Install .NET 8 SDK first: winget install Microsoft.DotNet.SDK.8
    pause
    exit /b 1
)
dotnet run --project "AppLauncher.csproj"
if errorlevel 1 pause
