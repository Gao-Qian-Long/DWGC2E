@echo off
REM ============================================================
REM  DwgTranslator dev cycle script
REM  Steps: Kill AutoCAD -> Build solution -> Run WPF app
REM ============================================================
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo === [1/3] Kill AutoCAD processes ===
taskkill /F /IM acad.exe /T 2>nul
taskkill /F /IM acadlt.exe /T 2>nul
echo   Done (any acad/acadlt processes killed)

echo.
echo === [2/3] Build solution (Debug) ===
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo .NET SDK not found. Launching the latest packaged EXE instead.
    if exist "release\DwgTranslator.exe" start "" "release\DwgTranslator.exe"
    if not exist "release\DwgTranslator.exe" echo [FAILED] release\DwgTranslator.exe not found.
    exit /b 0
)
dotnet build DwgTranslator.sln -c Debug --nologo
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FAILED] Build error, skipping run.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo === [3/3] Run WPF app ===
dotnet run --project src/DwgTranslator.App/DwgTranslator.App.csproj --no-build -c Debug
