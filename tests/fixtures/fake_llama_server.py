#!/usr/bin/env python3
"""Minimal stand-in for llama-server used by Core process-lifecycle tests."""
import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--model", required=True)
parser.add_argument("--host", required=True)
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--ctx-size", type=int, required=True)
parser.add_argument("--n-gpu-layers", type=int, required=True)
args = parser.parse_args()
if args.host != "127.0.0.1":
    raise SystemExit("refusing non-loopback bind")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, payload):
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self.reply({"status": "ok"})
        else:
            self.send_error(404)

    def body(self):
        if "chunked" not in self.headers.get("Transfer-Encoding", "").lower():
            return self.rfile.read(int(self.headers.get("Content-Length", "0")))
        data = b""
        while True:
            size = int(self.rfile.readline().split(b";", 1)[0], 16)
            if size == 0:
                while self.rfile.readline().strip():
                    pass
                return data
            data += self.rfile.read(size)
            self.rfile.read(2)

    def do_POST(self):
        request = json.loads(self.body())
        text = request["messages"][0]["content"]
        self.reply({"choices": [{"message": {"content": f"managed ctx={args.ctx_size} ngl={args.n_gpu_layers}: {text}"}}]})


ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()
