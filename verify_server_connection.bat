@echo off
echo.
echo ============================================
echo  Verifying Server Connection (Port 8000)
echo ============================================
echo.
echo Please manually launch the app on your Quest 3:
echo   1. Put on the headset
echo   2. Go to App Library
echo   3. Find "PassthroughCameraApiSamples"
echo   4. Launch the app
echo   5. Start MultiObjectDetection scene
echo.
echo This window will show server connection logs.
echo Look for:
echo   - [SERVER SEND] with port 8000 (NOT 8001)
echo   - Request completed messages
echo   - Connection errors (if any)
echo.
echo Press Ctrl+C to stop.
echo.
echo ============================================
echo.

REM Clear old logs first
adb logcat -c

REM Wait a moment
timeout /t 2 /nobreak >nul

REM Show filtered logs in real-time
adb logcat -s Unity | findstr /C:"SERVER" /C:"8000" /C:"8001" /C:"http" /C:"connect" /C:"Request completed"
