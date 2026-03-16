import os
import glob
import random
import torch
import numpy as np
from datetime import datetime
import argparse
import gc
import sys
try:
    from tqdm import tqdm
except ImportError:
    print("Installing tqdm for progress bars...")
    import subprocess
    import sys
    subprocess.check_call([sys.executable, "-m", "pip", "install", "tqdm"])
    from tqdm import tqdm

try:
    from patch_rom import ROMPatcher
except ImportError:
    print("? Error: Cannot import ROMPatcher from patch_rom.py")
    print("   Make sure patch_rom.py exists and contains the ROMPatcher class")
    sys.exit(1)

# Default model paths
DEFAULT_MODEL_PATH = "6502_span_predictor_best.pt"
EPOCHS_DIR = "epoch_models"

def get_model_info(model_path):
    """
    Get information about a saved model.
    
    Args:
        model_path: Path to the model file
    
    Returns:
        dict: Model information
    """
    try:
        checkpoint = torch.load(model_path, map_location='cpu')
        info = {
            'path': model_path,
            'epoch': checkpoint.get('epoch', 'Unknown'),
            'loss': checkpoint.get('loss', 'Unknown'),
            'accuracy': checkpoint.get('accuracy', 'Unknown'),
            'best_accuracy': checkpoint.get('best_accuracy', 'Unknown'),
            'file_size_mb': os.path.getsize(model_path) / (1024 * 1024)
        }
        
        # Extract config information if available
        config = checkpoint.get('config', {})
        if config:
            info.update({
                'seq_len': config.get('seq_len', 'Unknown'),
                'vocab_size': config.get('vocab_size', 'Unknown'),
                'embed_size': config.get('embed_size', 'Unknown'),
                'num_layers': config.get('num_layers', 'Unknown'),
                'num_heads': config.get('num_heads', 'Unknown')
            })
        
        return info
    except Exception as e:
        return {'error': str(e), 'path': model_path}

def get_model_path(model_arg, epoch=None):
    """
    Determine the model path based on arguments.
    
    Args:
        model_arg: Model path argument from command line
        epoch: Epoch number (if specified)
    
    Returns:
        str: Path to the model file
    """
    if epoch is not None:
        # Use epoch-specific model
        epoch_model_path = os.path.join(EPOCHS_DIR, f"6502_span_predictor_epoch{epoch}.pt")
        if os.path.exists(epoch_model_path):
            return epoch_model_path
        else:
            print(f"? Epoch model not found: {epoch_model_path}")
            print(f"?? Available epoch models:")
            if os.path.exists(EPOCHS_DIR):
                epoch_files = [f for f in os.listdir(EPOCHS_DIR) if f.startswith('6502_span_predictor_epoch') and f.endswith('.pt')]
                if epoch_files:
                    epoch_numbers = []
                    for f in sorted(epoch_files):
                        try:
                            # Extract epoch number from filename
                            epoch_num = int(f.replace('6502_span_predictor_epoch', '').replace('.pt', ''))
                            epoch_numbers.append(epoch_num)
                            print(f"   • Epoch {epoch_num}: {f}")
                        except ValueError:
                            continue
                    if epoch_numbers:
                        print(f"   Available epochs: {min(epoch_numbers)}-{max(epoch_numbers)}")
                else:
                    print("   • No epoch models found in epoch_models directory")
            else:
                print("   • epoch_models directory does not exist")
            sys.exit(1)
    else:
        # Use specified model path or default
        return model_arg if model_arg != DEFAULT_MODEL_PATH else DEFAULT_MODEL_PATH

class NESROMInfo:
    """Parse and handle iNES ROM header information"""
    
    def __init__(self, rom_data):
        if len(rom_data) < 16:
            raise ValueError("ROM file too small to contain iNES header")
        
        self.header = rom_data[:16]
        
        # Validate iNES magic
        if not (self.header[0] == ord('N') and self.header[1] == ord('E') and 
                self.header[2] == ord('S') and self.header[3] == 0x1A):
            raise ValueError("Invalid iNES header magic")
        
        self.prg_size = self.header[4] * 16 * 1024  # PRG ROM size in bytes
        self.chr_size = self.header[5] * 8 * 1024   # CHR ROM size in bytes
        self.has_trainer = (self.header[6] & 0x04) != 0
        
        # Calculate offsets
        self.offset = 16
        if self.has_trainer:
            self.offset += 512
            
        self.prg_start = self.offset
        self.prg_end = self.offset + self.prg_size
        self.chr_start = self.prg_end
        self.chr_end = self.chr_start + self.chr_size
        
        # Validate ROM size
        if len(rom_data) < self.prg_end:
            raise ValueError(f"ROM file too small: expected at least {self.prg_end} bytes, got {len(rom_data)}")
    
    def get_prg_data(self, rom_data):
        """Extract PRG ROM data"""
        return rom_data[self.prg_start:self.prg_end]
    
    def set_prg_data(self, rom_data, prg_data):
        """Replace PRG ROM data in the full ROM"""
        result = bytearray(rom_data)
        result[self.prg_start:self.prg_end] = prg_data
        return bytes(result)

class ROMValidator:
    """Validation system for ROM hole patching using synthetic corruption"""
    
    def __init__(self, model_path, epoch=None):
        self.results = []
        
        # Determine the actual model path
        actual_model_path = get_model_path(model_path, epoch)
        
        print(f"?? Loading model: {actual_model_path}")
        if epoch is not None:
            print(f"   Using epoch {epoch} model")
        
        # Display model information
        model_info = get_model_info(actual_model_path)
        if 'error' not in model_info:
            print(f"?? Model information:")
            if model_info.get('epoch') != 'Unknown':
                print(f"   • Epoch: {model_info['epoch']}")
            if model_info.get('accuracy') != 'Unknown':
                print(f"   • Training accuracy: {model_info['accuracy']:.4f}")
            if model_info.get('loss') != 'Unknown':
                print(f"   • Training loss: {model_info['loss']:.6f}")
            print(f"   • File size: {model_info['file_size_mb']:.1f} MB")
            if model_info.get('seq_len') != 'Unknown':
                print(f"   • Sequence length: {model_info['seq_len']}")
            if model_info.get('num_layers') != 'Unknown':
                print(f"   • Transformer layers: {model_info['num_layers']}")
        else:
            print(f"   ?? Could not read model info: {model_info['error']}")
        
        # Initialize the patcher with error handling
        try:
            self.patcher = ROMPatcher(actual_model_path)
            print(f"? ROMPatcher initialized successfully")
        except Exception as e:
            print(f"? Failed to initialize ROMPatcher: {e}")
            raise
        
    def create_intensity_holes(self, prg_data, intensity=1):
        """
        Create holes in PRG data based on intensity level
        
        Args:
            prg_data: PRG ROM data
            intensity: 1-10 scale where:
                1-3: Small holes (8-16 bytes), few holes
                4-6: Medium holes (16-24 bytes), moderate holes  
                7-8: Large holes (24-32 bytes), many holes
                9-10: Very large holes (32-64 bytes), extensive damage
        """
        intensity = max(1, min(10, intensity))  # Clamp to 1-10
        prg_data = list(prg_data) if isinstance(prg_data, bytes) else prg_data.copy()
        
        # Scale parameters based on intensity
        if intensity <= 3:
            hole_size_range = (8, 16)
            num_holes_base = 1
            num_holes_var = 1
        elif intensity <= 6:
            hole_size_range = (16, 24)
            num_holes_base = 2
            num_holes_var = 2
        elif intensity <= 8:
            hole_size_range = (24, 32)
            num_holes_base = 3
            num_holes_var = 3
        else:  # 9-10
            hole_size_range = (32, 64)
            num_holes_base = 4
            num_holes_var = 4
        
        # Calculate number of holes
        num_holes = num_holes_base + random.randint(0, num_holes_var)
        
        holes_info = []
        corrupted_prg = prg_data.copy()
        
        # Create multiple non-overlapping holes
        for hole_idx in range(num_holes):
            # Choose random hole size
            hole_size = random.randint(*hole_size_range)
            max_start = len(prg_data) - hole_size
            
            if max_start <= 0:
                continue
                
            # Find non-overlapping position
            attempts = 0
            while attempts < 50:  # Prevent infinite loop
                hole_start = random.randint(0, max_start)
                hole_end = hole_start + hole_size
                
                # Check if it overlaps with existing holes
                overlaps = False
                for existing_start, existing_end, _, _ in holes_info:
                    if not (hole_end <= existing_start or hole_start >= existing_end):
                        overlaps = True
                        break
                
                if not overlaps:
                    break
                attempts += 1
            
            if attempts >= 50:
                continue  # Skip if can't find non-overlapping position
            
            # Store original bytes
            original_bytes = prg_data[hole_start:hole_end]
            
            # Create corruption
            corruption_type = random.choice(['zeros', 'random', 'pattern', 'ff'])
            
            if corruption_type == 'zeros':
                for i in range(hole_start, hole_end):
                    corrupted_prg[i] = 0
            elif corruption_type == 'random':
                for i in range(hole_start, hole_end):
                    corrupted_prg[i] = random.randint(0, 255)
            elif corruption_type == 'ff':
                for i in range(hole_start, hole_end):
                    corrupted_prg[i] = 0xFF
            else:  # pattern corruption
                pattern = random.randint(0, 255)
                for i in range(hole_start, hole_end):
                    corrupted_prg[i] = pattern
            
            holes_info.append((hole_start, hole_end, original_bytes, corruption_type))
        
        return corrupted_prg, holes_info
    
    def calculate_byte_differences(self, original_prg, patched_prg):
        """Calculate detailed byte difference statistics"""
        original_prg = np.array(original_prg)
        patched_prg = np.array(patched_prg)
        
        if len(original_prg) != len(patched_prg):
            return {
                'error': 'Size mismatch',
                'original_size': len(original_prg),
                'patched_size': len(patched_prg)
            }
        
        # Calculate differences
        differences = original_prg != patched_prg
        diff_count = differences.sum()
        total_bytes = len(original_prg)
        
        # Calculate byte value differences
        byte_diffs = np.abs(original_prg.astype(int) - patched_prg.astype(int))
        
        # Similarity metrics
        exact_match_rate = 1.0 - (diff_count / total_bytes)
        avg_byte_diff = byte_diffs.mean()
        max_byte_diff = byte_diffs.max()
        
        # Bit-level differences
        bit_diffs = 0
        for orig, patch in zip(original_prg, patched_prg):
            bit_diffs += bin(orig ^ patch).count('1')
        
        bit_accuracy = 1.0 - (bit_diffs / (total_bytes * 8))
        
        return {
            'total_bytes': total_bytes,
            'different_bytes': int(diff_count),
            'exact_match_rate': float(exact_match_rate),
            'avg_byte_difference': float(avg_byte_diff),
            'max_byte_difference': int(max_byte_diff),
            'bit_accuracy': float(bit_accuracy),
            'total_bit_differences': bit_diffs
        }
    
    def calculate_metrics(self, original_bytes, predicted_bytes, confidence_scores):
        """Calculate various accuracy metrics"""
        original_bytes = np.array(original_bytes)
        predicted_bytes = np.array(predicted_bytes)
        confidence_scores = np.array(confidence_scores)
        
        if len(original_bytes) == 0 or len(predicted_bytes) == 0:
            return self._empty_metrics()
        
        # Ensure arrays are same length
        min_len = min(len(original_bytes), len(predicted_bytes), len(confidence_scores))
        original_bytes = original_bytes[:min_len]
        predicted_bytes = predicted_bytes[:min_len]
        confidence_scores = confidence_scores[:min_len]
        
        # Exact byte accuracy
        exact_matches = (original_bytes == predicted_bytes)
        byte_accuracy = exact_matches.mean()
        
        # Hamming distance (bit-level accuracy)
        bit_errors = 0
        total_bits = len(original_bytes) * 8;
        
        for orig, pred in zip(original_bytes, predicted_bytes):
            bit_errors += bin(orig ^ pred).count('1')
            
        bit_accuracy = 1.0 - (bit_errors / total_bits)
        
        # Confidence-weighted accuracy
        conf_sum = confidence_scores.sum()
        weighted_accuracy = (exact_matches * confidence_scores).sum() / conf_sum if conf_sum > 0 else 0
        
        # High-confidence accuracy (only predictions with confidence > 0.5)
        high_conf_mask = confidence_scores > 0.5
        high_conf_accuracy = exact_matches[high_conf_mask].mean() if high_conf_mask.sum() > 0 else 0
        
        # Near-miss accuracy (±1 byte value)
        near_misses = np.abs(original_bytes.astype(int) - predicted_bytes.astype(int)) <= 1
        near_miss_accuracy = near_misses.mean()
        
        return {
            'byte_accuracy': float(byte_accuracy),
            'bit_accuracy': float(bit_accuracy),
            'weighted_accuracy': float(weighted_accuracy),
            'high_conf_accuracy': float(high_conf_accuracy),
            'near_miss_accuracy': float(near_miss_accuracy),
            'avg_confidence': float(confidence_scores.mean()),
            'min_confidence': float(confidence_scores.min()),
            'max_confidence': float(confidence_scores.max()),
            'high_conf_count': int(high_conf_mask.sum()),
            'total_bytes': len(original_bytes)
        }
    
    def _empty_metrics(self):
        """Return empty metrics for error cases"""
        return {
            'byte_accuracy': 0.0,
            'bit_accuracy': 0.0,
            'weighted_accuracy': 0.0,
            'high_conf_accuracy': 0.0,
            'near_miss_accuracy': 0.0,
            'avg_confidence': 0.0,
            'min_confidence': 0.0,
            'max_confidence': 0.0,
            'high_conf_count': 0,
            'total_bytes': 0
        }
    
    def validate_opcode_structure(self, rom_bytes, start_pos, end_pos):
        """Enhanced validation of 6502 opcode structure"""
        # Complete list of valid 6502 opcodes
        valid_opcodes = {
            # ADC - Add with Carry
            0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79, 0x61, 0x71,
            # AND - Logical AND
            0x29, 0x25, 0x35, 0x2D, 0x3D, 0x39, 0x21, 0x31,
            # ASL - Arithmetic Shift Left
            0x0A, 0x06, 0x16, 0x0E, 0x1E,
            # BCC, BCS, BEQ, BMI, BNE, BPL, BVC, BVS - Branches
            0x90, 0xB0, 0xF0, 0x30, 0xD0, 0x10, 0x50, 0x70,
            # BIT - Bit Test
            0x24, 0x2C,
            # BRK - Break
            0x00,
            # CLC, CLD, CLI, CLV - Clear flags
            0x18, 0xD8, 0x58, 0xB8,
            # CMP - Compare
            0xC9, 0xC5, 0xD5, 0xCD, 0xDD, 0xD9, 0xC1, 0xD1,
            # CPX, CPY - Compare X/Y
            0xE0, 0xE4, 0xEC, 0xC0, 0xC4, 0xCC,
            # DEC - Decrement
            0xC6, 0xD6, 0xCE, 0xDE,
            # DEX, DEY - Decrement X/Y
            0xCA, 0x88,
            # EOR - Exclusive OR
            0x49, 0x45, 0x55, 0x4D, 0x5D, 0x59, 0x41, 0x51,
            # INC - Increment
            0xE6, 0xF6, 0xEE, 0xFE,
            # INX, INY - Increment X/Y
            0xE8, 0xC8,
            # JMP - Jump
            0x4C, 0x6C,
            # JSR - Jump to Subroutine
            0x20,
            # LDA - Load Accumulator
            0xA9, 0xA5, 0xB5, 0xAD, 0xBD, 0xB9, 0xA1, 0xB1,
            # LDX - Load X
            0xA2, 0xA6, 0xB6, 0xAE, 0xBE,
            # LDY - Load Y
            0xA0, 0xA4, 0xB4, 0xAC, 0xBC,
            # LSR - Logical Shift Right
            0x4A, 0x46, 0x56, 0x4E, 0x5E,
            # NOP - No Operation
            0xEA,
            # ORA - Logical OR
            0x09, 0x05, 0x15, 0x0D, 0x1D, 0x19, 0x01, 0x11,
            # PHA, PHP, PLA, PLP - Push/Pull
            0x48, 0x08, 0x68, 0x28,
            # ROL - Rotate Left
            0x2A, 0x26, 0x36, 0x2E, 0x3E,
            # ROR - Rotate Right
            0x6A, 0x66, 0x76, 0x6E, 0x7E,
            # RTI, RTS - Return
            0x40, 0x60,
            # SBC - Subtract with Carry
            0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9, 0xE1, 0xF1,
            # SEC, SED, SEI - Set flags
            0x38, 0xF8, 0x78,
            # STA - Store Accumulator
            0x85, 0x95, 0x8D, 0x9D, 0x99, 0x81, 0x91,
            # STX - Store X
            0x86, 0x96, 0x8E,
            # STY - Store Y
            0x84, 0x94, 0x8C,
            # TAX, TAY, TSX, TXA, TXS, TYA - Transfer
            0xAA, 0xA8, 0xBA, 0x8A, 0x9A, 0x98,
            # Unofficial/illegal opcodes (common ones)
            0x1A, 0x3A, 0x5A, 0x7A, 0xDA, 0xFA,  # NOP variants
            0x80, 0x82, 0x89, 0xC2, 0xE2,        # NOP with immediate
            0x04, 0x44, 0x64, 0x14, 0x34, 0x54, 0x74, 0xD4, 0xF4,  # NOP with zeropage
            0x0C, 0x1C, 0x3C, 0x5C, 0x7C, 0xDC, 0xFC,  # NOP with absolute
        }
        
        valid_count = 0
        total_count = 0
        
        # Check each byte in the patched region
        for i in range(start_pos, end_pos):
            if i < len(rom_bytes):
                total_count += 1
                if rom_bytes[i] in valid_opcodes:
                    valid_count += 1
                    
        return valid_count / total_count if total_count > 0 else 0
    
    def analyze_rom_patterns(self, rom_data, hole_start, hole_end):
        """Analyze patterns around the hole for better context"""
        analysis = {
            'likely_code': False,
            'likely_data': False,
            'has_patterns': False,
            'entropy': 0.0
        }
        
        try:
            # Look at context around hole
            context_size = 64
            context_start = max(0, hole_start - context_size)
            context_end = min(len(rom_data), hole_end + context_size)
            context = rom_data[context_start:context_end]
            
            if len(context) == 0:
                return analysis
                
            # Calculate entropy
            from collections import Counter
            byte_counts = Counter(context)
            total = len(context)
            entropy = -sum((count/total) * np.log2(count/total) for count in byte_counts.values())
            analysis['entropy'] = entropy
            
            # High entropy suggests code, low entropy suggests data/patterns
            if entropy > 6.0:
                analysis['likely_code'] = True
            elif entropy < 3.0:
                analysis['likely_data'] = True
                
            # Check for repeating patterns
            pattern_found = False
            for pattern_len in [2, 4, 8, 16]:
                if len(context) >= pattern_len * 2:
                    for i in range(len(context) - pattern_len * 2):
                        pattern = context[i:i+pattern_len]
                        if context[i+pattern_len:i+pattern_len*2] == pattern:
                            pattern_found = True
                            break
                if pattern_found:
                    break
                    
            analysis['has_patterns'] = pattern_found
            
        except Exception as e:
            print(f"   ?? Pattern analysis failed: {e}")
            
        return analysis
    
    def test_single_rom(self, nes_rom_path, intensity=5, methods=['forward', 'backward', 'bidirectional'], save_files=True):
        """Test patching on a single NES ROM with intensity-based corruption"""
        rom_name = os.path.basename(nes_rom_path)
        base_name = os.path.splitext(rom_name)[0]
        rom_dir = os.path.dirname(nes_rom_path)
        
        print(f"\n?? Testing ROM: {rom_name}")
        print(f"   Intensity level: {intensity}/10")
        
        # Load NES ROM with error handling
        try:
            with open(nes_rom_path, 'rb') as f:
                original_rom_data = f.read()
        except Exception as e:
            print(f"  ? Error loading ROM: {e}")
            return []
        
        # Parse iNES header
        try:
            nes_info = NESROMInfo(original_rom_data)
            print(f"   PRG ROM: {nes_info.prg_size:,} bytes")
            print(f"   CHR ROM: {nes_info.chr_size:,} bytes")
            print(f"   Trainer: {'Yes' if nes_info.has_trainer else 'No'}")
        except Exception as e:
            print(f"  ? Error parsing iNES header: {e}")
            return []
        
        if nes_info.prg_size < 1024:  # Skip very small PRG ROMs
            print(f"  ?? Skipping {rom_name}: PRG ROM too small ({nes_info.prg_size} bytes)")
            return []
        
        # Extract original PRG data
        original_prg = nes_info.get_prg_data(original_rom_data)
        
        # Create intensity-based corruption
        corrupted_prg, holes_info = self.create_intensity_holes(original_prg, intensity)
        
        if not holes_info:
            print(f"  ?? No holes could be created in {rom_name}")
            return []
        
        print(f"   Created {len(holes_info)} holes:")
        for i, (start, end, _, corruption_type) in enumerate(holes_info):
            print(f"     Hole {i+1}: {start:04X}-{end:04X} ({end-start} bytes, {corruption_type})")
        
        # Create corrupted ROM file
        corrupted_rom_data = nes_info.set_prg_data(original_rom_data, corrupted_prg)
        
        if save_files:
            corrupted_path = os.path.join(rom_dir, f"{base_name}.corrupted.nes")
            with open(corrupted_path, 'wb') as f:
                f.write(corrupted_rom_data)
            print(f"   ?? Corrupted ROM saved: {base_name}.corrupted.nes")
        
        rom_results = []
        patched_prg = corrupted_prg.copy()
        
        # Test each hole with each method
        hole_pbar = tqdm(holes_info, desc=f"  ?? Patching holes", leave=False)
        
        for hole_idx, (hole_start, hole_end, original_bytes, corruption_type) in enumerate(hole_pbar):
            hole_pbar.set_postfix_str(f"Hole {hole_idx+1}: {hole_start:04X}-{hole_end:04X}")
            
            # Analyze ROM patterns around hole
            pattern_analysis = self.analyze_rom_patterns(original_prg, hole_start, hole_end)
            
            # Test each method
            for method in methods:
                try:
                    # Patch the hole
                    predicted_bytes, confidence = self.patcher.patch_hole(
                        corrupted_prg, hole_start, hole_end, method=method, 
                        temperature=0.3, top_k=30
                    )
                    
                    # Apply patch to running patched version
                    for i, byte_val in enumerate(predicted_bytes):
                        if hole_start + i < len(patched_prg):
                            patched_prg[hole_start + i] = int(byte_val)
                    
                    # Calculate metrics
                    metrics = self.calculate_metrics(original_bytes, predicted_bytes, confidence)
                    
                    # Add opcode validation
                    opcode_validity = self.validate_opcode_structure(patched_prg, hole_start, hole_end)
                    
                    # Store result
                    result = {
                        'rom_name': rom_name,
                        'rom_size': len(original_rom_data),
                        'prg_size': nes_info.prg_size,
                        'intensity': intensity,
                        'hole_idx': hole_idx,
                        'hole_start': hole_start,
                        'hole_end': hole_end,
                        'hole_size': hole_end - hole_start,
                        'corruption_type': corruption_type,
                        'method': method,
                        'original_bytes': original_bytes,
                        'predicted_bytes': predicted_bytes.tolist() if hasattr(predicted_bytes, 'tolist') else list(predicted_bytes),
                        'confidence': confidence.tolist() if hasattr(confidence, 'tolist') else list(confidence),
                        'opcode_validity': opcode_validity,
                        'pattern_analysis': pattern_analysis,
                        **metrics
                    }
                    
                    rom_results.append(result)
                    
                except Exception as e:
                    print(f"    ? Error with method {method}: {e}")
                    continue
        
        hole_pbar.close()
        
        if rom_results:
            # Create final patched ROM
            final_patched_rom_data = nes_info.set_prg_data(original_rom_data, patched_prg)
            
            if save_files:
                patched_path = os.path.join(rom_dir, f"{base_name}.patched.nes")
                with open(patched_path, 'wb') as f:
                    f.write(final_patched_rom_data)
                print(f"   ?? Patched ROM saved: {base_name}.patched.nes")
            
            # Calculate overall ROM difference statistics
            rom_diff_stats = self.calculate_byte_differences(original_prg, patched_prg)
            
            # Print summary for this ROM
            avg_byte_acc = np.mean([r['byte_accuracy'] for r in rom_results])
            avg_conf = np.mean([r['avg_confidence'] for r in rom_results])
            avg_opcode = np.mean([r['opcode_validity'] for r in rom_results])
            
            print(f"  ? Results: {len(rom_results)} tests completed")
            print(f"     Avg byte accuracy: {avg_byte_acc:.3f}")
            print(f"     Avg confidence: {avg_conf:.3f}")
            print(f"     Avg opcode validity: {avg_opcode:.3f}")
            print(f"     Total different bytes: {rom_diff_stats['different_bytes']:,}/{rom_diff_stats['total_bytes']:,}")
            print(f"     Overall similarity: {rom_diff_stats['exact_match_rate']:.3f}")
            
            # Add ROM-wide statistics to each result
            for result in rom_results:
                result['rom_diff_stats'] = rom_diff_stats
        else:
            print(f"  ?? No successful tests for {rom_name}")
        
        # Force garbage collection to free memory
        gc.collect()
        
        return rom_results
    
    def generate_detailed_report(self, all_results, methods):
        """Generate a detailed analysis report"""
        if not all_results:
            return
            
        print("\n" + "=" * 70)
        print("?? DETAILED ANALYSIS REPORT")
        print("=" * 70)
        
        # Group results by method
        method_results = {}
        for result in all_results:
            method = result['method']
            if method not in method_results:
                method_results[method] = []
            method_results[method].append(result)
        
        # Analyze by intensity levels
        intensity_results = {}
        for result in all_results:
            intensity = result['intensity']
            if intensity not in intensity_results:
                intensity_results[intensity] = []
            intensity_results[intensity].append(result)
        
        print(f"\n?? Intensity Analysis:")
        for intensity in sorted(intensity_results.keys()):
            results = intensity_results[intensity]
            print(f"   Intensity {intensity}: {len(results)} tests")
            if results:
                print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in results]):.3f}")
                print(f"     Avg confidence: {np.mean([r['avg_confidence'] for r in results]):.3f}")
        
        # Analyze by ROM type (pattern analysis)
        code_results = [r for r in all_results if r['pattern_analysis']['likely_code']]
        data_results = [r for r in all_results if r['pattern_analysis']['likely_data']]
        pattern_results = [r for r in all_results if r['pattern_analysis']['has_patterns']]
        
        print(f"\n?? Pattern Analysis:")
        print(f"   Code regions: {len(code_results)} tests")
        if code_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in code_results]):.3f}")
        print(f"   Data regions: {len(data_results)} tests")
        if data_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in data_results]):.3f}")
        print(f"   Pattern regions: {len(pattern_results)} tests")
        if pattern_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in pattern_results]):.3f}")
        
        # Analyze by corruption type
        corruption_types = {}
        for result in all_results:
            corr_type = result['corruption_type']
            if corr_type not in corruption_types:
                corruption_types[corr_type] = []
            corruption_types[corr_type].append(result)
        
        print(f"\n?? Corruption Type Analysis:")
        for corr_type in sorted(corruption_types.keys()):
            results = corruption_types[corr_type]
            print(f"   {corr_type.title()}: {len(results)} tests")
            if results:
                print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in results]):.3f}")
        
        # Analyze by confidence levels
        high_conf_results = [r for r in all_results if r['avg_confidence'] > 0.7]
        med_conf_results = [r for r in all_results if 0.3 <= r['avg_confidence'] <= 0.7]
        low_conf_results = [r for r in all_results if r['avg_confidence'] < 0.3]
        
        print(f"\n?? Confidence Analysis:")
        print(f"   High confidence (>0.7): {len(high_conf_results)} tests")
        if high_conf_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in high_conf_results]):.3f}")
        print(f"   Medium confidence (0.3-0.7): {len(med_conf_results)} tests")
        if med_conf_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in med_conf_results]):.3f}")
        print(f"   Low confidence (<0.3): {len(low_conf_results)} tests")
        if low_conf_results:
            print(f"     Avg accuracy: {np.mean([r['byte_accuracy'] for r in low_conf_results]):.3f}")
    
    def run_validation(self, nes_rom_path, intensity=5, 
                      methods=['forward', 'backward', 'bidirectional'], 
                      output_file=None, epoch=None):
        """Run validation on a single NES ROM file"""
        print("=" * 70)
        print("?? NES ROM HOLE PATCHING VALIDATION")
        print("=" * 70)
        print(f"?? Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print(f"?? Target ROM: {nes_rom_path}")
        print(f"?? Intensity level: {intensity}/10")
        print(f"?? Methods: {', '.join(methods)}")
        if epoch is not None:
            print(f"?? Using epoch {epoch} model")
        
        # Validate input file
        if not os.path.exists(nes_rom_path):
            print(f"? ROM file not found: {nes_rom_path}")
            return []
        
        if not nes_rom_path.lower().endswith('.nes'):
            print(f"? File must be a .nes ROM: {nes_rom_path}")
            return []
        
        # Test the ROM
        all_results = []
        
        try:
            rom_results = self.test_single_rom(nes_rom_path, intensity, methods, save_files=True)
            all_results.extend(rom_results)
        except Exception as e:
            print(f"\n? Error testing ROM: {e}")
            import traceback
            traceback.print_exc()
            return []
        
        if not all_results:
            print("? No validation results obtained")
            return []
            
        # Analyze overall results
        print("\n" + "=" * 70)
        print("?? VALIDATION RESULTS")
        print("=" * 70)
        
        # Group results by method
        method_results = {}
        for result in all_results:
            method = result['method']
            if method not in method_results:
                method_results[method] = []
            method_results[method].append(result)
        
        # Print method-wise statistics
        for method in methods:
            if method not in method_results:
                continue
                
            results = method_results[method]
            
            print(f"\n?? Method: {method.upper()}")
            print(f"   Tests completed: {len(results)}")
            print(f"   Average byte accuracy: {np.mean([r['byte_accuracy'] for r in results]):.4f}")
            print(f"   Average bit accuracy: {np.mean([r['bit_accuracy'] for r in results]):.4f}")
            print(f"   Average near-miss accuracy: {np.mean([r['near_miss_accuracy'] for r in results]):.4f}")
            print(f"   Average confidence: {np.mean([r['avg_confidence'] for r in results]):.4f}")
            print(f"   High-confidence accuracy: {np.mean([r['high_conf_accuracy'] for r in results]):.4f}")
            print(f"   Average opcode validity: {np.mean([r['opcode_validity'] for r in results]):.4f}")
            
            # Accuracy by hole size
            hole_sizes = [r['hole_size'] for r in results]
            accuracies = [r['byte_accuracy'] for r in results]
            
            size_stats = {}
            for size, acc in zip(hole_sizes, accuracies):
                if size not in size_stats:
                    size_stats[size] = []
                size_stats[size].append(acc)
            
            for size in sorted(size_stats.keys()):
                size_accs = size_stats[size]
                print(f"   {size:2d}-byte holes: {np.mean(size_accs):.4f} ± {np.std(size_accs):.3f} accuracy ({len(size_accs)} tests)")
        
        # Overall statistics
        print(f"\n?? OVERALL STATISTICS:")
        print(f"   Total tests: {len(all_results)}")
        print(f"   Total holes tested: {len(set((r['hole_start'], r['hole_end']) for r in all_results))}")
        print(f"   Total bytes patched: {sum(r['hole_size'] for r in all_results)}")
        print(f"   Average hole size: {np.mean([r['hole_size'] for r in all_results]):.1f} bytes")
        print(f"   Intensity level: {intensity}/10")
        
        if method_results:
            best_method = max(methods, key=lambda m: np.mean([r['byte_accuracy'] for r in method_results.get(m, [])]))
            best_accuracy = np.mean([r['byte_accuracy'] for r in method_results.get(best_method, [])])
            print(f"   Best method: {best_method} ({best_accuracy:.4f} accuracy)")
        
        # ROM-wide statistics (from the last result)
        if all_results and 'rom_diff_stats' in all_results[-1]:
            rom_stats = all_results[-1]['rom_diff_stats']
            print(f"\n?? FINAL ROM COMPARISON:")
            print(f"   Total ROM bytes: {rom_stats['total_bytes']:,}")
            print(f"   Different bytes: {rom_stats['different_bytes']:,}")
            print(f"   Similarity rate: {rom_stats['exact_match_rate']:.4f}")
            print(f"   Bit accuracy: {rom_stats['bit_accuracy']:.4f}")
            print(f"   Avg byte difference: {rom_stats['avg_byte_difference']:.2f}")
            print(f"   Max byte difference: {rom_stats['max_byte_difference']}")
        
        # Generate detailed report
        self.generate_detailed_report(all_results, methods)
        
        # Save detailed results if requested
        if output_file:
            try:
                import json
                with open(output_file, 'w') as f:
                    json.dump({
                        'metadata': {
                            'timestamp': datetime.now().isoformat(),
                            'rom_file': nes_rom_path,
                            'intensity': intensity,
                            'num_tests': len(all_results),
                            'methods': methods,
                            'rom_diff_stats': all_results[-1]['rom_diff_stats'] if all_results and 'rom_diff_stats' in all_results[-1] else None
                        },
                        'results': all_results
                    }, f, indent=2)
                print(f"\n?? Detailed results saved to: {output_file}")
            except Exception as e:
                print(f"\n?? Failed to save results: {e}")
            
        print(f"\n?? Validation completed at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        
        return all_results

def main():
    parser = argparse.ArgumentParser(
        description="Validate ROM hole patching with intensity-based corruption on NES ROMs",
        epilog="""
Examples:
  python validate_patcher.py game.nes --epoch 15 --intensity 7
  python validate_patcher.py game.nes --list-epochs
  python validate_patcher.py game.nes --model custom_model.pt
  python validate_patcher.py game.nes --methods bidirectional --output results.json
        """,
        formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("nes_rom", help="Path to .nes ROM file to test")
    parser.add_argument("--intensity", type=int, default=5, choices=range(1, 11),
                       help="Corruption intensity level (1=light, 10=extreme)")
    parser.add_argument("--model", default=DEFAULT_MODEL_PATH, help="Path to trained model")
    parser.add_argument("--epoch", type=int, help="Specific epoch number to use for validation (e.g., --epoch 15)")
    parser.add_argument("--methods", nargs="+", default=['forward', 'backward', 'bidirectional'],
                       choices=['forward', 'backward', 'bidirectional', 'ensemble'],
                       help="Patching methods to test")
    parser.add_argument("--output", help="File to save detailed results (JSON)")
    parser.add_argument("--seed", type=int, default=42, help="Random seed for reproducible results")
    parser.add_argument("--list-epochs", action="store_true", 
                       help="List available epoch models and exit")
    
    args = parser.parse_args()
    
    # Handle --list-epochs option
    if args.list_epochs:
        print("?? Available epoch models:")
        if os.path.exists(EPOCHS_DIR):
            epoch_files = [f for f in os.listdir(EPOCHS_DIR) if f.startswith('6502_span_predictor_epoch') and f.endswith('.pt')]
            if epoch_files:
                epoch_numbers = []
                best_epoch = None
                best_accuracy = 0.0
                
                print(f"{'Epoch':<6} {'Accuracy':<10} {'Loss':<12} {'Size':<8} {'Filename'}")
                print("-" * 60)
                
                for f in sorted(epoch_files):
                    try:
                        epoch_num = int(f.replace('6502_span_predictor_epoch', '').replace('.pt', ''))
                        epoch_numbers.append(epoch_num)
                        
                        model_path = os.path.join(EPOCHS_DIR, f)
                        model_info = get_model_info(model_path)
                        
                        accuracy = model_info.get('accuracy', 'Unknown')
                        loss = model_info.get('loss', 'Unknown')
                        size_mb = model_info.get('file_size_mb', 0)
                        
                        # Track best accuracy
                        if isinstance(accuracy, (int, float)) and accuracy > best_accuracy:
                            best_accuracy = accuracy
                            best_epoch = epoch_num
                        
                        # Format the output
                        acc_str = f"{accuracy:.4f}" if isinstance(accuracy, (int, float)) else str(accuracy)[:8]
                        loss_str = f"{loss:.6f}" if isinstance(loss, (int, float)) else str(loss)[:10]
                        
                        marker = " ??" if epoch_num == best_epoch and isinstance(accuracy, (int, float)) else ""
                        
                        print(f"{epoch_num:<6} {acc_str:<10} {loss_str:<12} {size_mb:<7.1f}MB {f}{marker}")
                        
                    except ValueError:
                        continue
                
                if epoch_numbers:
                    print(f"\n?? Summary:")
                    print(f"   • Available epochs: {min(epoch_numbers)}-{max(epoch_numbers)}")
                    print(f"   • Total epoch models: {len(epoch_numbers)}")
                    if best_epoch:
                        print(f"   • Best accuracy: Epoch {best_epoch} ({best_accuracy:.4f})")
                    print(f"\n?? Usage examples:")
                    print(f"   • Use best epoch: python validate_patcher.py ROM.nes --epoch {best_epoch if best_epoch else max(epoch_numbers)}")
                    print(f"   • Use latest epoch: python validate_patcher.py ROM.nes --epoch {max(epoch_numbers)}")
                    print(f"   • Compare epochs: Run validation with different --epoch values")
            else:
                print("   • No epoch models found in epoch_models directory")
        else:
            print("   • epoch_models directory does not exist")
            print("   • Run training first to generate epoch models")
        return 0
    
    # Validate epoch and model arguments
    if args.epoch is not None and args.model != DEFAULT_MODEL_PATH:
        print("? Cannot specify both --epoch and --model arguments")
        print("?? Use --epoch to select a specific epoch, or --model for a custom model path")
        return 1
    
    # Set random seed for reproducible results
    random.seed(args.seed)
    np.random.seed(args.seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed(args.seed)
    torch.manual_seed(args.seed)
    
    print(f"?? Using random seed: {args.seed}")
    
    try:
        validator = ROMValidator(args.model, epoch=args.epoch)
        results = validator.run_validation(
            args.nes_rom,
            intensity=args.intensity,
            methods=args.methods,
            output_file=args.output,
            epoch=args.epoch
        )
        
        if results:
            print(f"\n? Validation completed successfully with {len(results)} total tests")
        else:
            print(f"\n?? Validation completed but no results were obtained")
            
    except KeyboardInterrupt:
        print(f"\n\n?? Validation interrupted by user")
        return 130
    except Exception as e:
        print(f"? Validation failed: {e}")
        import traceback
        traceback.print_exc()
        return 1
        
    return 0

if __name__ == "__main__":
    exit(main())