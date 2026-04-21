#!/usr/bin/env python3
"""
Pull telemetry CSV files from Quest to PC.

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
REMOTE_PATH = f"/sdcard/Android/data/{PACKAGE_NAME}/files/"

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
    """List telemetry files on Quest"""
    print_color("Checking for telemetry files on Quest...", Colors.YELLOW)

    cmd = ["adb", "shell", "ls", "-lh", f"{REMOTE_PATH}telemetry_*.csv"]
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0 or "No such file" in result.stderr:
        print_color("No telemetry files found on Quest.", Colors.RED)
        print()
        print_color("Make sure:", Colors.YELLOW)
        print_color("  1. App has been run on Quest", Colors.YELLOW)
        print_color("  2. Server inference mode is enabled", Colors.YELLOW)
        print_color("  3. At least one inference session completed", Colors.YELLOW)
        print()
        print_color("Check Unity logs with:", Colors.CYAN)
        print_color("  adb logcat -s Unity | grep 'LOCAL TELEMETRY'", Colors.CYAN)
        return []

    print_color("Available files on Quest:", Colors.GREEN)
    print(result.stdout)

    # Extract filenames
    files = []
    for line in result.stdout.split("\n"):
        if "telemetry_" in line:
            parts = line.split()
            if len(parts) > 0:
                filename = parts[-1]  # Last column is filename
                files.append(filename)

    print_color(f"Found {len(files)} telemetry file(s)", Colors.GREEN)
    print()

    return files

def pull_files(output_dir):
    """Pull all telemetry files to PC"""
    # Create output directory
    Path(output_dir).mkdir(parents=True, exist_ok=True)

    print_color(f"Pulling files to: {os.path.abspath(output_dir)}", Colors.YELLOW)

    # Pull files
    cmd = ["adb", "pull", f"{REMOTE_PATH}telemetry_*.csv", output_dir]
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode == 0:
        print()
        print_color("✓ Pull completed!", Colors.GREEN)
        print()
    else:
        print_color("ERROR: Pull failed", Colors.RED)
        print(result.stderr)
        return False

    return True

def list_local_files(output_dir):
    """List pulled files in local directory"""
    print_color("Pulled files:", Colors.CYAN)

    files = sorted(Path(output_dir).glob("telemetry_*.csv"))

    if not files:
        print_color("  (none)", Colors.YELLOW)
        return

    print(f"{'Filename':<50} {'Size (KB)':<12} {'Modified'}")
    print("-" * 80)

    for file in files:
        size_kb = file.stat().st_size / 1024
        mtime = file.stat().st_mtime
        from datetime import datetime
        mtime_str = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M:%S")

        print(f"{file.name:<50} {size_kb:>10.2f}  {mtime_str}")

    print()
    print_color(f"Files saved to: {os.path.abspath(output_dir)}", Colors.GREEN)
    print()

def delete_remote_files():
    """Ask user if they want to delete files from Quest"""
    print_color("Delete telemetry files from Quest to free space? (y/N): ", Colors.YELLOW, end="")
    response = input().strip().lower()

    if response == "y":
        print_color("Deleting files on Quest...", Colors.YELLOW)
        cmd = ["adb", "shell", "rm", f"{REMOTE_PATH}telemetry_*.csv"]
        result = subprocess.run(cmd, capture_output=True, text=True)

        if result.returncode == 0:
            print_color("✓ Files deleted from Quest", Colors.GREEN)
        else:
            print_color("ERROR: Failed to delete files", Colors.RED)
            print(result.stderr)
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

    # Pull files
    if not pull_files(output_dir):
        return 1

    # List pulled files
    list_local_files(output_dir)

    # Inform user about Excel
    print_color("You can now open these CSV files in Excel for analysis.", Colors.CYAN)
    print()

    # Ask if user wants to delete files from Quest
    delete_remote_files()

    print()
    print_color("Done!", Colors.GREEN, bold=True)

    return 0

if __name__ == "__main__":
    sys.exit(main())
