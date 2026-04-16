#!/bin/bash
# GPU Inference Test Script
# Tests whether YOLO is using GPU correctly and measures performance

echo "============================================================"
echo "GPU Inference Test - YOLO Performance Check"
echo "============================================================"
echo ""

# Check if Python3 is available
if ! command -v python3 &> /dev/null; then
    echo "❌ Python3 not found!"
    exit 1
fi

echo "Running GPU inference test..."
echo ""

python3 << 'EOF'
import torch
from ultralytics import YOLO
import numpy as np
import time

print("=" * 60)
print("1. PyTorch & CUDA Configuration")
print("=" * 60)
print(f"PyTorch version: {torch.__version__}")
print(f"CUDA available: {torch.cuda.is_available()}")

if torch.cuda.is_available():
    print(f"CUDA version: {torch.version.cuda}")
    print(f"GPU count: {torch.cuda.device_count()}")
    for i in range(torch.cuda.device_count()):
        print(f"GPU {i}: {torch.cuda.get_device_name(i)}")
    print("")
else:
    print("\n❌ CUDA NOT AVAILABLE - YOLO will run on CPU only!")
    print("This explains the slow inference times.")
    exit(1)

print("=" * 60)
print("2. Loading YOLO Model")
print("=" * 60)

try:
    model = YOLO("yolo11n.pt")
    print("✓ YOLO11n model loaded")
except:
    try:
        model = YOLO("yolov8n.pt")
        print("✓ YOLOv8n model loaded (fallback)")
    except Exception as e:
        print(f"❌ Failed to load YOLO model: {e}")
        exit(1)

# Check initial device
initial_device = next(model.model.parameters()).device
print(f"Model device (initial): {initial_device}")

# Move to GPU
model.to('cuda:0')
gpu_device = next(model.model.parameters()).device
print(f"Model device (after .to('cuda')): {gpu_device}")
print("")

print("=" * 60)
print("3. Performance Test")
print("=" * 60)

# Create test image (640x480 random noise)
test_image = np.random.randint(0, 255, (480, 640, 3), dtype=np.uint8)
print(f"Test image size: {test_image.shape}")
print("")

# Warmup (first inference is always slower)
print("Warming up GPU...")
_ = model(test_image, device='cuda', verbose=False)
print("✓ Warmup complete")
print("")

# GPU inference test (5 runs)
print("Testing GPU inference (5 runs)...")
gpu_times = []
for i in range(5):
    start = time.time()
    results = model(test_image, device='cuda', verbose=False)
    elapsed = (time.time() - start) * 1000
    gpu_times.append(elapsed)
    print(f"  Run {i+1}: {elapsed:.1f}ms")

gpu_avg = sum(gpu_times) / len(gpu_times)
print(f"✓ GPU average: {gpu_avg:.1f}ms")
print("")

# CPU inference test (3 runs for comparison)
print("Testing CPU inference (3 runs)...")
cpu_times = []
for i in range(3):
    start = time.time()
    results = model(test_image, device='cpu', verbose=False)
    elapsed = (time.time() - start) * 1000
    cpu_times.append(elapsed)
    print(f"  Run {i+1}: {elapsed:.1f}ms")

cpu_avg = sum(cpu_times) / len(cpu_times)
print(f"✓ CPU average: {cpu_avg:.1f}ms")
print("")

print("=" * 60)
print("4. Results Summary")
print("=" * 60)
print(f"GPU inference time: {gpu_avg:.1f}ms (average)")
print(f"CPU inference time: {cpu_avg:.1f}ms (average)")
print(f"Speedup: {cpu_avg/gpu_avg:.1f}x faster on GPU")
print("")

if gpu_avg > 100:
    print("⚠️  WARNING: GPU inference is slower than expected!")
    print("   Expected: 20-50ms on RTX 4060")
    print(f"   Actual: {gpu_avg:.1f}ms")
    print("")
    print("Possible causes:")
    print("  1. GPU in power-saving mode (check nvidia-smi)")
    print("  2. GPU throttling due to temperature")
    print("  3. Multiple processes competing for GPU")
    print("  4. CUDA driver version mismatch")
elif gpu_avg > 50:
    print("⚠️  GPU inference is acceptable but could be better")
    print(f"   Expected: 20-50ms, Got: {gpu_avg:.1f}ms")
else:
    print("✓ GPU inference is performing well!")

print("")
print("=" * 60)
print("5. Recommendations")
print("=" * 60)

# Check server code configuration
print("\nTo ensure your server uses GPU:")
print("")
print("1. Set GPU persistence mode:")
print("   sudo nvidia-smi -pm 1")
print("")
print("2. In your server code (app/inference_yolo.py or app/routes/segmentation.py):")
print("   Ensure you have:")
print("")
print("   DEVICE = torch.device('cuda' if torch.cuda.is_available() else 'cpu')")
print("   model.to(DEVICE)")
print("   results = model(image, device='cuda', verbose=False)")
print("")
print("3. Check current server inference device:")
print("   Look for '[YOLO] Using device: cuda' in server logs")
print("")

if cpu_avg > 500:
    print(f"⚠️  Your current server logs show {cpu_avg:.0f}ms+ inference times")
    print("   This matches CPU inference, NOT GPU!")
    print("   Your server is likely running on CPU.")
    print("")
    print("   Action needed: Force GPU usage in server code")

print("=" * 60)
print("Test Complete")
print("=" * 60)
EOF

echo ""
echo "Test finished. Check results above."
echo ""
