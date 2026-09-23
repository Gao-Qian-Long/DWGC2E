@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================================
echo  目标 1/2：构建并更新本机 release
echo  流程：门禁测试 - 编译 - 打包 - 编译安装器 - 交叉构建校验 - 替换 release
echo  注意：运行前请先关闭正在运行的 QLCAD，否则发布会被拒绝。
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Publish-Desktop.ps1"
set RC=%ERRORLEVEL%

if not "%RC%"=="0" (
  echo.
  echo FAILED: release 未被更新，上面有具体失败项。
  echo         门禁未通过时不会替换 release，正在运行的旧版本仍然可用。
  pause
  exit /b %RC%
)

echo.
echo OK: release\QLCAD.exe 已更新为新版本。
exit /b 0
