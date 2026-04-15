@echo off
setlocal enabledelayedexpansion

echo ========================================
echo Segmentation Telemetry Log Capture
echo ========================================
echo.

set LOGFILE=segmentation_telemetry_%date:~10,4%%date:~4,2%%date:~7,2%_%time:~0,2%%time:~3,2%%time:~6,2%.log
set LOGFILE=%LOGFILE: =0%

echo Saving logs to: %LOGFILE%
echo.
echo Instructions:
echo 1. Quest 3 connected: YES
echo 2. Now launch Segmentation scene on Quest 3
echo 3. Send 5-10 frames
echo 4. Press Ctrl+C when done
echo.
echo Monitoring started...
echo ========================================
echo.

adb logcat -c
adb logcat -s Unity:V Unity:D Unity:I Unity:W Unity:E > "%LOGFILE%" 2>&1 &

echo Capturing logs in background...
echo Filtering for telemetry messages:
echo.

adb logcat -s Unity:W Unity:E | findstr /C:"TELEMETRY" /C:"SEGMENTATION" /C:"FRAME TRACE" /C:"SERVER SEND"

echo.
echo ========================================
echo Log capture complete!
echo Full log saved to: %LOGFILE%
echo ========================================
