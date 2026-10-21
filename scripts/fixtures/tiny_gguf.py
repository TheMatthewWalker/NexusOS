#!/usr/bin/env python3
"""Write a tiny, randomly initialised llama-architecture GGUF model.

The weights are synthetic, so output is meaningless text. The file exists so
real inference engines (Ollama, llama.cpp) can be exercised end to end without
downloading model weights. It is test fixture material, never a usable model.
Requires the `gguf` and `numpy` packages.
"""
import argparse
from pathlib import Path

import gguf
import numpy as np

EMBED = 64
HEADS = 4
LAYERS = 2
FFN = 128
CONTEXT = 512


def vocabulary():
    tokens, scores, types = ["<unk>", "<s>", "</s>"], [0.0, 0.0, 0.0], [2, 3, 3]
    for byte in range(256):
        tokens.append(f"<0x{byte:02X}>")
        scores.append(0.0)
        types.append(6)  # byte-fallback token
    for index, piece in enumerate(["▁"] + [chr(c) for c in range(ord("a"), ord("z") + 1)]):
        tokens.append(piece)
        scores.append(-float(index))
        types.append(1)
    return tokens, scores, types


def write(path: Path, seed: int = 7):
    rng = np.random.default_rng(seed)
    tokens, scores, types = vocabulary()
    vocab = len(tokens)
    writer = gguf.GGUFWriter(str(path), "llama")
    writer.add_name("nexus-tiny-synthetic")
    writer.add_context_length(CONTEXT)
    writer.add_embedding_length(EMBED)
    writer.add_block_count(LAYERS)
    writer.add_feed_forward_length(FFN)
    writer.add_head_count(HEADS)
    writer.add_head_count_kv(HEADS)
    writer.add_rope_dimension_count(EMBED // HEADS)
    writer.add_layer_norm_rms_eps(1e-5)
    writer.add_file_type(gguf.LlamaFileType.ALL_F32)
    writer.add_tokenizer_model("llama")
    writer.add_token_list(tokens)
    writer.add_token_scores(scores)
    writer.add_token_types(types)
    writer.add_bos_token_id(1)
    writer.add_eos_token_id(2)
    writer.add_unk_token_id(0)
    writer.add_add_bos_token(True)
    writer.add_add_eos_token(False)

    def tensor(name, *shape):
        writer.add_tensor(name, (rng.standard_normal(shape) * 0.02).astype(np.float32))

    tensor("token_embd.weight", vocab, EMBED)
    for layer in range(LAYERS):
        writer.add_tensor(f"blk.{layer}.attn_norm.weight", np.ones(EMBED, dtype=np.float32))
        for part in ("attn_q", "attn_k", "attn_v", "attn_output"):
            tensor(f"blk.{layer}.{part}.weight", EMBED, EMBED)
        writer.add_tensor(f"blk.{layer}.ffn_norm.weight", np.ones(EMBED, dtype=np.float32))
        tensor(f"blk.{layer}.ffn_gate.weight", FFN, EMBED)
        tensor(f"blk.{layer}.ffn_up.weight", FFN, EMBED)
        tensor(f"blk.{layer}.ffn_down.weight", EMBED, FFN)
    writer.add_tensor("output_norm.weight", np.ones(EMBED, dtype=np.float32))
    tensor("output.weight", vocab, EMBED)

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit("Refusing to overwrite an existing file")
    write(args.output)


if __name__ == "__main__":
    main()
