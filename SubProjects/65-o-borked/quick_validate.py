#!/usr/bin/env python3
"""
Quick validation script for rapid testing during development.
This provides a fast way to test the model without running a full validation.
"""

import os
import sys
import argparse
from datetime import datetime

try:
    from validate_patcher import ROMValidator
    from patch_rom import ROMPatcher
except ImportError as e:
    print(f"? Import error: {e}")
    print("   Make sure validate_patcher.py and patch_rom.py exist")
    sys.exit(1)

def quick_test(model_path="6502_span_predictor_best.pt", roms_dir="prg", num_tests=3):
    """Run a quick validation test with minimal output"""
    print("?? Quick Validation Test")
    print("=" * 40)
    print(f"? Started: {datetime.now().strftime('%H:%M:%S')}")
    
    try:
        # Initialize validator
        validator = ROMValidator(model_path, roms_dir)
        
        # Run quick test
        results = validator.run_validation(
            num_roms=2,  # Test only 2 ROMs
            num_holes_per_rom=num_tests,
            methods=['bidirectional'],  # Test only best method
            output_file=None
        )
        
        if results:
            avg_accuracy = sum(r['byte_accuracy'] for r in results) / len(results)
            avg_confidence = sum(r['avg_confidence'] for r in results) / len(results)
            
            print(f"\n?? Quick Test Results:")
            print(f"   Tests completed: {len(results)}")
            print(f"   Average accuracy: {avg_accuracy:.3f}")
            print(f"   Average confidence: {avg_confidence:.3f}")
            
            if avg_accuracy > 0.6:
                print("? Model appears to be working well!")
            elif avg_accuracy > 0.3:
                print("??  Model is working but accuracy could be improved")
            else:
                print("? Model accuracy is low - check training")
                
        else:
            print("? No test results obtained")
            
    except Exception as e:
        print(f"? Quick test failed: {e}")
        return False
        
    print(f"?? Completed: {datetime.now().strftime('%H:%M:%S')}")
    return True

def test_single_rom(rom_path, model_path="6502_span_predictor_best.pt"):
    """Test patching on a single specific ROM"""
    print(f"?? Testing single ROM: {os.path.basename(rom_path)}")
    print("=" * 50)
    
    try:
        # Initialize patcher directly
        patcher = ROMPatcher(model_path)
        
        # Load ROM
        with open(rom_path, 'rb') as f:
            rom_data = list(f.read())
            
        print(f"?? ROM size: {len(rom_data):,} bytes")
        
        # Create a synthetic hole in the middle
        hole_size = 16
        hole_start = len(rom_data) // 2
        hole_end = hole_start + hole_size
        
        # Store original bytes
        original_bytes = rom_data[hole_start:hole_end]
        
        # Create corrupted version
        corrupted_rom = rom_data.copy()
        for i in range(hole_start, hole_end):
            corrupted_rom[i] = 0
            
        print(f"???  Created {hole_size}-byte hole at position {hole_start:04X}")
        print(f"?? Original bytes: {' '.join(f'{b:02X}' for b in original_bytes)}")
        
        # Test bidirectional patching
        predicted_bytes, confidence = patcher.patch_hole(
            corrupted_rom, hole_start, hole_end, method='bidirectional'
        )
        
        print(f"?? Predicted bytes: {' '.join(f'{b:02X}' for b in predicted_bytes)}")
        print(f"?? Confidence: {' '.join(f'{c:.2f}' for c in confidence)}")
        
        # Calculate accuracy
        matches = sum(1 for orig, pred in zip(original_bytes, predicted_bytes) if orig == pred)
        accuracy = matches / len(original_bytes)
        avg_confidence = sum(confidence) / len(confidence)
        
        print(f"\n?? Results:")
        print(f"   Exact matches: {matches}/{len(original_bytes)}")
        print(f"   Accuracy: {accuracy:.3f}")
        print(f"   Avg confidence: {avg_confidence:.3f}")
        
        if accuracy > 0.7:
            print("? Excellent accuracy!")
        elif accuracy > 0.4:
            print("? Good accuracy")
        else:
            print("?? Low accuracy")
            
    except Exception as e:
        print(f"? Error testing ROM: {e}")
        return False
        
    return True

def benchmark_methods(model_path="6502_span_predictor_best.pt", roms_dir="prg"):
    """Quick benchmark of all patching methods"""
    print("?? Method Benchmark")
    print("=" * 40)
    
    try:
        validator = ROMValidator(model_path, roms_dir)
        
        methods = ['forward', 'backward', 'bidirectional']
        results = validator.run_validation(
            num_roms=1,
            num_holes_per_rom=5,
            methods=methods,
            output_file=None
        )
        
        if results:
            # Group by method
            method_results = {}
            for result in results:
                method = result['method']
                if method not in method_results:
                    method_results[method] = []
                method_results[method].append(result['byte_accuracy'])
            
            print(f"\n?? Method Comparison:")
            for method in methods:
                if method in method_results:
                    avg_acc = sum(method_results[method]) / len(method_results[method])
                    print(f"   {method:12s}: {avg_acc:.3f} accuracy")
                    
        else:
            print("? No benchmark results obtained")
            
    except Exception as e:
        print(f"? Benchmark failed: {e}")
        return False
        
    return True

def main():
    parser = argparse.ArgumentParser(description="Quick validation tests for ROM patching")
    parser.add_argument("--model", default="6502_span_predictor_best.pt", help="Path to model")
    parser.add_argument("--roms-dir", default="prg", help="ROM directory")
    parser.add_argument("--mode", choices=['quick', 'single', 'benchmark'], default='quick',
                       help="Test mode: quick test, single ROM, or method benchmark")
    parser.add_argument("--rom", help="Specific ROM file to test (for single mode)")
    parser.add_argument("--tests", type=int, default=3, help="Number of tests for quick mode")
    
    args = parser.parse_args()
    
    print("?? Quick ROM Patcher Validation")
    print(f"?? {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print()
    
    success = False
    
    if args.mode == 'quick':
        success = quick_test(args.model, args.roms_dir, args.tests)
        
    elif args.mode == 'single':
        if not args.rom:
            print("? --rom argument required for single mode")
            return 1
        if not os.path.exists(args.rom):
            print(f"? ROM file not found: {args.rom}")
            return 1
        success = test_single_rom(args.rom, args.model)
        
    elif args.mode == 'benchmark':
        success = benchmark_methods(args.model, args.roms_dir)
    
    return 0 if success else 1

if __name__ == "__main__":
    exit(main())