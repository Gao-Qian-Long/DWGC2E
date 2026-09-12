@echo off
echo Publishing DWG Translator as self-contained single-file...
echo.

set PUBDIR=artifacts\publish
set RELEASEDIR=release

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
if not exist "%PUBDIR%\CadPlugin\cad-platform.txt" (
    echo ERROR: CAD plugin platform manifest was not produced.
    exit /b 2
)
set /p CADPLATFORM=<"%PUBDIR%\CadPlugin\cad-platform.txt"
set ZIPFILE=artifacts\DwgTranslator-win-x64-%CADPLATFORM%.zip

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Verify-ReleasePackage.ps1" -PublishDir "%PUBDIR%"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CAD plugin dependency verification failed.
    exit /b %ERRORLEVEL%
)

:: Keep the repository's documented runnable release folder synchronized with the verified output.
if not exist "%RELEASEDIR%\CadPlugin" mkdir "%RELEASEDIR%\CadPlugin"
if not exist "%RELEASEDIR%\glossaries" mkdir "%RELEASEDIR%\glossaries"
if not exist "%RELEASEDIR%\prompts" mkdir "%RELEASEDIR%\prompts"
copy /y "%PUBDIR%\DwgTranslator.exe" "%RELEASEDIR%\DwgTranslator.exe" >nul
copy /y "%PUBDIR%\settings.json" "%RELEASEDIR%\settings.json" >nul
copy /y "%PUBDIR%\CadPlugin\DwgTranslator.Cad.dll" "%RELEASEDIR%\CadPlugin\DwgTranslator.Cad.dll" >nul
copy /y "%PUBDIR%\CadPlugin\DwgTranslator.Core.dll" "%RELEASEDIR%\CadPlugin\DwgTranslator.Core.dll" >nul
copy /y "%PUBDIR%\CadPlugin\cad-platform.txt" "%RELEASEDIR%\CadPlugin\cad-platform.txt" >nul
copy /y "%PUBDIR%\glossaries\mechanical_zh_en.json" "%RELEASEDIR%\glossaries\mechanical_zh_en.json" >nul
copy /y "%PUBDIR%\prompts\deepl_context.txt" "%RELEASEDIR%\prompts\deepl_context.txt" >nul

if exist "%ZIPFILE%" del /q "%ZIPFILE%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBDIR%\*' -DestinationPath '%ZIPFILE%' -CompressionLevel Optimal"
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo.
echo Publish successful!
echo Output: %PUBDIR%\DwgTranslator.exe
echo.
echo %CADPLATFORM% online writeback plugin:
echo   %PUBDIR%\CadPlugin\DwgTranslator.Cad.dll
echo Package: %ZIPFILE%
echo The package is platform-locked and will reject installation into an incompatible CAD host.
