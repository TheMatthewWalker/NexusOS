#!/usr/bin/env python3
"""Deterministic Ollama-compatible mock; supports fixed-length and HTTP chunked bodies."""
import hashlib
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_REQUEST = 100000
EMBED_DIMENSIONS = 64
# Words in one group share a dimension, so "bill" is close to "invoice".
EMBED_CONCEPTS = [("invoice", "bill", "receipt", "payment"), ("holiday", "vacation", "trip", "travel")]


def mock_embedding(text):
    """Bag-of-words vector: deterministic across runs (no Python hash seed)."""
    text = re.sub(r"^search_(document|query): ", "", text)
    vector = [0.0] * EMBED_DIMENSIONS
    for word in re.findall(r"[a-z0-9]+", text.lower()):
        slot = next((i for i, group in enumerate(EMBED_CONCEPTS) if word in group), None)
        if slot is None:
            digest = hashlib.sha256(word.encode()).digest()
            slot = len(EMBED_CONCEPTS) + int.from_bytes(digest[:4], "big") % (EMBED_DIMENSIONS - len(EMBED_CONCEPTS))
        vector[slot] += 1.0
    return vector

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def read_body(self):
        if "chunked" in self.headers.get("Transfer-Encoding", "").lower():
            chunks = []
            size_total = 0
            while True:
                header = self.rfile.readline(128).strip()
                chunk_size = int(header.split(b";", 1)[0], 16)
                if chunk_size == 0:
                    while self.rfile.readline(4096).strip():
                        pass
                    break
                size_total += chunk_size
                if size_total > MAX_REQUEST:
                    raise ValueError("Request too large")
                data = self.rfile.read(chunk_size)
                if len(data) != chunk_size or self.rfile.read(2) != b"\r\n":
                    raise ValueError("Malformed chunk")
                chunks.append(data)
            return b"".join(chunks)
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > MAX_REQUEST:
            raise ValueError("Invalid content length")
        return self.rfile.read(length)

    def do_GET(self):
        if self.path != "/api/tags":
            self.send_error(404)
            return
        content = b'{"models":[]}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self):
        if self.path not in ("/api/generate", "/api/embed", "/v1/chat/completions"):
            self.send_error(404)
            return
        try:
            body = json.loads(self.read_body())
            if not body.get("model"):
                raise ValueError("Missing model")
            if self.path == "/api/embed":
                inputs = body.get("input")
                if isinstance(inputs, str):
                    inputs = [inputs]
                if not isinstance(inputs, list) or not inputs or not all(isinstance(x, str) for x in inputs):
                    raise ValueError("Missing input")
                content = json.dumps({"model": body["model"], "embeddings": [mock_embedding(x) for x in inputs]}).encode()
            elif self.path == "/v1/chat/completions":
                message = body.get("messages", [{}])[0].get("content", "")
                content = json.dumps({"choices":[{"message":{"role":"assistant","content":"Nexus llama mock: " + message}}]}).encode()
            elif body.get("keep_alive") == 0 and "prompt" not in body:
                content = json.dumps({"response": "", "done": True}).encode()
            elif isinstance(body.get("prompt"), str):
                content = json.dumps({"response": "Nexus mock response: " + body["prompt"]}).encode()
            else:
                raise ValueError("Missing prompt")
        except (ValueError, UnicodeDecodeError) as exc:
            print(f"Rejected mock request: {exc}; headers: {dict(self.headers)}", flush=True)
            self.send_error(400)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 11435), Handler).serve_forever()
