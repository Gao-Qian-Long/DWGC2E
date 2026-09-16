@echo off
chcp 65001 >nul
title DWGC2E

echo.
echo   ============================================
echo    DWG Translator 安装程序
echo   ============================================
echo.
echo   本程序为绿色免安装版本，运行时不依赖 .NET 运行时。
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -SourceDir "%~dp0." %*
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% NEQ 0 (
  echo   安装未完成（错误码 %EXITCODE%），请把上面的信息截图反馈。
) else (
  echo   安装完成。可以在开始菜单或桌面找到 “DWG Translator”。
)
echo.
pause
exit /b %EXITCODE%
