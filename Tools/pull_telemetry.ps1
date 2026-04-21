# Pull all telemetry CSV files from Quest to PC
# Usage: .\pull_telemetry.ps1 [output_directory]

param(
    [string]$OutputPath = "C:\Telemetry\"
)

$packageName = "com.samples.passthroughcamera"
$remotePath = "/sdcard/Android/data/$packageName/files/"

Write-Host "=== Quest Telemetry Retrieval ===" -ForegroundColor Cyan
Write-Host ""

# Create local directory if not exists
if (!(Test-Path -Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
    Write-Host "Created directory: $OutputPath" -ForegroundColor Green
}

# Check if Quest is connected
Write-Host "Checking Quest connection..." -ForegroundColor Yellow
$devicesOutput = adb devices 2>&1
$devices = $devicesOutput | Select-String "device$"

if ($devices.Count -eq 0) {
    Write-Host "ERROR: Quest not connected." -ForegroundColor Red
    Write-Host "Please connect Quest via USB or enable ADB over WiFi." -ForegroundColor Red
    Write-Host ""
    Write-Host "Run 'adb devices' to check connection status." -ForegroundColor Yellow
    exit 1
}

Write-Host "✓ Quest connected ($($devices.Count) device(s))" -ForegroundColor Green
Write-Host ""

# List files on Quest
Write-Host "Checking for telemetry files on Quest..." -ForegroundColor Yellow
$listCmd = "adb shell ls -lh $remotePath`telemetry_*.csv 2>&1"
$fileList = Invoke-Expression $listCmd

if ($fileList -match "No such file") {
    Write-Host "No telemetry files found on Quest." -ForegroundColor Red
    Write-Host ""
    Write-Host "Make sure:" -ForegroundColor Yellow
    Write-Host "  1. App has been run on Quest" -ForegroundColor Yellow
    Write-Host "  2. Server inference mode is enabled" -ForegroundColor Yellow
    Write-Host "  3. At least one inference session completed" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Check Unity logs with: adb logcat -s Unity | findstr `"LOCAL TELEMETRY`"" -ForegroundColor Cyan
    exit 1
}

# Display available files
Write-Host "Available files on Quest:" -ForegroundColor Green
Write-Host $fileList
Write-Host ""

# Count files
$fileCount = ($fileList | Select-String "telemetry_").Count
Write-Host "Found $fileCount telemetry file(s)" -ForegroundColor Green
Write-Host ""

# Pull files
Write-Host "Pulling files to: $OutputPath" -ForegroundColor Yellow
$pullCmd = "adb pull $remotePath`telemetry_*.csv `"$OutputPath`""
Invoke-Expression $pullCmd

Write-Host ""
Write-Host "✓ Pull completed!" -ForegroundColor Green
Write-Host ""

# List pulled files
Write-Host "Pulled files:" -ForegroundColor Cyan
Get-ChildItem -Path $OutputPath -Filter "telemetry_*.csv" |
    Select-Object Name, @{Name="Size (KB)";Expression={[math]::Round($_.Length/1KB, 2)}}, LastWriteTime |
    Format-Table -AutoSize

Write-Host "Files saved to: $OutputPath" -ForegroundColor Green
Write-Host ""
Write-Host "You can now open these CSV files in Excel for analysis." -ForegroundColor Cyan
Write-Host ""

# Ask if user wants to delete files on Quest
$delete = Read-Host "Delete telemetry files from Quest to free space? (y/N)"
if ($delete -eq "y" -or $delete -eq "Y") {
    Write-Host "Deleting files on Quest..." -ForegroundColor Yellow
    adb shell rm "$remotePath`telemetry_*.csv"
    Write-Host "✓ Files deleted from Quest" -ForegroundColor Green
} else {
    Write-Host "Files kept on Quest (you can delete manually later)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green
