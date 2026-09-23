@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================================
echo  目标 2/2：产出用户可安装的 Setup.exe 并投递到 upload\
echo
echo  说明：安装包是上面那条完整交付流水线的产物之一，所以本脚本会先跑一遍
echo        完整交付（即同时更新本机 release —— AGENTS.md 要求每次成功构建
echo        都更新 release）。随后把交付目录里的"网盘三件套"复制到 upload\：
echo            ^<版本^>-Setup.exe / SHA256SUMS.txt / 开始使用.txt
echo  安全：投递只增不删，upload\ 里已有的文件不会被删除。
echo        先 DryRun 预览用：build-setup.bat -DryRun
echo ============================================================
echo.

call "%~dp0build-release.bat" %*
if errorlevel 1 exit /b 1

rem 参数分流：构建参数（如 -SkipUi）转给发布脚本，投递只认 -DryRun（否则会把未知开关传给投递工具）。
set "UPLOAD_ARGS="
echo %* | findstr /i /c:"-DryRun" >nul && set "UPLOAD_ARGS=-DryRun"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Send-UploadPackage.ps1" %UPLOAD_ARGS%
if errorlevel 1 (
  echo.
  echo FAILED: upload 投递失败（release 已更新）。见上方错误。
  pause
  exit /b 1
)

echo.
echo OK: 安装包与校验文件已放入 upload\；历史版本不会被删除，请自行清理。
pause
exit /b 0
