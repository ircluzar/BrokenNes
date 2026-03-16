import torch
import sys

print(f"Python version: {sys.version}")
print(f"PyTorch version: {torch.__version__}")
print(f"CUDA available: {torch.cuda.is_available()}")

if torch.cuda.is_available():
    print(f"CUDA version: {torch.version.cuda}")
    print(f"Device count: {torch.cuda.device_count()}")
    print(f"Current device: {torch.cuda.current_device()}")
    print(f"Device name: {torch.cuda.get_device_name(0)}")
    print(f"Device capability: {torch.cuda.get_device_capability(0)}")
else:
    print("CUDA is not available. Possible reasons:")
    print("1. PyTorch was installed without CUDA support")
    print("2. NVIDIA drivers are not installed or outdated")
    print("3. CUDA toolkit version mismatch")

# Test creating a tensor on GPU
try:
    if torch.cuda.is_available():
        x = torch.tensor([1.0]).cuda()
        print(f"Successfully created tensor on GPU: {x.device}")
    else:
        print("Cannot test GPU tensor creation - CUDA not available")
except Exception as e:
    print(f"Error creating GPU tensor: {e}")