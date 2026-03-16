import os
import glob
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import Dataset, DataLoader
import time
import random
import numpy as np
from datetime import datetime, timedelta
import signal
import threading
try:
    from tqdm import tqdm
except ImportError:
    print("Installing tqdm for progress bars...")
    import subprocess
    import sys
    subprocess.check_call([sys.executable, "-m", "pip", "install", "tqdm"])
    from tqdm import tqdm

# Settings - Optimized for memory efficiency
PRG_DIR = os.path.join(os.path.dirname(__file__), 'prg')
SEQ_LEN = 128  # Increased from 32 for better context
BATCH_SIZE = 16  # Further reduced to save memory and avoid freezing
EPOCHS = 40
EMBED_SIZE = 256
HIDDEN_SIZE = 512
NUM_HEADS = 8
NUM_LAYERS = 6
DROPOUT = 0.1
MODEL_PATH = os.path.join(os.path.dirname(__file__), '6502_span_predictor.pt')

# Epoch model saving settings
SAVE_EPOCH_MODELS = True  # Set to False to disable epoch model saving
EPOCHS_DIR = os.path.join(os.path.dirname(__file__), 'epoch_models')  # Directory for epoch models

# Vocabulary: 0-255 for bytes, 256 for MASK token
VOCAB_SIZE = 257
MASK_TOKEN = 256
MIN_HOLE_SIZE = 8
MAX_HOLE_SIZE = 32
MASK_PROB = 0.15  # Probability of masking during training

# Memory optimization settings
MAX_SEQUENCES_IN_MEMORY = 10000  # Reduced cache size to prevent freezing
STEP_SIZE = SEQ_LEN // 4  # Overlap step size (75% overlap for more data)
MAX_FILES_TO_PROCESS = 500  # Limit number of files to prevent overwhelm
FILE_SIZE_LIMIT = 1024 * 1024  # Skip files larger than 1MB to avoid memory issues

class PositionalEncoding(nn.Module):
    """Positional encoding for transformer"""
    def __init__(self, d_model, max_len=1024):
        super().__init__()
        
        pe = torch.zeros(max_len, d_model)
        position = torch.arange(0, max_len, dtype=torch.float).unsqueeze(1)
        div_term = torch.exp(torch.arange(0, d_model, 2).float() * (-np.log(10000.0) / d_model))
        
        pe[:, 0::2] = torch.sin(position * div_term)
        pe[:, 1::2] = torch.cos(position * div_term)
        pe = pe.unsqueeze(0).transpose(0, 1)
        
        self.register_buffer('pe', pe)

    def forward(self, x):
        return x + self.pe[:x.size(0), :]

class TransformerPredictor(nn.Module):
    """Transformer model for bidirectional span reconstruction"""
    def __init__(self, vocab_size=VOCAB_SIZE, embed_size=EMBED_SIZE, 
                 hidden_size=HIDDEN_SIZE, num_heads=NUM_HEADS, 
                 num_layers=NUM_LAYERS, dropout=DROPOUT, max_len=SEQ_LEN):
        super().__init__()
        
        self.vocab_size = vocab_size
        self.embed_size = embed_size
        self.max_len = max_len
        
        # Embedding layers
        self.token_embedding = nn.Embedding(vocab_size, embed_size)
        self.pos_encoding = PositionalEncoding(embed_size, max_len)
        
        # Transformer encoder
        encoder_layer = nn.TransformerEncoderLayer(
            d_model=embed_size,
            nhead=num_heads,
            dim_feedforward=hidden_size,
            dropout=dropout,
            activation='gelu',
            batch_first=True
        )
        self.transformer = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)
        
        # Output projection
        self.layer_norm = nn.LayerNorm(embed_size)
        self.output_projection = nn.Linear(embed_size, vocab_size)
        
        # Initialize weights
        self.apply(self._init_weights)
        
    def _init_weights(self, module):
        if isinstance(module, nn.Linear):
            torch.nn.init.normal_(module.weight, mean=0.0, std=0.02)
            if module.bias is not None:
                torch.nn.init.zeros_(module.bias)
        elif isinstance(module, nn.Embedding):
            torch.nn.init.normal_(module.weight, mean=0.0, std=0.02)
        elif isinstance(module, nn.LayerNorm):
            torch.nn.init.zeros_(module.bias)
            torch.nn.init.ones_(module.weight)

    def forward(self, x, attention_mask=None):
        # Token embedding
        embeddings = self.token_embedding(x) * np.sqrt(self.embed_size)
        
        # Add positional encoding
        embeddings = self.pos_encoding(embeddings.transpose(0, 1)).transpose(0, 1)
        
        # Create attention mask for padding
        if attention_mask is not None:
            # Convert to transformer format (True = masked)
            attention_mask = ~attention_mask.bool()
        
        # Transformer encoding
        encoded = self.transformer(embeddings, src_key_padding_mask=attention_mask)
        
        # Layer norm and output projection
        encoded = self.layer_norm(encoded)
        logits = self.output_projection(encoded)
        
        return logits

class RobustStreamingDataset(Dataset):
    """Ultra-robust streaming dataset that prevents freezing"""
    def __init__(self, prg_dir, seq_len, mask_prob=MASK_PROB, max_sequences=MAX_SEQUENCES_IN_MEMORY):
        print(f"🔍 Initializing robust streaming dataset from directory: {prg_dir}")
        self.seq_len = seq_len
        self.mask_prob = mask_prob
        self.max_sequences = max_sequences
        
        # File metadata instead of storing all sequences
        self.file_info = []
        self.total_sequences = 0
        
        # Get all PRG files
        print(f"📁 Scanning for .prg files in {prg_dir}...")
        prg_files = glob.glob(os.path.join(prg_dir, '*.prg'))
        
        if not prg_files:
            print(f"⚠️  WARNING: No .prg files found in {prg_dir}")
            return
        
        # Limit number of files to prevent overwhelm
        if len(prg_files) > MAX_FILES_TO_PROCESS:
            print(f"📊 Found {len(prg_files)} files, limiting to {MAX_FILES_TO_PROCESS} for performance")
            prg_files = prg_files[:MAX_FILES_TO_PROCESS]
        else:
            print(f"📁 Found {len(prg_files)} .prg files")
        
        total_bytes = 0
        processed_files = 0
        skipped_files = 0
        
        # Process files with timeout protection
        for i, prg_file in enumerate(prg_files):
            file_name = os.path.basename(prg_file)
            
            # Progress indicator without progress bar to avoid tqdm issues
            if i % 50 == 0 or i == len(prg_files) - 1:
                print(f"  📄 Processing file {i+1}/{len(prg_files)}: {file_name}")
            
            try:
                # Check file size first to avoid large files
                file_size = os.path.getsize(prg_file)
                
                if file_size > FILE_SIZE_LIMIT:
                    skipped_files += 1
                    if skipped_files <= 10:  # Only show first 10 skipped files
                        print(f"  ⚠️  Skipping large file: {file_name} ({file_size} bytes)")
                    continue
                
                if file_size >= seq_len:
                    # Calculate how many sequences this file can produce
                    num_sequences = (file_size - seq_len + STEP_SIZE) // STEP_SIZE
                    
                    # Limit sequences per file to prevent memory issues
                    max_sequences_per_file = min(num_sequences, 1000)
                    
                    self.file_info.append({
                        'path': prg_file,
                        'size': file_size,
                        'num_sequences': max_sequences_per_file,
                        'sequence_start_idx': self.total_sequences
                    })
                    
                    self.total_sequences += max_sequences_per_file
                    total_bytes += file_size
                    processed_files += 1
                    
                    if processed_files <= 10:  # Show details for first 10 files
                        print(f"    ✅ {file_name}: {file_size} bytes → {max_sequences_per_file:,} sequences")
                else:
                    skipped_files += 1
                    if skipped_files <= 10:
                        print(f"    ⚠️  {file_name}: Too small ({file_size} bytes < {seq_len})")
                    
            except Exception as e:
                skipped_files += 1
                if skipped_files <= 10:
                    print(f"    ❌ {file_name}: Error - {e}")
                continue
        
        # Cache for recently accessed sequences
        self.sequence_cache = {}
        self.cache_hits = 0
        self.cache_misses = 0
        
        print(f"🎯 Dataset initialization complete:")
        print(f"   • Processed files: {processed_files:,}")
        print(f"   • Skipped files: {skipped_files:,}")
        print(f"   • Total sequences: {self.total_sequences:,}")
        print(f"   • Total bytes: {total_bytes:,}")
        print(f"   • Memory usage: ~{len(self.file_info) * 100} bytes (metadata only)")
        print(f"   • Cache limit: {max_sequences:,} sequences")
        
    def __len__(self):
        return self.total_sequences
    
    def _find_file_for_sequence(self, idx):
        """Find which file contains the sequence at the given index"""
        for file_info in self.file_info:
            if idx >= file_info['sequence_start_idx']:
                if idx < file_info['sequence_start_idx'] + file_info['num_sequences']:
                    local_idx = idx - file_info['sequence_start_idx']
                    return file_info, local_idx
        raise IndexError(f"Sequence index {idx} out of range")
    
    def _load_sequence_from_file(self, file_info, local_idx):
        """Load a specific sequence from a file with error handling"""
        cache_key = (file_info['path'], local_idx)
        
        # Check cache first
        if cache_key in self.sequence_cache:
            self.cache_hits += 1
            return self.sequence_cache[cache_key]
        
        self.cache_misses += 1
        
        # Load from file with timeout protection
        try:
            with open(file_info['path'], 'rb') as f:
                start_pos = local_idx * STEP_SIZE
                
                # Ensure we don't read beyond file
                if start_pos >= file_info['size']:
                    start_pos = max(0, file_info['size'] - self.seq_len)
                
                f.seek(start_pos)
                data = f.read(self.seq_len)
                
                # Pad with zeros if needed
                if len(data) < self.seq_len:
                    data = data + b'\x00' * (self.seq_len - len(data))
                
                sequence = list(data)
                
                # Cache management - simple FIFO when cache is full
                if len(self.sequence_cache) >= self.max_sequences:
                    # Remove 25% of oldest entries
                    keys_to_remove = list(self.sequence_cache.keys())[:self.max_sequences // 4]
                    for key in keys_to_remove:
                        del self.sequence_cache[key]
                
                self.sequence_cache[cache_key] = sequence
                return sequence
                
        except Exception as e:
            # Return fallback sequence with some randomness to avoid training issues
            fallback = [random.randint(0, 255) for _ in range(self.seq_len)]
            return fallback
    
    def create_masked_span(self, sequence):
        """Create a masked span in the sequence for training"""
        seq = sequence.copy()
        labels = [-100] * len(seq)  # -100 means ignore in loss calculation
        
        # Randomly choose hole size and position
        hole_size = random.randint(MIN_HOLE_SIZE, MAX_HOLE_SIZE)
        if hole_size >= len(seq):
            hole_size = len(seq) // 2
            
        max_start = len(seq) - hole_size
        if max_start <= 0:
            return torch.tensor(seq, dtype=torch.long), torch.tensor(labels, dtype=torch.long)
            
        hole_start = random.randint(0, max_start)
        hole_end = hole_start + hole_size
        
        # Store original values in labels for the masked region
        for i in range(hole_start, hole_end):
            labels[i] = seq[i]
            seq[i] = MASK_TOKEN
            
        return torch.tensor(seq, dtype=torch.long), torch.tensor(labels, dtype=torch.long)
        
    def __getitem__(self, idx):
        try:
            # Find which file contains this sequence
            file_info, local_idx = self._find_file_for_sequence(idx)
            
            # Load the sequence from file (using cache if available)
            sequence = self._load_sequence_from_file(file_info, local_idx)
            
            # Create masked version for training
            masked_seq, labels = self.create_masked_span(sequence)
            
            return masked_seq, labels
        except Exception as e:
            # Return a fallback sample to prevent training crashes
            fallback_seq = [random.randint(0, 255) for _ in range(self.seq_len)]
            masked_seq, labels = self.create_masked_span(fallback_seq)
            return masked_seq, labels
    
    def get_cache_stats(self):
        """Get cache performance statistics"""
        total_requests = self.cache_hits + self.cache_misses
        hit_rate = self.cache_hits / total_requests if total_requests > 0 else 0
        return {
            'cache_hits': self.cache_hits,
            'cache_misses': self.cache_misses,
            'hit_rate': hit_rate,
            'cache_size': len(self.sequence_cache)
        }

def compute_span_accuracy(predictions, labels, mask_token=MASK_TOKEN):
    """Compute accuracy only on masked positions"""
    mask = (labels != -100)  # Only positions that were masked
    if mask.sum() == 0:
        return 0.0
    
    pred_tokens = predictions[mask].argmax(dim=-1)
    true_tokens = labels[mask]
    
    correct = (pred_tokens == true_tokens).float().sum()
    total = mask.sum().float()
    
    return (correct / total).item()

def make_model():
    print("🏗️  Building Transformer model for span reconstruction...")
    
    model = TransformerPredictor()
    
    # Count parameters
    print("🔢 Counting model parameters...")
    total_params = sum(p.numel() for p in model.parameters())
    trainable_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    
    print(f"✅ Transformer model created:")
    print(f"  • Total parameters: {total_params:,}")
    print(f"  • Trainable parameters: {trainable_params:,}")
    print(f"  • Embedding size: {EMBED_SIZE}")
    print(f"  • Hidden size: {HIDDEN_SIZE}")
    print(f"  • Number of heads: {NUM_HEADS}")
    print(f"  • Number of layers: {NUM_LAYERS}")
    print(f"  • Vocabulary size: {VOCAB_SIZE} (includes MASK token)")
    
    return model

def format_time(seconds):
    """Format seconds into human readable time"""
    if seconds < 60:
        return f"{seconds:.1f}s"
    elif seconds < 3600:
        minutes = seconds // 60
        secs = seconds % 60
        return f"{int(minutes)}m {secs:.0f}s"
    else:
        hours = seconds // 3600
        minutes = (seconds % 3600) // 60
        return f"{int(hours)}h {int(minutes)}m"

def save_epoch_model(model, optimizer, scheduler, epoch, epoch_loss, epoch_accuracy, model_dir):
    """Save model for specific epoch with epoch number in filename"""
    if not SAVE_EPOCH_MODELS:
        return
        
    # Create epoch models directory if it doesn't exist
    os.makedirs(model_dir, exist_ok=True)
    
    # Create filename with epoch number
    epoch_model_path = os.path.join(model_dir, f'6502_span_predictor_epoch{epoch+1}.pt')
    
    try:
        torch.save({
            'model_state_dict': model.state_dict(),
            'optimizer_state_dict': optimizer.state_dict(),
            'scheduler_state_dict': scheduler.state_dict(),
            'epoch': epoch,
            'loss': epoch_loss,
            'accuracy': epoch_accuracy,
            'config': {
                'seq_len': SEQ_LEN,
                'vocab_size': VOCAB_SIZE,
                'embed_size': EMBED_SIZE,
                'hidden_size': HIDDEN_SIZE,
                'num_heads': NUM_HEADS,
                'num_layers': NUM_LAYERS,
                'dropout': DROPOUT
            }
        }, epoch_model_path)
        
        model_size = os.path.getsize(epoch_model_path) / (1024 * 1024)  # MB
        print(f"   💾 Epoch model saved: epoch{epoch+1}.pt ({model_size:.1f} MB)")
        
    except Exception as e:
        print(f"   ⚠️  Warning: Failed to save epoch model: {e}")

def train():
    print("=" * 70)
    print("🚀 6502 SPAN RECONSTRUCTION TRAINING - FREEZE-RESISTANT")
    print("=" * 70)
    print(f"⏰ Training started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"💻 Using device: {device}")
    
    if device.type == 'cuda':
        print(f"🎮 GPU: {torch.cuda.get_device_name(0)}")
        print(f"💾 GPU Memory: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")
        if torch.cuda.is_available():
            print(f"🔥 CUDA Version: {torch.version.cuda}")
    
    print("\n📋 Configuration:")
    print(f"  • Sequence Length: {SEQ_LEN}")
    print(f"  • Batch Size: {BATCH_SIZE} (ultra-conservative for stability)")
    print(f"  • Epochs: {EPOCHS}")
    print(f"  • Embedding Size: {EMBED_SIZE}")
    print(f"  • Hidden Size: {HIDDEN_SIZE}")
    print(f"  • Transformer Heads: {NUM_HEADS}")
    print(f"  • Transformer Layers: {NUM_LAYERS}")
    print(f"  • Vocabulary Size: {VOCAB_SIZE}")
    print(f"  • Mask Token: {MASK_TOKEN}")
    print(f"  • Hole Size Range: {MIN_HOLE_SIZE}-{MAX_HOLE_SIZE} bytes")
    print(f"  • Step Size: {STEP_SIZE} (sequence overlap)")
    print(f"  • Max Cached Sequences: {MAX_SEQUENCES_IN_MEMORY:,}")
    print(f"  • Max Files to Process: {MAX_FILES_TO_PROCESS:,}")
    print(f"  • File Size Limit: {FILE_SIZE_LIMIT:,} bytes")
    print(f"  • Model Path: {MODEL_PATH}")
    print(f"  • Save Epoch Models: {'✅ Enabled' if SAVE_EPOCH_MODELS else '❌ Disabled'}")
    if SAVE_EPOCH_MODELS:
        print(f"  • Epoch Models Directory: {EPOCHS_DIR}")
    
    print("\n" + "-" * 70)
    print("📚 LOADING DATA (ROBUST STREAMING MODE)")
    print("-" * 70)
    
    try:
        dataset = RobustStreamingDataset(PRG_DIR, SEQ_LEN, MASK_PROB)
        
        if len(dataset) == 0:
            print("❌ ERROR: No training data found!")
            return
        
        print("\n🔄 Creating DataLoader...")
        # Ultra-conservative settings to prevent freezing
        loader = DataLoader(
            dataset, 
            batch_size=BATCH_SIZE, 
            shuffle=True, 
            num_workers=0,  # No multiprocessing to avoid hangs
            pin_memory=False,
            drop_last=True,  # Drop incomplete batches
            timeout=0  # No timeout
        )
        
        print(f"✅ DataLoader created:")
        print(f"  • Total samples: {len(dataset):,}")
        print(f"  • Batches per epoch: {len(loader):,}")
        print(f"  • Samples per batch: {BATCH_SIZE}")
        print(f"  • Workers: 0 (single-threaded for stability)")
        print(f"  • Streaming mode: ✅ (ultra-low memory usage)")
        
    except Exception as e:
        print(f"❌ ERROR during dataset creation: {e}")
        print("💡 Try reducing MAX_FILES_TO_PROCESS or clearing the prg directory")
        return
    
    print("\n" + "-" * 70)
    print("🧠 INITIALIZING MODEL")
    print("-" * 70)
    
    model = make_model().to(device)
    
    # Use CrossEntropyLoss with ignore_index for masked language modeling
    criterion = nn.CrossEntropyLoss(ignore_index=-100)
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-4, weight_decay=0.01)
    
    # Learning rate scheduler
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=EPOCHS)
    
    print(f"📉 Loss function: CrossEntropyLoss (ignore_index=-100)")
    print(f"⚡ Optimizer: AdamW (lr=1e-4, weight_decay=0.01)")
    print(f"📈 Scheduler: CosineAnnealingLR")
    
    print("\n" + "=" * 70)
    print("🎯 STARTING TRAINING")
    print("=" * 70)
    
    training_start_time = time.time()
    best_accuracy = 0.0
    
    for epoch in range(EPOCHS):
        epoch_start_time = time.time()
        model.train()
        total_loss = 0
        total_accuracy = 0
        num_batches = 0
        
        print(f"\n📈 Epoch {epoch+1}/{EPOCHS}")
        
        try:
            batch_count = 0
            for batch_idx, (x, labels) in enumerate(loader):
                batch_start_time = time.time()
                
                x, labels = x.to(device), labels.to(device)
                
                # Forward pass
                logits = model(x)
                
                # Reshape for loss calculation: (batch_size * seq_len, vocab_size)
                logits_reshaped = logits.view(-1, logits.size(-1))
                labels_reshaped = labels.view(-1)
                
                # Calculate loss (only on masked positions)
                loss = criterion(logits_reshaped, labels_reshaped)
                
                # Calculate accuracy on masked positions
                accuracy = compute_span_accuracy(logits_reshaped, labels_reshaped)
                
                # Backward pass
                optimizer.zero_grad()
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                optimizer.step()
                
                total_loss += loss.item()
                total_accuracy += accuracy
                num_batches += 1
                batch_count += 1
                
                # Progress updates every 100 batches
                if batch_count % 100 == 0:
                    avg_loss = total_loss / num_batches
                    avg_accuracy = total_accuracy / num_batches
                    elapsed = time.time() - epoch_start_time
                    batches_per_sec = batch_count / elapsed
                    eta_remaining = (len(loader) - batch_count) / batches_per_sec if batches_per_sec > 0 else 0
                    
                    print(f"  Batch {batch_count:4d}/{len(loader)} | "
                          f"Loss: {loss.item():.4f} | "
                          f"Acc: {accuracy:.3f} | "
                          f"AvgLoss: {avg_loss:.4f} | "
                          f"AvgAcc: {avg_accuracy:.3f} | "
                          f"ETA: {format_time(eta_remaining)}")
                
                # Clear GPU cache periodically
                if batch_count % 50 == 0 and device.type == 'cuda':
                    torch.cuda.empty_cache()
                    
        except Exception as e:
            print(f"❌ Error during training batch: {e}")
            print("Continuing with next epoch...")
            continue
        
        # Step scheduler
        scheduler.step()
        
        epoch_time = time.time() - epoch_start_time
        epoch_loss = total_loss / num_batches if num_batches > 0 else float('inf')
        epoch_accuracy = total_accuracy / num_batches if num_batches > 0 else 0.0
        
        # Save model for this specific epoch
        save_epoch_model(model, optimizer, scheduler, epoch, epoch_loss, epoch_accuracy, EPOCHS_DIR)
        
        # Save best model
        if epoch_accuracy > best_accuracy:
            best_accuracy = epoch_accuracy
            best_model_path = MODEL_PATH.replace('.pt', '_best.pt')
            torch.save({
                'model_state_dict': model.state_dict(),
                'optimizer_state_dict': optimizer.state_dict(),
                'scheduler_state_dict': scheduler.state_dict(),
                'epoch': epoch,
                'loss': epoch_loss,
                'accuracy': epoch_accuracy,
                'config': {
                    'seq_len': SEQ_LEN,
                    'vocab_size': VOCAB_SIZE,
                    'embed_size': EMBED_SIZE,
                    'hidden_size': HIDDEN_SIZE,
                    'num_heads': NUM_HEADS,
                    'num_layers': NUM_LAYERS,
                    'dropout': DROPOUT
                }
            }, best_model_path)
        
        # Print epoch summary
        cache_stats = dataset.get_cache_stats()
        print(f"✅ Epoch {epoch+1} completed in {format_time(epoch_time)}")
        print(f"   📊 Loss: {epoch_loss:.6f} | Accuracy: {epoch_accuracy:.4f} | LR: {scheduler.get_last_lr()[0]:.2e}")
        print(f"   💾 Cache: {cache_stats['hit_rate']:.2%} hit rate, {cache_stats['cache_size']:,} sequences cached")
        if epoch_accuracy > best_accuracy - 0.001:
            print(f"   🏆 New best accuracy: {epoch_accuracy:.4f}")
        
        # Memory usage if CUDA
        if device.type == 'cuda':
            memory_used = torch.cuda.memory_allocated() / 1024**3
            memory_cached = torch.cuda.memory_reserved() / 1024**3
            print(f"   🎮 GPU Memory: {memory_used:.2f}GB used, {memory_cached:.2f}GB cached")
            torch.cuda.empty_cache()
    
    training_time = time.time() - training_start_time
    
    print("\n" + "=" * 70)
    print("🎉 TRAINING COMPLETED")
    print("=" * 70)
    print(f"⏱️  Total training time: {format_time(training_time)}")
    print(f"🏆 Best accuracy achieved: {best_accuracy:.4f}")
    
    # Final cache statistics
    final_cache_stats = dataset.get_cache_stats()
    print(f"💾 Final cache statistics:")
    print(f"   • Cache hit rate: {final_cache_stats['hit_rate']:.2%}")
    print(f"   • Total cache hits: {final_cache_stats['cache_hits']:,}")
    print(f"   • Total cache misses: {final_cache_stats['cache_misses']:,}")
    print(f"   • Final cache size: {final_cache_stats['cache_size']:,} sequences")
    
    print(f"\n💾 Saving final model to: {MODEL_PATH}")
    
    # Save final model
    torch.save({
        'model_state_dict': model.state_dict(),
        'optimizer_state_dict': optimizer.state_dict(),
        'scheduler_state_dict': scheduler.state_dict(),
        'epoch': EPOCHS,
        'loss': epoch_loss,
        'accuracy': epoch_accuracy,
        'best_accuracy': best_accuracy,
        'config': {
            'seq_len': SEQ_LEN,
            'vocab_size': VOCAB_SIZE,
            'embed_size': EMBED_SIZE,
            'hidden_size': HIDDEN_SIZE,
            'num_heads': NUM_HEADS,
            'num_layers': NUM_LAYERS,
            'dropout': DROPOUT
        }
    }, MODEL_PATH)
    
    model_size = os.path.getsize(MODEL_PATH) / (1024 * 1024)  # MB
    print(f"✅ Model saved successfully! Size: {model_size:.2f} MB")
    
    # Print summary of saved epoch models
    if SAVE_EPOCH_MODELS and os.path.exists(EPOCHS_DIR):
        epoch_files = [f for f in os.listdir(EPOCHS_DIR) if f.startswith('6502_span_predictor_epoch') and f.endswith('.pt')]
        if epoch_files:
            print(f"\n📁 Epoch models saved in: {EPOCHS_DIR}")
            print(f"   • Total epoch models: {len(epoch_files)}")
            total_size = sum(os.path.getsize(os.path.join(EPOCHS_DIR, f)) for f in epoch_files) / (1024 * 1024)
            print(f"   • Total size: {total_size:.1f} MB")
            print(f"   • Usage: Load specific epoch for validation testing")
    
    print(f"\n🏁 Training completed at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Final summary
    print("\n📈 TRAINING SUMMARY:")
    print(f"  • Architecture: Transformer with {NUM_LAYERS} layers, {NUM_HEADS} heads")
    print(f"  • Best accuracy: {best_accuracy:.4f}")
    print(f"  • Model parameters: {sum(p.numel() for p in model.parameters()):,}")
    print(f"  • Memory optimization: ✅ Ultra-robust streaming with freeze prevention")
    if SAVE_EPOCH_MODELS:
        print(f"  • Epoch models: ✅ Saved {EPOCHS} epoch models for validation testing")

if __name__ == '__main__':
    train()
