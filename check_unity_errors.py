import re
import os

log_path = os.path.expanduser(r"C:\Users\user\AppData\Local\Unity\Editor\Editor.log")

with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Get last 1000 lines
recent_lines = lines[-1000:]

errors = []
for i, line in enumerate(recent_lines):
    if 'error CS' in line.lower() or ('Assets' in line and '.cs(' in line):
        # Get context (current line + next 2 lines)
        context = recent_lines[i:min(i+3, len(recent_lines))]
        errors.extend(context)

print("=== Unity Compilation Errors ===\n")
if errors:
    for line in errors[:30]:  # First 30 lines of errors
        print(line.rstrip())
else:
    print("No compilation errors found")
