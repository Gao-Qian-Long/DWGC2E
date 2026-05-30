@echo off
echo Publishing DWG Translator as self-contained single-file...
echo.

set PUBDIR=src\DwgTranslator.App\bin\Release\net8.0-windows\win-x64\publish

dotnet publish src/DwgTranslator.App/DwgTranslator.App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o %PUBDIR%

if %ERRORLEVEL% NEQ 0 (
    echo Publish failed with error code %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

:: Copy the Cad plugin DLL so AutoCAD NETLOAD can find it
echo.
echo Copying DwgTranslator.Cad.dll to publish directory...
copy /Y "src\DwgTranslator.Cad\bin\Release\net8.0\DwgTranslator.Cad.dll" "%PUBDIR%\"
if %ERRORLEVEL% == 0 (
    echo Copied successfully.
) else (
    echo WARNING: Failed to copy DwgTranslator.Cad.dll. Make sure Cad project is built in Release first.
)

echo.
echo Publish successful!
echo Output: %PUBDIR%\DwgTranslator.exe
echo.
echo IMPORTANT: For AutoCAD precise writeback, the Cad plugin is at:
echo   %PUBDIR%\DwgTranslator.Cad.dll
echo You can also configure the path in Settings -^> AutoCAD Configuration.

pause
