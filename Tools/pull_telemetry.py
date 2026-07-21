#!/usr/bin/env python3
"""
Pull telemetry CSV files from Quest to PC.
Uses two-step copy method to bypass Android 11+ scoped storage restrictions.

Usage:
    python pull_telemetry.py [output_directory]

Examples:
    python pull_telemetry.py                    # Pull to ./telemetry/
    python pull_telemetry.py C:\\Telemetry       # Pull to C:\Telemetry\
    python pull_telemetry.py ~/telemetry        # Pull to ~/telemetry/ (Linux/Mac)
"""

import subprocess
import sys
import os
from pathlib import Path

PACKAGE_NAME = "com.samples.passthroughcamera"
APP_PRIVATE_PATH = f"/storage/emulated/0/Android/data/{PACKAGE_NAME}/files/"
PUBLIC_PATH = "/sdcard/"

# ANSI color codes for terminal output
class Colors:
    CYAN = '\033[96m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    RESET = '\033[0m'
    BOLD = '\033[1m'

def print_color(text, color=Colors.RESET, bold=False):
    """Print colored text to terminal"""
    prefix = Colors.BOLD if bold else ""
    print(f"{prefix}{color}{text}{Colors.RESET}")

def check_adb():
    """Check if adb is available"""
    try:
        result = subprocess.run(["adb", "version"], capture_output=True, check=True, text=True)
        return True
    except (subprocess.CalledProcessError, FileNotFoundError):
        print_color("ERROR: adb not found.", Colors.RED)
        print_color("Please install Android Platform Tools:", Colors.YELLOW)
        print_color("  https://developer.android.com/tools/releases/platform-tools", Colors.YELLOW)
        return False

def check_device():
    """Check if Quest is connected"""
    print_color("Checking Quest connection...", Colors.YELLOW)

    result = subprocess.run(["adb", "devices"], capture_output=True, text=True)
    devices = [line for line in result.stdout.split("\n") if "\tdevice" in line]

    if len(devices) == 0:
        print_color("ERROR: No Quest device connected.", Colors.RED)
        print_color("", Colors.RESET)
        print_color("Please:", Colors.YELLOW)
        print_color("  1. Connect Quest via USB cable, or", Colors.YELLOW)
        print_color("  2. Enable ADB over WiFi (Developer Mode → Wireless ADB)", Colors.YELLOW)
        print_color("", Colors.RESET)
        print_color("Then run 'adb devices' to verify connection.", Colors.CYAN)
        return False

    print_color(f"✓ Quest connected ({len(devices)} device(s))", Colors.GREEN)
    print()
    return True

def list_remote_files():
    """List telemetry and epoch files on Quest"""
    print_color("Checking for telemetry files on Quest...", Colors.YELLOW)

    files = []
    any_output = False
    for pattern in ["telemetry_*.csv", "epoch_*.csv"]:
        cmd = ["adb", "shell", "ls", "-lh", f"{APP_PRIVATE_PATH}{pattern}"]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0 and result.stdout.strip():
            any_output = True
            print(result.stdout)
            keyword = "telemetry_" if "telemetry" in pattern else "epoch_"
            for line in result.stdout.split("\n"):
                if keyword in line:
                    parts = line.split()
                    if len(parts) > 0:
                        files.append(parts[-1])

    if not files:
        print_color("No telemetry or epoch files found on Quest.", Colors.RED)
        print()
        print_color("Make sure:", Colors.YELLOW)
        print_color("  1. App has been run on Quest", Colors.YELLOW)
        print_color("  2. Server inference mode is enabled", Colors.YELLOW)
        print_color("  3. At least one inference session completed", Colors.YELLOW)
        print()
        print_color("Check Unity logs with:", Colors.CYAN)
        print_color("  adb logcat -s Unity | grep 'TELEMETRY'", Colors.CYAN)
        return []

    print_color(f"Found {len(files)} file(s) on Quest", Colors.GREEN)
    print()
    return files

def copy_to_public():
    """Copy files from app private directory to public /sdcard/"""
    print_color("Step 1/3: Copying files to public directory on Quest...", Colors.YELLOW)

    # Copy both per-frame telemetry and per-epoch control-plane CSV files
    for pattern in ["telemetry_*.csv", "epoch_*.csv"]:
        cmd = ["adb", "shell", "cp", f"{APP_PRIVATE_PATH}{pattern}", PUBLIC_PATH]
        subprocess.run(cmd, capture_output=True, text=True)  # ignore missing pattern errors

    print_color("✓ Files copied to /sdcard/", Colors.GREEN)
    print()
    return True

def pull_files(output_dir):
    """Pull all telemetry files from /sdcard/ to PC"""
    # Create output directory
    Path(output_dir).mkdir(parents=True, exist_ok=True)

    print_color("Step 2/3: Pulling files from Quest to PC...", Colors.YELLOW)

    pulled_any = False
    for pattern in ["telemetry_*.csv", "epoch_*.csv"]:
        cmd = ["adb", "pull", f"{PUBLIC_PATH}{pattern}", output_dir]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0:
            pulled_any = True

    if not pulled_any:
        print_color("ERROR: Failed to pull any telemetry files from Quest", Colors.RED)
        return False

    print_color(f"✓ Files pulled to: {os.path.abspath(output_dir)}", Colors.GREEN)
    print()

    return True

def cleanup_public():
    """Clean up temporary files from /sdcard/"""
    print_color("Step 3/3: Cleaning up temporary files on Quest...", Colors.YELLOW)

    for pattern in ["telemetry_*.csv", "epoch_*.csv"]:
        subprocess.run(["adb", "shell", "rm", f"{PUBLIC_PATH}{pattern}"],
                       capture_output=True, text=True)

    print_color("✓ Temporary files removed from /sdcard/", Colors.GREEN)

    print()

def list_local_files(output_dir):
    """List pulled files in local directory"""
    print_color("✓ Pull completed successfully!", Colors.GREEN)
    print()

    print_color("Pulled files:", Colors.CYAN)

    files = sorted(
        list(Path(output_dir).glob("telemetry_*.csv")) +
        list(Path(output_dir).glob("epoch_*.csv"))
    )

    if not files:
        print_color("  (none)", Colors.YELLOW)
        return

    print(f"{'Filename':<60} {'Size (KB)':<12} {'Modified'}")
    print("-" * 90)

    for file in files:
        size_kb = file.stat().st_size / 1024
        mtime = file.stat().st_mtime
        from datetime import datetime
        mtime_str = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M:%S")

        print(f"{file.name:<60} {size_kb:>10.2f}  {mtime_str}")

    print()
    print_color(f"Files saved to: {os.path.abspath(output_dir)}", Colors.GREEN)
    print()

def delete_remote_files():
    """Ask user if they want to delete original files from Quest"""
    print_color("Delete original telemetry files from Quest to free space? (y/N): ", Colors.YELLOW, end="")
    response = input().strip().lower()

    if response == "y":
        print_color("Deleting files from Quest app directory...", Colors.YELLOW)
        for pattern in ["telemetry_*.csv", "epoch_*.csv"]:
            subprocess.run(["adb", "shell", "rm", f"{APP_PRIVATE_PATH}{pattern}"],
                           capture_output=True, text=True)
        print_color("✓ Files deleted from Quest", Colors.GREEN)
    else:
        print_color("Files kept on Quest (you can delete manually later)", Colors.YELLOW)

def main():
    # Get output directory from command line or use default
    output_dir = sys.argv[1] if len(sys.argv) > 1 else "./telemetry"

    print_color("=== Quest Telemetry Retrieval ===", Colors.CYAN, bold=True)
    print()

    # Checks
    if not check_adb():
        return 1

    if not check_device():
        return 1

    # List available files on Quest
    files = list_remote_files()

    if not files:
        return 1

    # ===========================================================================
    # TWO-STEP COPY METHOD (Android 11+ scoped storage workaround)
    # ===========================================================================

    # Step 1: Copy to public directory
    if not copy_to_public():
        return 1

    # Step 2: Pull files
    if not pull_files(output_dir):
        return 1

    # Step 3: Clean up /sdcard/
    cleanup_public()

    # ===========================================================================
    # DISPLAY RESULTS
    # ===========================================================================

    # List pulled files
    list_local_files(output_dir)

    # Inform user about Excel
    print_color("You can now open these CSV files in Excel for analysis.", Colors.CYAN)
    print()

    # ===========================================================================
    # OPTIONAL: DELETE FROM QUEST
    # ===========================================================================

    # Ask if user wants to delete original files from Quest
    delete_remote_files()

    print()
    print_color("Done!", Colors.GREEN, bold=True)

    return 0

if __name__ == "__main__":
    sys.exit(main())
