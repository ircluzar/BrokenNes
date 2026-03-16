import os
import torch
import torch.nn as nn
import numpy as np
from datetime import datetime
import argparse
try:
    from tqdm import tqdm
except ImportError:
    print("Installing tqdm for progress bars...")
    import subprocess
    import sys
    subprocess.check_call([sys.executable, "-m", "pip", "install", "tqdm"])
    from tqdm import tqdm

# Import the model architecture from the training script
from train_6502_predictor import TransformerPredictor, VOCAB_SIZE, MASK_TOKEN

class ROMPatcher:
    """Class for patching holes in NES PRG ROMs using the trained model"""
    
    def __init__(self, model_path, device=None):
        self.device = device or torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        self.model = None
        self.config = None
        
        print(f"?? Initializing ROM Patcher")
        print(f"?? Using device: {self.device}")
        
        # Load model
        self.load_model(model_path)
        
    def load_model(self, model_path):
        """Load the trained model and configuration"""
        if not os.path.exists(model_path):
            raise FileNotFoundError(f"Model file not found: {model_path}")
            
        print(f"?? Loading model from: {model_path}")
        
        checkpoint = torch.load(model_path, map_location=self.device)
        self.config = checkpoint['config']
        
        # Create model with loaded config
        self.model = TransformerPredictor(
            vocab_size=self.config['vocab_size'],
            embed_size=self.config['embed_size'],
            hidden_size=self.config['hidden_size'],
            num_heads=self.config['num_heads'],
            num_layers=self.config['num_layers'],
            dropout=self.config['dropout'],
            max_len=self.config['seq_len']
        ).to(self.device)
        
        # Load model state
        self.model.load_state_dict(checkpoint['model_state_dict'])
        self.model.eval()
        
        print(f"? Model loaded successfully!")
        print(f"  • Training accuracy: {checkpoint.get('accuracy', 'N/A'):.4f}")
        print(f"  • Best accuracy: {checkpoint.get('best_accuracy', 'N/A'):.4f}")
        print(f"  • Sequence length: {self.config['seq_len']}")
        print(f"  • Model parameters: {sum(p.numel() for p in self.model.parameters()):,}")
        
    def prepare_sequence(self, rom_data, hole_start, hole_end, context_size=None):
        """Prepare a sequence with the hole marked for prediction"""
        if context_size is None:
            context_size = self.config['seq_len']
            
        # Calculate sequence boundaries
        seq_start = max(0, hole_start - (context_size - (hole_end - hole_start)) // 2)
        seq_end = min(len(rom_data), seq_start + context_size)
        
        # Adjust if we hit boundaries
        if seq_end - seq_start < context_size:
            seq_start = max(0, seq_end - context_size)
            
        # Extract sequence
        sequence = list(rom_data[seq_start:seq_end])
        
        # Mark hole positions with MASK tokens
        hole_start_rel = hole_start - seq_start
        hole_end_rel = hole_end - seq_start
        
        # Ensure hole is within sequence bounds
        hole_start_rel = max(0, hole_start_rel)
        hole_end_rel = min(len(sequence), hole_end_rel)
        
        # Replace hole with MASK tokens
        for i in range(hole_start_rel, hole_end_rel):
            if i < len(sequence):
                sequence[i] = MASK_TOKEN
                
        # Pad or truncate to exact sequence length
        if len(sequence) < context_size:
            sequence.extend([0] * (context_size - len(sequence)))
        elif len(sequence) > context_size:
            sequence = sequence[:context_size]
            
        return torch.tensor(sequence, dtype=torch.long).unsqueeze(0), hole_start_rel, hole_end_rel, seq_start
    
    def predict_span(self, sequence_tensor, hole_start_rel, hole_end_rel, temperature=1.0, top_k=None):
        """Predict the bytes for the masked span"""
        with torch.no_grad():
            sequence_tensor = sequence_tensor.to(self.device)
            
            # Get model predictions
            logits = self.model(sequence_tensor)
            
            # Extract predictions for the hole region
            hole_logits = logits[0, hole_start_rel:hole_end_rel]
            
            # Apply temperature scaling
            if temperature != 1.0:
                hole_logits = hole_logits / temperature
                
            # Apply top-k filtering if specified
            if top_k is not None:
                top_k = min(top_k, hole_logits.size(-1))
                indices_to_remove = hole_logits < torch.topk(hole_logits, top_k)[0][..., -1, None]
                hole_logits[indices_to_remove] = float('-inf')
            
            # Convert to probabilities
            probs = torch.softmax(hole_logits, dim=-1)
            
            # Sample from distribution (for diversity) or take argmax (for deterministic)
            if temperature > 0:
                predicted_tokens = torch.multinomial(probs, num_samples=1).squeeze(-1)
            else:
                predicted_tokens = probs.argmax(dim=-1)
            
            # Get confidence scores
            confidence_scores = torch.gather(probs, 1, predicted_tokens.unsqueeze(-1)).squeeze(-1)
            
            return predicted_tokens.cpu().numpy(), confidence_scores.cpu().numpy()
    
    def patch_hole(self, rom_data, hole_start, hole_end, method='bidirectional', 
                   temperature=0.5, top_k=50, min_confidence=0.1):
        """
        Patch a hole in ROM data using different methods
        
        Args:
            rom_data: The ROM data as bytes or list
            hole_start: Start position of the hole
            hole_end: End position of the hole  
            method: 'forward', 'backward', 'bidirectional', or 'ensemble'
            temperature: Sampling temperature (0 = deterministic, >0 = stochastic)
            top_k: Limit sampling to top-k most likely tokens
            min_confidence: Minimum confidence threshold for predictions
        """
        rom_data = list(rom_data) if isinstance(rom_data, bytes) else rom_data.copy()
        hole_size = hole_end - hole_start
        
        print(f"?? Patching hole at positions {hole_start}-{hole_end} ({hole_size} bytes)")
        print(f"?? Method: {method}, Temperature: {temperature}, Top-k: {top_k}")
        
        if method == 'forward' or method == 'bidirectional':
            # Forward prediction
            seq_tensor, hole_start_rel, hole_end_rel, seq_start = self.prepare_sequence(
                rom_data, hole_start, hole_end
            )
            
            pred_tokens, confidence = self.predict_span(
                seq_tensor, hole_start_rel, hole_end_rel, temperature, top_k
            )
            
            print(f"? Forward prediction completed")
            print(f"   Average confidence: {confidence.mean():.3f}")
            print(f"   Min confidence: {confidence.min():.3f}")
            
            if method == 'forward':
                return pred_tokens, confidence
                
        if method == 'backward' or method == 'bidirectional':
            # Backward prediction - reverse the ROM data
            reversed_rom = rom_data[::-1]
            reversed_hole_start = len(rom_data) - hole_end
            reversed_hole_end = len(rom_data) - hole_start
            
            seq_tensor_rev, hole_start_rel_rev, hole_end_rel_rev, seq_start_rev = self.prepare_sequence(
                reversed_rom, reversed_hole_start, reversed_hole_end
            )
            
            pred_tokens_rev, confidence_rev = self.predict_span(
                seq_tensor_rev, hole_start_rel_rev, hole_end_rel_rev, temperature, top_k
            )
            
            # Reverse the predictions back
            pred_tokens_back = pred_tokens_rev[::-1]
            confidence_back = confidence_rev[::-1]
            
            print(f"? Backward prediction completed")
            print(f"   Average confidence: {confidence_back.mean():.3f}")
            print(f"   Min confidence: {confidence_back.min():.3f}")
            
            if method == 'backward':
                return pred_tokens_back, confidence_back
                
        if method == 'bidirectional':
            # Combine forward and backward predictions
            combined_tokens = []
            combined_confidence = []
            
            for i in range(len(pred_tokens)):
                # Choose prediction with higher confidence
                if confidence[i] >= confidence_back[i]:
                    combined_tokens.append(pred_tokens[i])
                    combined_confidence.append(confidence[i])
                else:
                    combined_tokens.append(pred_tokens_back[i])
                    combined_confidence.append(confidence_back[i])
                    
            print(f"? Bidirectional merge completed")
            print(f"   Forward wins: {sum(1 for i in range(len(pred_tokens)) if confidence[i] >= confidence_back[i])}")
            print(f"   Backward wins: {sum(1 for i in range(len(pred_tokens)) if confidence[i] < confidence_back[i])}")
            print(f"   Average confidence: {np.mean(combined_confidence):.3f}")
            
            return np.array(combined_tokens), np.array(combined_confidence)
            
        elif method == 'ensemble':
            # Ensemble method - average probabilities (requires multiple runs)
            print("?? Running ensemble prediction...")
            ensemble_preds = []
            ensemble_confs = []
            
            for run in range(5):  # 5 runs for ensemble
                seq_tensor, hole_start_rel, hole_end_rel, seq_start = self.prepare_sequence(
                    rom_data, hole_start, hole_end
                )
                pred_tokens, confidence = self.predict_span(
                    seq_tensor, hole_start_rel, hole_end_rel, temperature=0.7, top_k=top_k
                )
                ensemble_preds.append(pred_tokens)
                ensemble_confs.append(confidence)
                
            # Take majority vote for each position
            final_tokens = []
            final_confidence = []
            
            for pos in range(hole_size):
                votes = [pred[pos] for pred in ensemble_preds]
                confs = [conf[pos] for conf in ensemble_confs]
                
                # Count votes for each token
                vote_counts = {}
                vote_confs = {}
                
                for token, conf in zip(votes, confs):
                    if token not in vote_counts:
                        vote_counts[token] = 0
                        vote_confs[token] = []
                    vote_counts[token] += 1
                    vote_confs[token].append(conf)
                
                # Choose token with most votes, break ties with confidence
                best_token = max(vote_counts.keys(), 
                               key=lambda t: (vote_counts[t], np.mean(vote_confs[t])))
                
                final_tokens.append(best_token)
                final_confidence.append(np.mean(vote_confs[best_token]))
                
            print(f"? Ensemble prediction completed")
            print(f"   Average confidence: {np.mean(final_confidence):.3f}")
            
            return np.array(final_tokens), np.array(final_confidence)
    
    def patch_rom_file(self, input_path, output_path, hole_start, hole_end, 
                       method='bidirectional', temperature=0.5, top_k=50):
        """Patch a hole in a ROM file and save the result"""
        print(f"?? Patching ROM file: {input_path}")
        
        # Read ROM data
        with open(input_path, 'rb') as f:
            rom_data = list(f.read())
            
        print(f"?? ROM size: {len(rom_data):,} bytes")
        print(f"???  Hole: {hole_start}-{hole_end} ({hole_end - hole_start} bytes)")
        
        # Validate hole bounds
        if hole_start < 0 or hole_end > len(rom_data) or hole_start >= hole_end:
            raise ValueError(f"Invalid hole bounds: {hole_start}-{hole_end}")
            
        # Show context around hole
        context_size = 32
        context_start = max(0, hole_start - context_size)
        context_end = min(len(rom_data), hole_end + context_size)
        
        print(f"\n?? Context around hole (±{context_size} bytes):")
        context_bytes = rom_data[context_start:hole_start] + [None] * (hole_end - hole_start) + rom_data[hole_end:context_end]
        
        hex_lines = []
        for i in range(0, len(context_bytes), 16):
            addr = context_start + i
            line_bytes = context_bytes[i:i+16]
            hex_parts = []
            ascii_parts = []
            
            for b in line_bytes:
                if b is None:
                    hex_parts.append("??")
                    ascii_parts.append("?")
                else:
                    hex_parts.append(f"{b:02X}")
                    ascii_parts.append(chr(b) if 32 <= b <= 126 else ".")
                    
            hex_str = " ".join(hex_parts).ljust(47)
            ascii_str = "".join(ascii_parts)
            hex_lines.append(f"  {addr:04X}: {hex_str} |{ascii_str}|")
            
        for line in hex_lines[:8]:  # Show first few lines
            print(line)
        if len(hex_lines) > 8:
            print("  ...")
            
        # Perform patching
        predicted_bytes, confidence = self.patch_hole(
            rom_data, hole_start, hole_end, method, temperature, top_k
        )
        
        # Apply patch
        patched_rom = rom_data.copy()
        for i, byte_val in enumerate(predicted_bytes):
            patched_rom[hole_start + i] = int(byte_val)
            
        # Show patched result
        print(f"\n?? Patch results:")
        print(f"   Predicted bytes: {' '.join(f'{b:02X}' for b in predicted_bytes)}")
        print(f"   Confidence scores: {' '.join(f'{c:.2f}' for c in confidence)}")
        print(f"   Average confidence: {confidence.mean():.3f}")
        
        # Low confidence warning
        low_conf_count = sum(1 for c in confidence if c < 0.3)
        if low_conf_count > 0:
            print(f"   ??  {low_conf_count} bytes have low confidence (<0.3)")
            
        # Save patched ROM
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, 'wb') as f:
            f.write(bytes(patched_rom))
            
        print(f"\n?? Patched ROM saved to: {output_path}")
        print(f"? Patching completed successfully!")
        
        return predicted_bytes, confidence

def main():
    parser = argparse.ArgumentParser(description="Patch holes in NES PRG ROM files using trained model")
    parser.add_argument("input_rom", help="Path to the ROM file with holes")
    parser.add_argument("output_rom", help="Path to save the patched ROM")
    parser.add_argument("hole_start", type=int, help="Start position of the hole (hex or decimal)")
    parser.add_argument("hole_end", type=int, help="End position of the hole (hex or decimal)")
    parser.add_argument("--model", default="6502_span_predictor_best.pt", help="Path to trained model")
    parser.add_argument("--method", choices=['forward', 'backward', 'bidirectional', 'ensemble'], 
                       default='bidirectional', help="Prediction method")
    parser.add_argument("--temperature", type=float, default=0.5, help="Sampling temperature")
    parser.add_argument("--top-k", type=int, default=50, help="Top-k sampling limit")
    
    args = parser.parse_args()
    
    print("=" * 70)
    print("?? NES ROM HOLE PATCHER")
    print("=" * 70)
    print(f"? Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    
    try:
        # Initialize patcher
        patcher = ROMPatcher(args.model)
        
        # Patch the ROM
        predicted_bytes, confidence = patcher.patch_rom_file(
            args.input_rom, args.output_rom, args.hole_start, args.hole_end,
            args.method, args.temperature, args.top_k
        )
        
        print(f"\n?? Completed at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        
    except Exception as e:
        print(f"? Error: {e}")
        return 1
        
    return 0

if __name__ == "__main__":
    exit(main())