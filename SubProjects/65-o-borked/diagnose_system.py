#!/usr/bin/env python3
"""
Diagnostic script for the NES ROM hole reconstruction system.
This script checks for common issues and provides troubleshooting guidance.
"""

import os
import sys
import glob
import traceback
from datetime import datetime

def check_file_exists(filepath, description="File"):
    """Check if a file exists and report status"""
    if os.path.exists(filepath):
        size_mb = os.path.getsize(filepath) / (1024 * 1024)
        print(f"? {description}: {filepath} ({size_mb:.1f} MB)")
        return True
    else:
        print(f"? {description}: {filepath} - NOT FOUND")
        return False

def check_python_imports():
    """Check if all required Python packages can be imported"""
    print("?? Checking Python Dependencies...")
    
    required_packages = [
        ('torch', 'PyTorch'),
        ('numpy', 'NumPy'),
        ('tqdm', 'tqdm'),
    ]
    
    all_good = True
    
    for package, name in required_packages:
        try:
            module = __import__(package)
            if hasattr(module, '__version__'):
                print(f"? {name}: {module.__version__}")
            else:
                print(f"? {name}: Available")
        except ImportError:
            print(f"? {name}: NOT INSTALLED")
            all_good = False
    
    # Special check for CUDA
    try:
        import torch
        if torch.cuda.is_available():
            print(f"? CUDA: Available ({torch.version.cuda})")
            print(f"   GPU: {torch.cuda.get_device_name(0)}")
        else:
            print("??  CUDA: Not available (will use CPU)")
    except:
        pass
    
    return all_good

def check_project_files():
    """Check if all project files exist"""
    print("\n?? Checking Project Files...")
    
    required_files = [
        ('train_6502_predictor.py', 'Training script'),
        ('patch_rom.py', 'ROM patching utility'),
        ('validate_patcher.py', 'Validation script'),
        ('test_system.py', 'System test script'),
        ('quick_validate.py', 'Quick validation script'),
    ]
    
    all_good = True
    for filepath, description in required_files:
        if not check_file_exists(filepath, description):
            all_good = False
    
    return all_good

def check_training_data():
    """Check training data availability"""
    print("\n?? Checking Training Data...")
    
    prg_dir = "prg"
    if not os.path.exists(prg_dir):
        print(f"? PRG directory not found: {prg_dir}")
        return False
    
    prg_files = glob.glob(os.path.join(prg_dir, "*.prg"))
    
    if len(prg_files) == 0:
        print(f"? No PRG files found in {prg_dir}")
        print("   Run the C# extractor: dotnet run")
        return False
    
    total_size = sum(os.path.getsize(f) for f in prg_files)
    print(f"? Found {len(prg_files)} PRG files")
    print(f"   Total size: {total_size / (1024*1024):.1f} MB")
    
    if len(prg_files) < 10:
        print("??  Less than 10 PRG files - consider adding more ROMs")
    
    if total_size < 10 * 1024 * 1024:  # Less than 10MB
        print("??  Training data is quite small - more data recommended")
    
    return True

def check_models():
    """Check for trained models"""
    print("\n?? Checking Trained Models...")
    
    model_files = [
        "6502_predictor.pt",
        "6502_span_predictor.pt",
        "6502_span_predictor_best.pt"
    ]
    
    found_models = []
    for model_file in model_files:
        if check_file_exists(model_file, "Model"):
            found_models.append(model_file)
    
    if not found_models:
        print("??  No trained models found")
        print("   Run training: python train_6502_predictor.py")
        return False
    
    return True

def test_basic_functionality():
    """Test basic functionality of the system"""
    print("\n?? Testing Basic Functionality...")
    
    try:
        # Test imports
        print("  ?? Testing imports...")
        from train_6502_predictor import TransformerPredictor, VOCAB_SIZE
        print("    ? Training script imports OK")
        
        from patch_rom import ROMPatcher
        print("    ? Patching script imports OK")
        
        from validate_patcher import ROMValidator
        print("    ? Validation script imports OK")
        
        # Test model creation
        print("  ???  Testing model creation...")
        model = TransformerPredictor()
        param_count = sum(p.numel() for p in model.parameters())
        print(f"    ? Model created: {param_count:,} parameters")
        
        return True
        
    except Exception as e:
        print(f"    ? Error: {e}")
        traceback.print_exc()
        return False

def test_model_loading():
    """Test loading a trained model"""
    print("\n?? Testing Model Loading...")
    
    model_files = [
        "6502_span_predictor_best.pt",
        "6502_span_predictor.pt",
        "6502_predictor.pt"
    ]
    
    for model_file in model_files:
        if os.path.exists(model_file):
            try:
                print(f"  ?? Testing {model_file}...")
                from patch_rom import ROMPatcher
                patcher = ROMPatcher(model_file)
                print(f"    ? Model loaded successfully")
                return True
            except Exception as e:
                print(f"    ? Error loading {model_file}: {e}")
                continue
    
    print("  ??  No loadable models found")
    return False

def test_quick_patch():
    """Test a quick patch operation"""
    print("\n?? Testing Quick Patch...")
    
    # Find a PRG file to test with
    prg_files = glob.glob("prg/*.prg")
    if not prg_files:
        print("  ? No PRG files available for testing")
        return False
    
    test_rom = prg_files[0]
    
    model_files = [
        "6502_span_predictor_best.pt",
        "6502_span_predictor.pt"
    ]
    
    for model_file in model_files:
        if os.path.exists(model_file):
            try:
                print(f"  ?? Testing with {os.path.basename(test_rom)}...")
                
                from patch_rom import ROMPatcher
                patcher = ROMPatcher(model_file)
                
                # Load ROM and create a small test hole
                with open(test_rom, 'rb') as f:
                    rom_data = list(f.read())
                
                if len(rom_data) < 100:
                    print("    ??  ROM too small for testing")
                    continue
                
                # Create 8-byte hole in the middle
                hole_start = len(rom_data) // 2
                hole_end = hole_start + 8
                
                # Test patch
                predicted_bytes, confidence = patcher.patch_hole(
                    rom_data, hole_start, hole_end, method='bidirectional',
                    temperature=0.5, top_k=30
                )
                
                avg_confidence = sum(confidence) / len(confidence)
                print(f"    ? Patch test successful")
                print(f"       Predicted 8 bytes with {avg_confidence:.3f} avg confidence")
                
                return True
                
            except Exception as e:
                print(f"    ? Patch test failed: {e}")
                continue
    
    print("  ? No working models for patch testing")
    return False

def provide_recommendations(issues):
    """Provide recommendations based on found issues"""
    if not issues:
        return
    
    print("\n?? RECOMMENDATIONS:")
    print("=" * 50)
    
    if 'python_deps' in issues:
        print("?? Python Dependencies:")
        print("   Install required packages:")
        print("   pip install torch numpy tqdm")
        print()
    
    if 'project_files' in issues:
        print("?? Project Files:")
        print("   Some project files are missing.")
        print("   Re-download or regenerate the missing files.")
        print()
    
    if 'training_data' in issues:
        print("?? Training Data:")
        print("   No training data found.")
        print("   1. Place .nes ROM files in 'roms/' directory")
        print("   2. Run: dotnet run")
        print("   3. This will extract .prg files to 'prg/' directory")
        print()
    
    if 'models' in issues:
        print("?? Trained Models:")
        print("   No trained models found.")
        print("   Run training: python train_6502_predictor.py")
        print("   This will take 1-2 hours depending on hardware.")
        print()
    
    if 'functionality' in issues:
        print("?? Functionality:")
        print("   Basic functionality tests failed.")
        print("   Check for import errors or missing dependencies.")
        print()
    
    if 'model_loading' in issues:
        print("?? Model Loading:")
        print("   Cannot load trained models.")
        print("   Models may be corrupted or incompatible.")
        print("   Try retraining: python train_6502_predictor.py")
        print()

def main():
    print("?? NES ROM PATCHER - SYSTEM DIAGNOSTICS")
    print("=" * 60)
    print(f"?? {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print()
    
    issues = []
    
    # Run all checks
    if not check_python_imports():
        issues.append('python_deps')
    
    if not check_project_files():
        issues.append('project_files')
    
    if not check_training_data():
        issues.append('training_data')
    
    if not check_models():
        issues.append('models')
    
    if not test_basic_functionality():
        issues.append('functionality')
    
    if not test_model_loading():
        issues.append('model_loading')
    
    if not test_quick_patch():
        issues.append('patch_test')
    
    # Summary
    print("\n" + "=" * 60)
    print("?? DIAGNOSTIC SUMMARY")
    print("=" * 60)
    
    if not issues:
        print("? ALL SYSTEMS OPERATIONAL!")
        print("?? Your NES ROM patcher is ready to use.")
        print()
        print("?? Next steps:")
        print("   • Run validation: python validate_patcher.py --num-roms 5")
        print("   • Patch a ROM: python patch_rom.py input.prg output.prg 0x1000 0x1020")
    else:
        print(f"??  {len(issues)} ISSUE(S) FOUND:")
        for issue in issues:
            print(f"   • {issue}")
        
        provide_recommendations(issues)
    
    print("\n?? Diagnostics completed")
    return 0 if not issues else 1

if __name__ == "__main__":
    exit(main())