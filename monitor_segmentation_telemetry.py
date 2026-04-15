#!/usr/bin/env python3
"""
Real-time Segmentation Telemetry Monitor
Parses adb logcat and highlights key telemetry events
"""

import subprocess
import re
import sys
from datetime import datetime

# ANSI color codes
class Colors:
    RED = '\033[91m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    MAGENTA = '\033[95m'
    CYAN = '\033[96m'
    WHITE = '\033[97m'
    BOLD = '\033[1m'
    END = '\033[0m'

def colorize(text, color):
    return f"{color}{text}{Colors.END}"

def print_header():
    print("=" * 80)
    print(colorize("Segmentation Telemetry Real-Time Monitor", Colors.BOLD + Colors.CYAN))
    print("=" * 80)
    print(f"Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("\nWaiting for Segmentation scene to start...")
    print("Press Ctrl+C to stop\n")
    print("=" * 80)

def parse_and_display(line):
    """Parse logcat line and display with colors"""

    # Check for key patterns
    if "SEGMENTATION INFERENCE RUN MANAGER START" in line:
        print(colorize("\n🚀 SEGMENTATION STARTED!", Colors.BOLD + Colors.GREEN))
        print("=" * 80)
        return

    if "Created FrameTrace" in line:
        # Extract frame info
        match = re.search(r'Created FrameTrace (\d+), unity_send_ts=(\d+), session_id=(.+)', line)
        if match:
            frame_id, ts, session = match.groups()
            ts_valid = "✅" if int(ts) > 0 else "❌"
            print(colorize(f"\n📝 Frame {frame_id} Created", Colors.BOLD + Colors.BLUE))
            print(f"   unity_send_ts: {ts} {ts_valid}")
            print(f"   session_id: {session[:20]}...")
        return

    if "MarkCompleted frame" in line:
        # Extract completion info
        match = re.search(r'MarkCompleted frame (\d+), state=(\w+), server_recv=(\d+), server_send=(\d+), unity_recv=(\d+)', line)
        if match:
            frame_id, state, srv_recv, srv_send, unity_recv = match.groups()
            srv_valid = "✅" if int(srv_recv) > 0 else "❌"
            print(colorize(f"✓ Frame {frame_id} Completed", Colors.GREEN))
            print(f"   state: {state}")
            print(f"   server_recv: {srv_recv} {srv_valid}")
            print(f"   server_send: {srv_send}")
            print(f"   unity_recv: {unity_recv}")
        return

    if "Set m_lastCompletedTrace to DISPLAYED" in line:
        # Extract displayed info
        match = re.search(r'DISPLAYED frame (\d+), state=(\w+), unity_send_ts=(\d+), server_recv=(\d+)', line)
        if match:
            frame_id, state, ts, srv_recv = match.groups()
            print(colorize(f"🎬 Frame {frame_id} Displayed & Saved", Colors.BOLD + Colors.MAGENTA))
            print(f"   state: {state}")
            print(f"   unity_send_ts: {ts}")
            print(f"   server_recv: {srv_recv}")
        return

    if "Sending delayed headers for frame" in line:
        # Extract delayed header info
        match = re.search(r'for frame (\d+), state=(\w+), unity_send_ts=(\d+), server_recv=(\d+)', line)
        if match:
            frame_id, state, ts, srv_recv = match.groups()
            print(colorize(f"📤 Sending Delayed Headers (Frame {frame_id})", Colors.CYAN))
            print(f"   state: {state}")
            print(f"   unity_send_ts: {ts}")
            print(f"   server_recv: {srv_recv}")
        return

    if "m_lastCompletedTrace is NULL" in line:
        # Extract frame that has no previous
        match = re.search(r'Frame (\d+):', line)
        if match:
            frame_id = match.group(1)
            print(colorize(f"⚠️  Frame {frame_id}: No Previous Frame (Expected for Frame 0)", Colors.YELLOW))
        return

    # Generic telemetry debug
    if "TELEMETRY DEBUG" in line:
        print(colorize(f"[DEBUG] {line.split('TELEMETRY DEBUG')[1].strip()}", Colors.WHITE))
        return

    # Server send
    if "SERVER SEND" in line and "Sending frame" in line:
        match = re.search(r'Sending frame (\d+)', line)
        if match:
            frame_id = match.group(1)
            print(colorize(f"→ Sending Frame {frame_id} to server", Colors.BLUE))
        return

    # Frame trace
    if "FRAME TRACE" in line and "completed" in line:
        match = re.search(r'Frame (\d+) completed', line)
        if match:
            frame_id = match.group(1)
            print(colorize(f"← Frame {frame_id} response received", Colors.GREEN))
        return

def main():
    print_header()

    # Clear logcat
    subprocess.run(['adb', 'logcat', '-c'], capture_output=True)

    # Start logcat monitoring
    process = subprocess.Popen(
        ['adb', 'logcat', '-s', 'Unity:W', 'Unity:E'],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1
    )

    try:
        frame_count = 0
        for line in process.stdout:
            line = line.strip()
            if not line:
                continue

            # Count frames
            if "Created FrameTrace" in line:
                frame_count += 1

            # Parse and display
            parse_and_display(line)

            # Summary every 10 frames
            if frame_count > 0 and frame_count % 10 == 0:
                print(colorize(f"\n📊 {frame_count} frames processed", Colors.BOLD + Colors.CYAN))
                print("=" * 80)

    except KeyboardInterrupt:
        print(colorize("\n\n✋ Monitoring stopped", Colors.YELLOW))
        print(colorize(f"Total frames captured: {frame_count}", Colors.BOLD))
        process.terminate()
        sys.exit(0)

if __name__ == "__main__":
    main()
