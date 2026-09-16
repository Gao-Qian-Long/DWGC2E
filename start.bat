@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0release\DwgTranslator.exe" (
  echo No installed release. Run publish.bat first.
  pause
  exit /b 1
)
start "" /D "%~dp0release" "%~dp0release\DwgTranslator.exe"
