import os

log_path = os.path.expanduser(r"C:\Users\user\AppData\Local\Unity\Editor\Editor.log")

with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find "Build Failed" index
build_fail_idx = -1
for i in range(len(lines) - 1, max(len(lines) - 500, 0), -1):
    if 'Build Failed' in lines[i]:
        build_fail_idx = i
        break

if build_fail_idx == -1:
    print("No build failure found")
    exit()

# Get 150 lines before build failure
start_idx = max(0, build_fail_idx - 150)
context_lines = lines[start_idx:build_fail_idx + 20]

# Print lines with error/warning
print("=== Build Errors ===\n")
for line in context_lines:
    lower = line.lower()
    if any(keyword in lower for keyword in ['error', 'exception', 'failed', 'missing']):
        if 'error cs' in lower or 'exception:' in lower or 'build failed' in lower:
            print(line.rstrip())
