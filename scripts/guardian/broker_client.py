#!/usr/bin/env python3
"""Minimal command-line client for the Guardian broker socket (diagnostics and tests).

  broker_client.py [--socket PATH] OPERATION JSON-PAYLOAD
"""
import argparse
import json
import sys

from root_review_broker import request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--socket", default="/run/nexus-guardian/review.sock")
    parser.add_argument("operation", choices=("submit", "review", "status"))
    parser.add_argument("payload")
    args = parser.parse_args()
    try:
        response = request(args.socket, args.operation, json.loads(args.payload))
    except PermissionError:
        print(json.dumps({"ok": False, "error": "permission denied connecting to broker"}))
        return 3
    except (ConnectionError, OSError) as error:
        print(json.dumps({"ok": False, "error": f"no response ({type(error).__name__})"}))
        return 4
    print(json.dumps(response, sort_keys=True))
    return 0 if response.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
