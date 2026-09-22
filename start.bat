@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0release\QLCAD.exe" (
  echo No installed release. Run publish.bat first.
  pause
  exit /b 1
)
start "" /D "%~dp0release" "%~dp0release\QLCAD.exe"
