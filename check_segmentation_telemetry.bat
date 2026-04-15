@echo off
echo ========================================
echo Segmentation Telemetry Debug Monitor
echo ========================================
echo.
echo Instructions:
echo 1. Make sure Quest 3 is connected (adb devices)
echo 2. Launch Segmentation scene on Quest 3
echo 3. This will show real-time telemetry debug messages
echo.
echo Press Ctrl+C to stop monitoring
echo ========================================
echo.

adb logcat -c
echo Log cleared. Waiting for debug messages...
echo.

adb logcat -s Unity:W Unity:E | findstr /C:"TELEMETRY DEBUG" /C:"SEGMENTATION INFERENCE"
