@echo off
echo.
echo ============================================
echo  延遲資訊顯示測試 (Latency Display Test)
echo ============================================
echo.
echo 請在 Quest 3 上手動啟動應用程式：
echo   1. 戴上頭盔
echo   2. 前往應用程式庫
echo   3. 找到 "PassthroughCameraApiSamples"
echo   4. 啟動應用程式
echo   5. 選擇 "MultiObjectDetection"
echo   6. 授予權限
echo   7. 按 Button A 或捏合手勢開始
echo   8. 將相機對準物體
echo.
echo 此視窗將顯示延遲資訊更新日誌
echo 您應該會在螢幕底部看到灰色面板顯示：
echo   - E2E Latency: XXXms
echo   - Upload, Server, Download, Parse 分解
echo   - 資料傳輸量
echo   - 平均信心度
echo.
echo 按 Ctrl+C 停止監控
echo.
echo ============================================
echo.

REM 清除舊日誌
adb logcat -c

REM 等待一下
timeout /t 2 /nobreak >nul

REM 顯示延遲相關日誌
adb logcat -s Unity | findstr /C:"[LATENCY]" /C:"[PANEL]" /C:"UpdateMetrics" /C:"E2E="
