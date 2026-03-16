# NES ROM Hole Reconstruction - Usage Guide

This project implements an advanced neural network system for reconstructing missing data (holes) in damaged NES PRG ROM files using bidirectional context and transformer architecture.

## Features

- **Bidirectional Span Reconstruction**: Uses both preceding and following context to fill holes
- **Transformer Architecture**: Employs attention mechanisms for better sequence understanding
- **Multiple Prediction Methods**: Forward-only, backward-only, bidirectional merge, and ensemble
- **Comprehensive Validation**: Synthetic corruption testing with accuracy metrics
- **Opcode-Aware**: Validates reconstructed code for 6502 instruction validity

## Quick Start

### 1. Extract NES ROMs (if not already done)
```bash
dotnet run  # This will extract .nes files to .prg files in the prg/ directory
```

### 2. Train the Model
```bash
python train_6502_predictor.py
```

This will:
- Load all `.prg` files from the `prg/` directory
- Train a Transformer model with masked span reconstruction
- Save the best model as `6502_span_predictor_best.pt`
- Take approximately 1-2 hours depending on hardware

### 3. Validate the Model
```bash
python validate_patcher.py --num-roms 10 --holes-per-rom 3
```

This will:
- Test the model on synthetic corruption
- Report accuracy metrics for different hole sizes
- Compare forward, backward, and bidirectional methods

### 4. Patch a Real ROM
```bash
python patch_rom.py damaged_rom.prg fixed_rom.prg 0x1000 0x1020 --method bidirectional
```

This patches bytes from position 0x1000 to 0x1020 using bidirectional prediction.

## Architecture Changes

### From Simple Feedforward to Transformer

**Old Model (train_6502_predictor.py original):**
- Predicted next byte only
- Used flattened embeddings + linear layers
- No sequence awareness beyond embedding

**New Model (train_6502_predictor.py updated):**
- Predicts entire masked spans
- Uses Transformer encoder with self-attention  
- Bidirectional context understanding
- Positional encoding for sequence awareness

### Key Improvements

1. **Masked Language Modeling**: Trains on randomly masked 8-32 byte spans
2. **Longer Context**: Increased sequence length from 32 to 128 bytes
3. **Bidirectional Attention**: Uses both left and right context simultaneously
4. **Advanced Sampling**: Temperature scaling and top-k sampling for diversity

## Patching Methods

### Forward Prediction
- Uses only preceding context
- Good for sequential code patterns
- Faster but less accurate for complex structures

### Backward Prediction  
- Uses only following context (in reverse)
- Good for data tables and known endpoints
- Complements forward prediction

### Bidirectional Merge
- Runs both forward and backward predictions
- Chooses prediction with higher confidence for each byte
- Best overall accuracy for most scenarios

### Ensemble
- Multiple sampling runs with confidence voting
- Highest accuracy but slower
- Best for critical patches

## Model Configuration

```python
SEQ_LEN = 128          # Context window size
VOCAB_SIZE = 257       # 0-255 bytes + MASK token
EMBED_SIZE = 256       # Embedding dimensions
HIDDEN_SIZE = 512      # Feed-forward hidden size
NUM_HEADS = 8          # Attention heads
NUM_LAYERS = 6         # Transformer layers
MIN_HOLE_SIZE = 8      # Minimum synthetic hole size
MAX_HOLE_SIZE = 32     # Maximum synthetic hole size
```

## File Structure

- `train_6502_predictor.py` - Main training script with Transformer model
- `patch_rom.py` - ROM patching utility with multiple methods
- `validate_patcher.py` - Validation system with synthetic corruption
- `Extractor.cs` - NES ROM extraction (C#)
- `Program.cs` - Main C# program
- `prg/` - Directory containing extracted PRG ROM files
- `6502_span_predictor_best.pt` - Best trained model checkpoint

## Expected Performance

Based on the improved architecture, expect:
- **70-85% byte accuracy** on 8-16 byte holes
- **60-75% byte accuracy** on 16-32 byte holes  
- **85-95% opcode validity** in code regions
- **Higher accuracy** in data tables and repeated patterns

## Hardware Requirements

- **Training**: NVIDIA GPU with 8GB+ VRAM recommended (2-4 hours)
- **Inference**: Any GPU or modern CPU (seconds per hole)
- **RAM**: 16GB+ recommended for large ROM collections

## Validation Metrics

The validation system measures:
- **Byte Accuracy**: Exact byte matches
- **Bit Accuracy**: Bit-level similarity  
- **Confidence-Weighted Accuracy**: Accuracy weighted by model confidence
- **Opcode Validity**: Percentage of valid 6502 instructions
- **High-Confidence Accuracy**: Accuracy for confident predictions only

## Tips for Best Results

1. **Use bidirectional method** for general-purpose patching
2. **Lower temperature** (0.1-0.5) for deterministic results
3. **Higher temperature** (0.7-1.0) for creative reconstruction
4. **Ensemble method** for critical patches where accuracy matters most
5. **Validate results** with an emulator when possible

## Troubleshooting

### Low Accuracy
- Increase training epochs
- Use ensemble method
- Check if hole is in code vs data region

### Memory Issues
- Reduce batch size in training
- Use CPU inference instead of GPU
- Process smaller sequence lengths

### Model Not Loading
- Ensure model file exists and isn't corrupted
- Check Python dependencies (torch, tqdm, numpy)
- Verify model was trained with compatible configuration

## Future Improvements

- **Game-Specific Models**: Train separate models for different game genres
- **Multi-Scale Context**: Use multiple sequence lengths simultaneously  
- **Advanced Validation**: Integration with actual NES emulators
- **IPS Patch Export**: Direct generation of patch files for ROM hacking tools