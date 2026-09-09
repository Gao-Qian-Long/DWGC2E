@echo off
echo Publishing DWG Translator as self-contained single-file...
echo.

set PUBDIR=artifacts\publish
set ZIPFILE=artifacts\DwgTranslator-win-x64-GstarCAD2024.zip

dotnet publish src/DwgTranslator.App/DwgTranslator.App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:NuGetAudit=false ^
    -o %PUBDIR%

if %ERRORLEVEL% NEQ 0 (
    echo Publish failed with error code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

:: The App project builds the CAD plugin and copies its complete private dependency
:: closure to CadPlugin. CAD-host API DLLs (Ac*/Gc*) are intentionally supplied by
:: the installed CAD product and are not redistributed.
if not exist "%PUBDIR%\CadPlugin\DwgTranslator.Cad.dll" (
    echo ERROR: CAD plugin package was not produced.
    exit /b 2
)

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Verify-ReleasePackage.ps1" -PublishDir "%PUBDIR%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CAD plugin dependency verification failed.
    exit /b %ERRORLEVEL%
)

if exist "%ZIPFILE%" del /q "%ZIPFILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBDIR%\*' -DestinationPath '%ZIPFILE%' -CompressionLevel Optimal"
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo.
echo Publish successful!
echo Output: %PUBDIR%\DwgTranslator.exe
echo.
echo GstarCAD/AutoCAD online writeback plugin:
echo   %PUBDIR%\CadPlugin\DwgTranslator.Cad.dll
echo Package: %ZIPFILE%
echo The application auto-detects GstarCAD/AutoCAD and this bundled plugin.
