#!/usr/bin/env python3
"""Opt-in local Ollama benchmark: reports observed speed without fabricating GPU metrics."""
import argparse
import json
import statistics
import time
import urllib.parse
import urllib.request

def validate_url(raw):
    url = urllib.parse.urlsplit(raw)
    if url.scheme != "http" or url.hostname not in ("localhost", "127.0.0.1", "::1") or url.username or url.password:
        raise ValueError("Only local Ollama HTTP endpoints are permitted")
    return raw.rstrip("/")

def tokens_per_second(payload):
    count, duration = payload.get("eval_count"), payload.get("eval_duration")
    if isinstance(count, int) and isinstance(duration, int) and duration > 0:
        return round(count / (duration / 1e9), 2)
    return None

def query(base, model, prompt, keep_alive="5m"):
    body = json.dumps({"model": model, "prompt": prompt, "stream": False, "keep_alive": keep_alive}).encode()
    request = urllib.request.Request(base + "/api/generate", data=body,
                                     headers={"Content-Type": "application/json"}, method="POST")
    start = time.monotonic()
    with urllib.request.urlopen(request, timeout=240) as response:
        data = response.read(4 * 1024 * 1024 + 1)
    if len(data) > 4 * 1024 * 1024:
        raise ValueError("Model response too large")
    return round(time.monotonic() - start, 3), json.loads(data)

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://127.0.0.1:11434")
    p.add_argument("--model", required=True)
    p.add_argument("--prompt", default="Explain Linux systemd in one paragraph.")
    p.add_argument("--runs", type=int, default=3)
    args = p.parse_args()
    if not 1 <= args.runs <= 10:
        p.error("--runs must be between 1 and 10")
    base = validate_url(args.url)
    durations, rates = [], []
    for i in range(args.runs):
        duration, data = query(base, args.model, args.prompt)
        rate = tokens_per_second(data)
        durations.append(duration)
        if rate is not None: rates.append(rate)
        print(f"run={i+1} elapsed_s={duration} output_tokens_per_s={rate if rate is not None else 'unknown'}")
    print(json.dumps({"model": args.model, "runs": args.runs,
                      "median_elapsed_s": statistics.median(durations),
                      "median_output_tokens_per_s": statistics.median(rates) if rates else None}, indent=2))

if __name__ == "__main__":
    main()
