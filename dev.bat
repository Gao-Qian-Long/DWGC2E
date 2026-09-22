@echo off
setlocal
cd /d "%~dp0"
call "%~dp0publish.bat"
if errorlevel 1 (
  echo FAILED: executable was NOT updated. See errors above.
  pause
  exit /b 1
)
start "" /D "%~dp0release" "%~dp0release\QLCAD.exe"
