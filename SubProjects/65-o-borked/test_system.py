#!/usr/bin/env python3
"""
Quick test script to verify that all required dependencies are available
and the system is ready for training and patching.
"""

import sys
import os

def test_imports():
    """Test that all required Python packages are available"""
    print("?? Testing Python dependencies...")
    
    try:
        import torch
        print(f"? PyTorch {torch.__version__} - Available")
        
        # Test CUDA availability
        if torch.cuda.is_available():
            print(f"?? CUDA {torch.version.cuda} - Available")
            print(f"   GPU: {torch.cuda.get_device_name(0)}")
            print(f"   Memory: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")
        else:
            print("?? CUDA - Not available (will use CPU)")
            
    except ImportError:
        print("? PyTorch - Not installed")
        print("   Install with: pip install torch")
        return False
    
    try:
        import numpy as np
        print(f"? NumPy {np.__version__} - Available")
    except ImportError:
        print("? NumPy - Not installed")
        print("   Install with: pip install numpy")
        return False
    
    try:
        from tqdm import tqdm
        print("? tqdm - Available")
    except ImportError:
        print("??  tqdm - Not installed (will auto-install during training)")
    
    return True

def test_data():
    """Test that training data is available"""
    print("\n?? Testing training data...")
    
    prg_dir = os.path.join(os.path.dirname(__file__), 'prg')
    
    if not os.path.exists(prg_dir):
        print(f"? PRG directory not found: {prg_dir}")
        print("   Run the C# extractor first to create PRG files")
        return False
        
    prg_files = [f for f in os.listdir(prg_dir) if f.endswith('.prg')]
    
    if len(prg_files) == 0:
        print(f"? No PRG files found in {prg_dir}")
        print("   Run the C# extractor first to create PRG files")
        return False
        
    total_size = sum(os.path.getsize(os.path.join(prg_dir, f)) for f in prg_files)
    
    print(f"? Found {len(prg_files)} PRG files")
    print(f"   Total data: {total_size / (1024*1024):.1f} MB")
    
    if len(prg_files) < 10:
        print("??  Less than 10 PRG files found - consider adding more for better training")
    
    return True

def test_model_files():
    """Check for existing model files"""
    print("\n?? Checking for existing models...")
    
    model_files = [
        '6502_predictor.pt',
        '6502_span_predictor.pt', 
        '6502_span_predictor_best.pt'
    ]
    
    found_models = []
    
    for model_file in model_files:
        if os.path.exists(model_file):
            size_mb = os.path.getsize(model_file) / (1024*1024)
            print(f"? Found {model_file} ({size_mb:.1f} MB)")
            found_models.append(model_file)
        else:
            print(f"?? {model_file} - Not found (will be created during training)")
    
    if found_models:
        print(f"?? {len(found_models)} existing model(s) found - training will create new versions")
    else:
        print("?? No existing models found - ready for fresh training")
    
    return True

def estimate_training_time():
    """Provide training time estimates"""
    print("\n? Training time estimates:")
    
    try:
        import torch
        
        if torch.cuda.is_available():
            gpu_name = torch.cuda.get_device_name(0).lower()
            
            if 'rtx 40' in gpu_name or 'rtx 30' in gpu_name:
                print("?? High-end GPU detected: ~30-60 minutes")
            elif 'rtx 20' in gpu_name or 'gtx 16' in gpu_name:
                print("? Mid-range GPU detected: ~1-2 hours")
            elif 'gtx' in gpu_name:
                print("?? Older GPU detected: ~2-4 hours")
            else:
                print("?? GPU detected: ~1-3 hours (depends on model)")
        else:
            print("?? CPU training: ~6-12 hours (not recommended)")
            print("   Consider using Google Colab or similar for GPU training")
            
    except ImportError:
        print("? Cannot estimate - PyTorch not available")

def main():
    print("=" * 60)
    print("?? NES ROM PATCHER - SYSTEM TEST")
    print("=" * 60)
    
    all_good = True
    
    # Test dependencies
    if not test_imports():
        all_good = False
    
    # Test training data
    if not test_data():
        all_good = False
        
    # Check model files
    test_model_files()
    
    # Training time estimate
    estimate_training_time()
    
    print("\n" + "=" * 60)
    
    if all_good:
        print("? SYSTEM READY!")
        print("?? You can now run:")
        print("   • python train_6502_predictor.py  (to train)")
        print("   • python validate_patcher.py      (to test)")
        print("   • python patch_rom.py             (to patch ROMs)")
    else:
        print("? SYSTEM NOT READY")
        print("?? Please fix the issues above before proceeding")
        
    print("=" * 60)
    
    return 0 if all_good else 1

if __name__ == "__main__":
    exit(main())