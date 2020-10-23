@echo off

where git >NUL
if NOT ["%errorlevel%"]==["0"] (
	echo git not found on PATH
    pause
    exit /b %errorlevel%
)

echo Restoring git submodules
git submodule update --init --recursive
if NOT ["%errorlevel%"]==["0"] (
    pause
    exit /b %errorlevel%
)

where dotnet >NUL
if NOT ["%errorlevel%"]==["0"] (
	echo dotnet not found on PATH. Install .NET Core!
    pause
    exit /b %errorlevel%
)

echo building setup.csproj
dotnet build setup/setup.csproj

if NOT ["%errorlevel%"]==["0"] (
    pause
    exit /b %errorlevel%
)

start "" "setup\bin\Debug\netcoreapp3.1\setup.exe"