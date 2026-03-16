import os
import torch
import torch.nn as nn
import numpy as np
from train_6502_predictor import TransformerPredictor, VOCAB_SIZE, MASK_TOKEN

def export_model_to_onnx(model_path, onnx_path, seq_len=128):
    """
    Export the trained PyTorch model to ONNX format for C# inference
    """
    print("?? Exporting PyTorch model to ONNX for C# inference...")
    print(f"?? Source model: {model_path}")
    print(f"?? Target ONNX: {onnx_path}")
    
    # Load the trained model
    if not os.path.exists(model_path):
        raise FileNotFoundError(f"Model file not found: {model_path}")
    
    checkpoint = torch.load(model_path, map_location='cpu')  # Load on CPU for export
    config = checkpoint['config']
    
    print(f"?? Model configuration:")
    for key, value in config.items():
        print(f"   • {key}: {value}")
    
    # Create model with loaded config
    model = TransformerPredictor(
        vocab_size=config['vocab_size'],
        embed_size=config['embed_size'],
        hidden_size=config['hidden_size'],
        num_heads=config['num_heads'],
        num_layers=config['num_layers'],
        dropout=config['dropout'],
        max_len=config['seq_len']
    )
    
    # Load model state
    model.load_state_dict(checkpoint['model_state_dict'])
    model.eval()
    
    print(f"? Model loaded successfully!")
    print(f"   ?? Training accuracy: {checkpoint.get('accuracy', 'N/A')}")
    print(f"   ?? Best accuracy: {checkpoint.get('best_accuracy', 'N/A')}")
    print(f"   ?? Parameters: {sum(p.numel() for p in model.parameters()):,}")
    
    # Create dummy input for tracing (batch_size=1, seq_len)
    dummy_input = torch.randint(0, VOCAB_SIZE, (1, seq_len), dtype=torch.long)
    
    print(f"?? Creating dummy input for tracing: shape {dummy_input.shape}")
    
    # Export to ONNX
    try:
        torch.onnx.export(
            model,
            dummy_input,
            onnx_path,
            export_params=True,
            opset_version=11,  # Compatible with ML.NET
            do_constant_folding=True,
            input_names=['input_ids'],
            output_names=['logits'],
            dynamic_axes={
                'input_ids': {0: 'batch_size'},
                'logits': {0: 'batch_size'}
            },
            verbose=False
        )
        
        file_size = os.path.getsize(onnx_path) / (1024 * 1024)  # MB
        print(f"? ONNX export successful!")
        print(f"   ?? File size: {file_size:.2f} MB")
        
        # Verify the export by loading it
        try:
            import onnx
            onnx_model = onnx.load(onnx_path)
            onnx.checker.check_model(onnx_model)
            print(f"? ONNX model validation passed!")
            
            # Print input/output info
            print(f"?? ONNX Model Information:")
            for input_info in onnx_model.graph.input:
                print(f"   ?? Input: {input_info.name} - {[dim.dim_value for dim in input_info.type.tensor_type.shape.dim]}")
            for output_info in onnx_model.graph.output:
                print(f"   ?? Output: {output_info.name} - {[dim.dim_value for dim in output_info.type.tensor_type.shape.dim]}")
                
        except ImportError:
            print("??  ONNX validation skipped (onnx package not installed)")
        except Exception as e:
            print(f"??  ONNX validation failed: {e}")
            
    except Exception as e:
        print(f"? ONNX export failed: {e}")
        raise
    
    # Save configuration for C# implementation
    config_path = onnx_path.replace('.onnx', '_config.json')
    import json
    
    export_config = {
        'seq_len': config['seq_len'],
        'vocab_size': config['vocab_size'],
        'embed_size': config['embed_size'],
        'hidden_size': config['hidden_size'],
        'num_heads': config['num_heads'],
        'num_layers': config['num_layers'],
        'dropout': config['dropout'],
        'mask_token': MASK_TOKEN,
        'model_accuracy': float(checkpoint.get('accuracy', 0)),
        'best_accuracy': float(checkpoint.get('best_accuracy', 0)),
        'export_timestamp': str(torch.datetime.datetime.now()),
        'pytorch_version': torch.__version__
    }
    
    with open(config_path, 'w') as f:
        json.dump(export_config, f, indent=2)
    
    print(f"?? Configuration saved to: {config_path}")
    
    return onnx_path, config_path

def export_sample_data(output_dir="onnx_export"):
    """
    Export sample data for testing C# inference
    """
    os.makedirs(output_dir, exist_ok=True)
    
    # Create sample input with masked hole
    seq_len = 128
    sample_sequence = list(range(256))[:seq_len]  # Sample sequence 0-127
    
    # Create a hole in the middle
    hole_start = 60
    hole_end = 68
    masked_sequence = sample_sequence.copy()
    original_bytes = masked_sequence[hole_start:hole_end].copy()
    
    for i in range(hole_start, hole_end):
        masked_sequence[i] = MASK_TOKEN
    
    # Save test data
    test_data = {
        'masked_sequence': masked_sequence,
        'original_sequence': sample_sequence,
        'hole_start': hole_start,
        'hole_end': hole_end,
        'expected_bytes': original_bytes,
        'seq_len': seq_len,
        'mask_token': MASK_TOKEN
    }
    
    import json
    test_data_path = os.path.join(output_dir, 'test_data.json')
    with open(test_data_path, 'w') as f:
        json.dump(test_data, f, indent=2)
    
    print(f"?? Test data saved to: {test_data_path}")
    return test_data_path

def main():
    print("=" * 70)
    print("?? PYTORCH ? ONNX EXPORTER FOR C# INFERENCE")
    print("=" * 70)
    
    # Look for the best model first, fall back to regular model
    model_candidates = [
        "6502_span_predictor_best.pt",
        "6502_span_predictor.pt"
    ]
    
    model_path = None
    for candidate in model_candidates:
        if os.path.exists(candidate):
            model_path = candidate
            break
    
    if not model_path:
        print("? No trained model found!")
        print("Available candidates:")
        for candidate in model_candidates:
            print(f"   • {candidate} - {'?' if os.path.exists(candidate) else '?'}")
        print("\n?? Run training first: python train_6502_predictor.py")
        return 1
    
    # Export paths
    output_dir = "onnx_export"
    os.makedirs(output_dir, exist_ok=True)
    
    onnx_path = os.path.join(output_dir, "6502_span_predictor.onnx")
    
    try:
        # Export model to ONNX
        exported_onnx, config_path = export_model_to_onnx(model_path, onnx_path)
        
        # Export sample test data
        test_data_path = export_sample_data(output_dir)
        
        print(f"\n?? Export completed successfully!")
        print(f"?? Output directory: {output_dir}")
        print(f"   ?? ONNX model: {os.path.basename(exported_onnx)}")
        print(f"   ??  Configuration: {os.path.basename(config_path)}")
        print(f"   ?? Test data: {os.path.basename(test_data_path)}")
        
        print(f"\n?? Next steps for C# integration:")
        print(f"1. Install ML.NET package: Microsoft.ML.OnnxRuntime")
        print(f"2. Copy ONNX files to your C# project")
        print(f"3. Implement C# inference using the exported model")
        print(f"4. Use test_data.json to validate C# vs Python results")
        
        return 0
        
    except Exception as e:
        print(f"? Export failed: {e}")
        return 1

if __name__ == "__main__":
    exit(main())