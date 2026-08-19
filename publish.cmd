@echo off
REM Builds the release exe locally. Output lands in publish\ next to this file.
REM   publish.cmd            -> both variants
REM   publish.cmd single     -> only the self-contained single file exe
REM
REM BuildVariant is stamped into the assembly so the built-in updater knows which of the two
REM published exe files to download when a new release appears.

setlocal
set PROJECT=ST Device Monitoring\ST Device Monitoring.csproj

echo.
echo === Self-contained single file (no .NET needed on the target machine) ===
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None ^
  -p:BuildVariant=selfcontained ^
  -o publish\selfcontained
if errorlevel 1 goto :error

if /i "%1"=="single" goto :done

echo.
echo === Framework-dependent (needs the .NET 8 Desktop Runtime, ~2 MB) ===
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=None ^
  -p:BuildVariant=framework-dependent ^
  -o publish\framework-dependent
if errorlevel 1 goto :error

:done
echo.
echo Done. The exe is in publish\selfcontained\
exit /b 0

:error
echo.
echo BUILD FAILED
exit /b 1
