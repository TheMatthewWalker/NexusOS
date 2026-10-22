#!/usr/bin/env python3
"""Single source of truth for Guardian options, rendering and identities (ADR-002).

The allowlist lives in schema/guardian-options.json and is shared with Nexus
Core; schema/guardian-vectors.json pins the exact bytes and IDs both
implementations must produce.
"""
import hashlib
import json
import os
import re
from pathlib import Path

SCHEMA_PATH = Path(os.environ.get("NEXUS_GUARDIAN_SCHEMA",
                                  Path(__file__).resolve().parents[2] / "schema" / "guardian-options.json"))
STORE_SYSTEM = re.compile(r"/nix/store/[0-9a-df-np-sv-z]{32}-nixos-system-[A-Za-z0-9+._-]+\Z")
STORE_PATH = re.compile(r"/nix/store/[0-9a-df-np-sv-z]{32}-[A-Za-z0-9+._?=-]+\Z")
DRV_PATH = re.compile(r"/nix/store/[0-9a-df-np-sv-z]{32}-[A-Za-z0-9+._?=-]+\.drv\Z")
# A flake reference plus attribute, e.g. "path:/etc/nixos#nixosConfigurations.host".
HOST_CONFIG = re.compile(r"[A-Za-z0-9+._:/?=&-]{1,200}#nixosConfigurations\.[A-Za-z0-9_-]{1,64}\Z")
ID_LENGTH = 24


def load_schema(path=SCHEMA_PATH):
    with open(path, encoding="utf-8") as stream:
        schema = json.load(stream)
    if schema.get("schemaVersion") != 1 or not isinstance(schema.get("options"), dict):
        raise ValueError("Unsupported Guardian schema")
    return schema


SCHEMA = load_schema()


def normalise(changes, schema=SCHEMA):
    """Validate a change list and return {option: bool} with strict typing."""
    if not isinstance(changes, list) or not 1 <= len(changes) <= schema["maxChanges"]:
        raise ValueError(f"Expected 1 to {schema['maxChanges']} changes")
    values = {}
    for change in changes:
        if not isinstance(change, dict) or set(change) != {"option", "enabled"}:
            raise ValueError("Invalid change shape")
        key, enabled = change["option"], change["enabled"]
        spec = schema["options"].get(key) if isinstance(key, str) else None
        if spec is None or key in values:
            raise ValueError("Unsupported or duplicate option")
        if spec["type"] != "bool" or type(enabled) is not bool:
            raise ValueError("Option value has the wrong type")
        values[key] = enabled
    return values


def render(changes, schema=SCHEMA):
    values = normalise(changes, schema)
    lines = [f"  {key} = {'true' if value else 'false'};" for key, value in sorted(values.items())]
    return "{\n" + "\n".join(lines) + "\n}\n"


def risk(changes, schema=SCHEMA):
    values = normalise(changes, schema)
    notes = [schema["options"][key]["riskNote"] for key, value in sorted(values.items())
             if schema["options"][key]["risk"] == "elevated" and schema["options"][key].get("elevatedWhen") == value]
    return {"level": "elevated", "notes": notes} if notes else {"level": "review", "notes": []}


def _digest(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:ID_LENGTH]


def plan_id(module_text):
    return _digest("nexus-guardian-plan-v1\n" + module_text)


def require_baseline(baseline):
    if not isinstance(baseline, str) or STORE_SYSTEM.fullmatch(baseline) is None:
        raise ValueError("Baseline must be an observed /nix/store NixOS system path")
    return baseline


def require_host_config(host_config):
    if not isinstance(host_config, str) or HOST_CONFIG.fullmatch(host_config) is None or ".." in host_config:
        raise ValueError("Host configuration must be '<flake-ref>#nixosConfigurations.<name>'")
    return host_config


def candidate_id(baseline, host_config, module_text):
    require_baseline(baseline)
    require_host_config(host_config)
    return _digest("nexus-guardian-candidate-v1\n" + baseline + "\n" + host_config + "\n" + module_text)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")
