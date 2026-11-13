#!/usr/bin/env python3
"""Minimal stand-in for llama-server used by Core process-lifecycle tests."""
import argparse
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--model", required=True)
parser.add_argument("--host", required=True)
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--ctx-size", type=int, required=True)
parser.add_argument("--n-gpu-layers", type=int, required=True)
# FAKE_LLAMA_OLD=1 stands in for a llama-server too old to know --jinja: argparse exits on it.
if os.environ.get("FAKE_LLAMA_OLD") != "1":
    parser.add_argument("--jinja", action="store_true")
# FAKE_LLAMA_NO_MOE=1: a llama-server from before --n-cpu-moe.
if os.environ.get("FAKE_LLAMA_NO_MOE") != "1":
    parser.add_argument("--n-cpu-moe", type=int, default=0)
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

    def send_body(self, status, body, kind):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        request = json.loads(self.body())
        jinja = getattr(args, "jinja", False)
        # As llama-server: tools need --jinja, otherwise the request is refused.
        if request.get("tools") and not jinja:
            self.send_body(500, b'{"error":{"code":500,"message":"tools param requires --jinja flag"}}', "application/json")
            return
        if request.get("stream"):
            said = f"jinja={str(jinja).lower()} tools={len(request.get('tools') or [])}"
            events = [{"choices": [{"delta": {"content": said}}]}, {"choices": [{"delta": {}, "finish_reason": "stop"}]}]
            body = "".join(f"data: {json.dumps(e)}\n\n" for e in events) + "data: [DONE]\n\n"
            self.send_body(200, body.encode(), "text/event-stream")
            return
        text = request["messages"][0]["content"]
        moe = getattr(args, "n_cpu_moe", 0)
        extra = (f" moe={moe}" if moe else "") + (f" effort={request['chat_template_kwargs']['reasoning_effort']}"
                                                  if request.get("chat_template_kwargs") else "")
        self.reply({"choices": [{"message": {"content": f"managed ctx={args.ctx_size} ngl={args.n_gpu_layers}{extra}: {text}"}}]})


ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()
