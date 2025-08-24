#!/usr/bin/env python3
"""
Export a PyTorch 6502 span predictor checkpoint (.pt) to ONNX.

Supports two modes:
  1) TorchScript checkpoint: pass --pt path/to/model.pt and it will export directly.
  2) State dict checkpoint: pass --pt path/to/weights.pt along with --factory module_or_file:callable
     which must build and return an initialized nn.Module with the same architecture.

Defaults are tailored for the 6502 span predictor described in WebUse.md:
  - Input: tokens [1, 128] int64 (values 0..256; 256 is MASK)
  - Output: logits [1, 128, 257]

Examples
  # A) TorchScript .pt (saved via torch.jit.script/trace)
  python scripts/export_onnx.py \
    --pt models/6502_span_predictor_epoch25.pt \
    --out models/6502_span_predictor_epoch25.onnx

  # B) state_dict .pt with a factory function in a local Python file
  #    my_model.py must define create_model() -> nn.Module
  python scripts/export_onnx.py \
    --pt models/6502_span_predictor_epoch25.pt \
    --factory my_model.py:create_model \
    --out models/6502_span_predictor_epoch25.onnx

Notes
  - If your factory requires constructor args, provide a JSON string via --factory-args.
  - Use --seq-len and --vocab to adjust shapes if your training config differs.
"""

from __future__ import annotations

import argparse
import importlib
import importlib.util
import json
import os
import sys
from typing import Any, Callable, Optional, Tuple, List
import glob


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Export PyTorch .pt to ONNX")
    # Single-file mode
    p.add_argument("--pt", required=False, help="Path to .pt checkpoint (TorchScript or state_dict)")
    p.add_argument("--out", required=False, help="Output .onnx path")
    p.add_argument("--factory", default=None,
                   help="Model factory in the form module_or_file:callable (required for state_dict)")
    p.add_argument("--factory-args", default=None,
                   help='JSON object with args for the factory callable, e.g. "{\"vocab_size\":257,\"seq_len\":128}"')
    p.add_argument("--seq-len", type=int, default=128, help="Sequence length (default: 128)")
    p.add_argument("--vocab", type=int, default=257, help="Vocabulary size (default: 257 incl. MASK=256)")
    p.add_argument("--opset", type=int, default=17, help="ONNX opset version (default: 17)")
    p.add_argument("--no-check", action="store_true", help="Skip ONNX checker after export")
    # Batch mode
    p.add_argument("--batch-dir", default=None, help="Directory containing .pt files to export in batch mode")
    p.add_argument("--batch-pattern", default="*.pt", help="Glob pattern to match .pt files (default: *.pt)")
    p.add_argument("--out-dir", default=None, help="Output directory for ONNX files in batch mode (defaults to batch-dir)")
    p.add_argument("--skip-existing", action="store_true", help="Skip export if destination .onnx already exists")
    return p.parse_args()


def _import_callable(spec: str) -> Callable[..., Any]:
    """Import a callable from a module path or a .py file, given 'path_or_module:attr'."""
    if ":" not in spec:
        raise ValueError("--factory must be 'module_or_file:callable'")
    mod_path, attr = spec.split(":", 1)

    if mod_path.endswith(".py"):
        # Load from a file path
        mod_name = os.path.splitext(os.path.basename(mod_path))[0]
        spec_obj = importlib.util.spec_from_file_location(mod_name, mod_path)
        if spec_obj is None or spec_obj.loader is None:
            raise ImportError(f"Cannot load module from file: {mod_path}")
        module = importlib.util.module_from_spec(spec_obj)
        sys.modules[mod_name] = module
        spec_obj.loader.exec_module(module)  # type: ignore[attr-defined]
    else:
        # Load via regular module path
        module = importlib.import_module(mod_path)

    fn = getattr(module, attr, None)
    if fn is None or not callable(fn):
        raise AttributeError(f"Callable '{attr}' not found in '{mod_path}'")
    return fn


def _try_load_torchscript(pt_path: str):
    import torch
    try:
        m = torch.jit.load(pt_path, map_location="cpu")
        m.eval()
        return m
    except Exception:
        return None


def _load_state_dict(pt_path: str):
    import torch
    obj = torch.load(pt_path, map_location="cpu")
    # If the object is an nn.Module instance, return it so we can export directly
    try:
        import torch.nn as nn
        if isinstance(obj, nn.Module):
            obj.eval()
            return obj  # special case: return module instance
    except Exception:
        pass
    # Common patterns: a pure state_dict (ordered dict of tensors) or a dict with a state dict under a known key
    if isinstance(obj, dict):
        for key in ("state_dict", "model_state_dict", "model"):  # try common container keys
            if key in obj and isinstance(obj[key], dict):
                return obj[key]
    if isinstance(obj, dict):
        # Heuristic: dict of tensors
        if all(hasattr(v, "shape") for v in obj.values()):
            return obj
    # If we get here, it's likely not a state_dict we recognize
    return None


def build_model_from_factory(factory_spec: str, factory_args_json: Optional[str]):
    factory = _import_callable(factory_spec)
    kwargs = {}
    if factory_args_json:
        try:
            kwargs = json.loads(factory_args_json)
            if not isinstance(kwargs, dict):
                raise ValueError("--factory-args must be a JSON object")
        except json.JSONDecodeError as e:
            raise ValueError(f"Invalid JSON for --factory-args: {e}")
    model = factory(**kwargs)
    try:
        import torch
        model.eval()
        return model
    except Exception:
        return model


def _export_one(pt_path: str, out_path: str, args: argparse.Namespace) -> bool:
    """Export a single .pt file to ONNX. Returns True on success."""
    # Lazy import heavy deps here
    import torch

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)

    # 1) Try TorchScript path first
    model = _try_load_torchscript(pt_path)
    loaded_mode = "torchscript" if model is not None else None

    # 2) Otherwise, attempt state_dict + factory (with sensible fallback)
    if model is None:
        state_or_module = _load_state_dict(pt_path)
        if state_or_module is None:
            print(f"[error] {pt_path}: not TorchScript and not a recognizable state_dict")
            return False
        try:
            import torch.nn as nn
            if isinstance(state_or_module, nn.Module):
                model = state_or_module
                loaded_mode = "eager_module"
            else:
                factory_spec = args.factory
                # Fallback: try local factory if not provided
                if not factory_spec:
                    # Prefer the in-repo factory for the 6502 span predictor
                    repo_factory = os.path.join(os.path.dirname(__file__), "pt_factory_6502.py")
                    if os.path.isfile(repo_factory):
                        factory_spec = f"{repo_factory}:create_model"
                if not factory_spec:
                    print(f"[error] {pt_path}: state_dict requires --factory module_or_file:callable (no default found)")
                    return False

                model = build_model_from_factory(factory_spec, args.factory_args)
                try:
                    model.load_state_dict(state_or_module, strict=False)
                except Exception as e:
                    print(f"[warn] {pt_path}: load_state_dict(strict=False) failed: {e}")
                    try:
                        model.load_state_dict(state_or_module, strict=True)
                    except Exception as e2:
                        print(f"[error] {pt_path}: load_state_dict(strict=True) also failed: {e2}")
                        return False
                loaded_mode = "state_dict"
        except Exception as e:
            print(f"[error] {pt_path}: unexpected error while handling state/module: {e}")
            return False

    assert model is not None
    model.eval()

    # 3) Prepare a representative dummy input
    seq_len = int(args.seq_len)
    vocab = int(args.vocab)
    dummy = torch.randint(low=0, high=vocab, size=(1, seq_len), dtype=torch.long)

    # 4) Export to ONNX
    input_names = ["tokens"]
    output_names = ["logits"]
    dynamic_axes = {"tokens": {0: "batch"}, "logits": {0: "batch"}}

    print(f"[info] Exporting ({loaded_mode}) -> ONNX: {out_path}")
    torch.onnx.export(
        model,
        dummy,
        out_path,
        export_params=True,
        opset_version=int(args.opset),
        do_constant_folding=True,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
    )

    if not args.no_check:
        try:
            import onnx
            m = onnx.load(out_path)
            onnx.checker.check_model(m)
            print("[ok] ONNX model passed checker.")
        except Exception as e:
            print(f"[warn] ONNX checker reported an issue: {e}")
    return True


def main() -> None:
    args = parse_args()

    # Determine mode
    if args.batch_dir:
        # Batch mode
        bdir = os.path.abspath(args.batch_dir)
        if not os.path.isdir(bdir):
            print(f"[error] Batch dir not found: {bdir}")
            sys.exit(2)
        pattern = os.path.join(bdir, args.batch_pattern)
        pt_files = sorted(glob.glob(pattern))
        if not pt_files:
            print(f"[warn] No files matched pattern: {pattern}")
            sys.exit(1)
        out_dir = os.path.abspath(args.out_dir or bdir)
        os.makedirs(out_dir, exist_ok=True)

        ok = 0
        fail = 0
        for pt in pt_files:
            base = os.path.splitext(os.path.basename(pt))[0]
            out = os.path.join(out_dir, base + ".onnx")
            if args.skip_existing and os.path.isfile(out):
                print(f"[skip] {out} already exists")
                ok += 1
                continue
            if _export_one(pt, out, args):
                ok += 1
            else:
                fail += 1

        print(f"[done] Batch export complete. Success: {ok}, Failed: {fail}")
        sys.exit(0 if fail == 0 else 3)

    # Single-file mode (backward compatible)
    if not args.pt or not args.out:
        print("[error] In single-file mode you must provide --pt and --out. Or use --batch-dir for batch mode.")
        sys.exit(2)

    success = _export_one(args.pt, args.out, args)
    if not success:
        sys.exit(2)
    print("[done] Export complete.")


if __name__ == "__main__":
    main()
