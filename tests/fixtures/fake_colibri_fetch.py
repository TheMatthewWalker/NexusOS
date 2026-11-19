#!/usr/bin/env python3
"""Stand-in for nexus-colibri-model: 'downloads' at once, or lists one model."""
import argparse
import json
import os
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument("--dir", required=True)
parser.add_argument("--model")
parser.add_argument("--list", action="store_true")
args = parser.parse_args()
if args.list:
    json.dump([{"id": "qwen36-35b", "name": "Qwen3.6-35B-A3B", "downloadGb": 23.1, "fits": True, "why": None}], sys.stdout)
    sys.exit(0)
time.sleep(0.2)
folder = os.path.join(args.dir, args.model)
os.makedirs(folder, exist_ok=True)
with open(os.path.join(folder, ".colibri-download.json"), "w") as f:
    json.dump({"complete": True}, f)
with open(os.path.join(args.dir, args.model + ".status.json"), "w") as f:
    json.dump({"state": "ready", "message": "downloaded", "done": 10, "total": 10, "percent": 100}, f)
