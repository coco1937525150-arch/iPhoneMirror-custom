@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "HOST=%~dp0Maitou.CaptureHost.exe"
set "REPORT=%TEMP%\iPhoneMirror-USB-ReadOnly-Diagnostics-%RANDOM%.txt"

if not exist "%HOST%" (
  echo USB capture host is missing. Please extract the entire iPhoneMirror ZIP.
  pause
  exit /b 2
)

echo iPhoneMirror USB capture diagnostic > "%REPORT%"
echo Read-only mode: no drivers installed; no USB configuration switched. >> "%REPORT%"
echo. >> "%REPORT%"
echo === Backend information === >> "%REPORT%"
"%HOST%" --capture-backend-info >> "%REPORT%" 2>&1
echo. >> "%REPORT%"
echo === Read-only USB preflight === >> "%REPORT%"
"%HOST%" --usb-readonly-preflight >> "%REPORT%" 2>&1
set "RESULT=%ERRORLEVEL%"
echo. >> "%REPORT%"
echo Preflight exit code: %RESULT% >> "%REPORT%"
echo.
echo USB preflight finished. Exit code: %RESULT%
echo Diagnostics: %REPORT%
echo A Notepad window will open with the detailed result.
start "" notepad.exe "%REPORT%"
endlocal
