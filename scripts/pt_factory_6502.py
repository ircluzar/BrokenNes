import math
import torch
import torch.nn as nn


class PositionalEncoding(nn.Module):
    def __init__(self, d_model: int, max_len: int = 1024, dropout: float = 0.0):
        super().__init__()
        self.dropout = nn.Dropout(p=dropout)
        pe = torch.zeros(max_len, d_model)
        position = torch.arange(0, max_len, dtype=torch.float).unsqueeze(1)
        div_term = torch.exp(torch.arange(0, d_model, 2).float() * (-math.log(10000.0) / d_model))
        pe[:, 0::2] = torch.sin(position * div_term)
        pe[:, 1::2] = torch.cos(position * div_term)
        pe = pe.unsqueeze(1)  # [T, 1, C]
        self.register_buffer("pe", pe)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: [B, T, C]; self.pe: [T, 1, C]
        T = x.size(1)
        pe = self.pe[:T].transpose(0, 1)  # [1, T, C]
        x = x + pe
        return self.dropout(x)


class SpanPredictor(nn.Module):
    def __init__(self, vocab_size: int = 257, seq_len: int = 128,
                 embed_size: int = 256, hidden_size: int = 512,
                 num_heads: int = 8, num_layers: int = 6, dropout: float = 0.1):
        super().__init__()
        self.vocab_size = vocab_size
        self.seq_len = seq_len
        self.embed_size = embed_size

        self.token_embedding = nn.Embedding(vocab_size, embed_size)
        self.pos_encoding = PositionalEncoding(embed_size, max_len=seq_len, dropout=dropout)

        encoder_layer = nn.TransformerEncoderLayer(
            d_model=embed_size,
            nhead=num_heads,
            dim_feedforward=hidden_size,
            dropout=dropout,
            batch_first=True,
        )
        self.transformer = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)
        self.layer_norm = nn.LayerNorm(embed_size)
        self.output_projection = nn.Linear(embed_size, vocab_size)

    def forward(self, tokens: torch.Tensor) -> torch.Tensor:
        # tokens: [B, T] int64
        x = self.token_embedding(tokens)  # [B, T, C]
        x = self.pos_encoding(x)
        x = self.transformer(x)  # [B, T, C]
        x = self.layer_norm(x)
        logits = self.output_projection(x)  # [B, T, V]
        return logits


def create_model(vocab_size: int = 257, seq_len: int = 128,
                 embed_size: int = 256, hidden_size: int = 512,
                 num_heads: int = 8, num_layers: int = 6, dropout: float = 0.1):
    return SpanPredictor(
        vocab_size=vocab_size,
        seq_len=seq_len,
        embed_size=embed_size,
        hidden_size=hidden_size,
        num_heads=num_heads,
        num_layers=num_layers,
        dropout=dropout,
    )
