@echo off
echo.
echo ============================================
echo  Collecting Diagnostic Logs from Quest 3
echo ============================================
echo.
echo This will show LATENCY and HUD diagnostic messages.
echo Press Ctrl+C to stop.
echo.
echo ============================================
echo.

REM Clear old logs first
adb logcat -c

REM Wait a moment
timeout /t 1 /nobreak >nul

REM Show filtered logs
adb logcat -s Unity:I Unity:W Unity:E | findstr /C:"[LATENCY]" /C:"[HUD]" /C:"[TIMING]"
