@echo off
echo.
echo ============================================
echo  Monitoring [PANEL] Diagnostic Logs
echo ============================================
echo.
echo Please manually launch the app on your Quest 3:
echo   1. Put on the headset
echo   2. Go to App Library
echo   3. Find "PassthroughCameraApiSamples"
echo   4. Launch the app
echo   5. Accept any permissions
echo   6. Start the MultiObjectDetection scene
echo.
echo This window will show [PANEL] logs as they appear.
echo Press Ctrl+C to stop.
echo.
echo ============================================
echo.

REM Clear old logs first
adb logcat -c

REM Wait a moment
timeout /t 2 /nobreak >nul

REM Show filtered logs in real-time
adb logcat -s Unity | findstr /C:"[PANEL]" /C:"[LATENCY]" /C:"[HUD]"
