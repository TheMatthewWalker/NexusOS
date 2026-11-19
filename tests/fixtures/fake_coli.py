#!/usr/bin/env python3
"""Stand-in for colibri's `coli serve`, for Core's very-large-model tests."""
import argparse
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("command", choices=["serve"])
parser.add_argument("--model", required=True)
parser.add_argument("--host", required=True)
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--model-id", required=True)
args = parser.parse_args()
if args.host != "127.0.0.1":
    raise SystemExit("refusing non-loopback bind")
# As coli: a model folder that is not a whole download does not start.
if not os.path.exists(os.path.join(args.model, ".colibri-download.json")):
    raise SystemExit("no model at " + args.model)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send_body(self, body, kind):
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/v1/models":
            self.send_body(json.dumps({"data": [{"id": args.model_id}]}).encode(), "application/json")
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
        request = json.loads(self.body() or b"{}")
        said = f"colibri {os.path.basename(args.model)} as {request.get('model')}"
        if request.get("stream"):
            said += f" tools={len(request.get('tools') or [])}"
            events = [{"choices": [{"delta": {"content": said}}]}, {"choices": [{"delta": {}, "finish_reason": "stop"}]}]
            self.send_body(("".join(f"data: {json.dumps(e)}\n\n" for e in events) + "data: [DONE]\n\n").encode(), "text/event-stream")
            return
        self.send_body(json.dumps({"choices": [{"message": {"content": said}}]}).encode(), "application/json")


ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()
