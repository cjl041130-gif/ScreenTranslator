@echo off
cd /d "%~dp0"
if not exist "Release\ScreenTranslator.exe" (
  echo Release\ScreenTranslator.exe is missing. Run Build.ps1 first.
  pause
  exit /b 1
)
start "" "%~dp0Release\ScreenTranslator.exe"
if errorlevel 1 (
  echo Windows could not start the application. Check docs\Acceptance.md for signing policy requirements.
  pause
  exit /b 1
)
