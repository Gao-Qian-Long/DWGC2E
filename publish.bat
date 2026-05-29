@echo off
echo Publishing DWG Translator as self-contained single-file...
echo.

dotnet publish src/DwgTranslator.App/DwgTranslator.App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o src/DwgTranslator.App/bin/Release/net8.0-windows/win-x64/publish

echo.
if %ERRORLEVEL% == 0 (
    echo Publish successful!
    echo Output: src\DwgTranslator.App\bin\Release\net8.0-windows\win-x64\publish\DwgTranslator.exe
) else (
    echo Publish failed with error code %ERRORLEVEL%
)

pause
