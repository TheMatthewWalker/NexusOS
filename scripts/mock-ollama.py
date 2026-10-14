#!/usr/bin/env python3
"""Deterministic Ollama-compatible server for Nexus Core CI, no GPU/network API keys."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/api/generate":
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length > 100000:
                self.send_error(413)
                return
            body = json.loads(self.rfile.read(length))
            if not isinstance(body.get("prompt"), str) or not body.get("model"):
                self.send_error(400)
                return
            content = json.dumps({"response": "Nexus mock response: " + body["prompt"]}).encode()
        except (ValueError, json.JSONDecodeError, UnicodeDecodeError):
            self.send_error(400)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 11435), Handler).serve_forever()
