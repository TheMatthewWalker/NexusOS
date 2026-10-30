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
# Settings an option brings with it (alsoSetWhenEnabled): a plain option path
# and a true/false or a short plain word, so they render without quoting.
FIXED_PATH = re.compile(r"[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){1,7}\Z")
FIXED_WORD = re.compile(r"[A-Za-z0-9_-]{1,64}\Z")


def load_schema(path=SCHEMA_PATH):
    with open(path, encoding="utf-8") as stream:
        schema = json.load(stream)
    if schema.get("schemaVersion") != 1 or not isinstance(schema.get("options"), dict):
        raise ValueError("Unsupported Guardian schema")
    seen = set()
    for spec in schema["options"].values():
        for key, value in spec.get("alsoSetWhenEnabled", {}).items():
            # Never proposable on their own, and set by one option only.
            if FIXED_PATH.fullmatch(key) is None or key in schema["options"] or key in seen:
                raise ValueError("Invalid fixed setting in Guardian schema")
            if not (type(value) is bool or (isinstance(value, str) and FIXED_WORD.fullmatch(value))):
                raise ValueError("Invalid fixed setting in Guardian schema")
            seen.add(key)
    return schema


SCHEMA = load_schema()


def package_attr(attr, schema=SCHEMA):
    """A nixpkgs attribute path such as "hello" or "python3Packages.requests", or ValueError.

    Only plain identifiers are accepted: no flake references, store paths,
    quoting or Nix syntax, so the rendered module can only select from pkgs.
    """
    spec = schema.get("packages")
    if spec is None or not isinstance(attr, str) or re.fullmatch(spec["attrPattern"], attr, re.ASCII) is None:
        raise ValueError("Package must be a plain nixpkgs attribute name")
    if any(segment in spec["reservedWords"] for segment in attr.split(".")):
        raise ValueError("Package must be a plain nixpkgs attribute name")
    return attr


def split(changes, schema=SCHEMA):
    """Validate a change list; return ({option: bool}, sorted packages) with strict typing.

    A change is either {"option": <allowlisted option>, "enabled": <bool>}
    or {"package": <nixpkgs attribute>}.
    """
    if not isinstance(changes, list) or not 1 <= len(changes) <= schema["maxChanges"]:
        raise ValueError(f"Expected 1 to {schema['maxChanges']} changes")
    values, packages = {}, set()
    for change in changes:
        if isinstance(change, dict) and set(change) == {"package"}:
            attr = package_attr(change["package"], schema)
            if attr in packages:
                raise ValueError("Duplicate package")
            packages.add(attr)
            continue
        if not isinstance(change, dict) or set(change) != {"option", "enabled"}:
            raise ValueError("Invalid change shape")
        key, enabled = change["option"], change["enabled"]
        spec = schema["options"].get(key) if isinstance(key, str) else None
        if spec is None or key in values:
            raise ValueError("Unsupported or duplicate option")
        if spec["type"] != "bool" or type(enabled) is not bool:
            raise ValueError("Option value has the wrong type")
        values[key] = enabled
    return values, sorted(packages)


def normalise(changes, schema=SCHEMA):
    """Validate a change list and return {option: bool} with strict typing."""
    return split(changes, schema)[0]


def _nix(value):
    if type(value) is bool:
        return "true" if value else "false"
    return '"' + value + '"'


def settings(values, schema=SCHEMA):
    """Every option the module sets: the changes plus what turning one on always brings
    (for example key-only SSH with services.openssh.enable)."""
    out = dict(values)
    for key, value in values.items():
        if value is True:
            out.update(schema["options"][key].get("alsoSetWhenEnabled", {}))
    return out


def render(changes, schema=SCHEMA):
    values, packages = split(changes, schema)
    lines = [f"  {key} = {_nix(value)};" for key, value in sorted(settings(values, schema).items())]
    if not packages:
        return "{\n" + "\n".join(lines) + "\n}\n"
    # Packages come from the host configuration's own pinned nixpkgs.
    lines.insert(0, "  environment.systemPackages = [ " + " ".join("pkgs." + p for p in packages) + " ];")
    return "{ pkgs, ... }:\n{\n" + "\n".join(lines) + "\n}\n"


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


def update_candidate_id(baseline, host_config, lock_hash):
    """An update's identity: the same host configuration, evaluated with this flake.lock."""
    require_baseline(baseline)
    require_host_config(host_config)
    if not isinstance(lock_hash, str) or re.fullmatch(r"[0-9a-f]{64}", lock_hash) is None:
        raise ValueError("An update must name its lock file's SHA-256")
    return _digest("nexus-guardian-update-v1\n" + baseline + "\n" + host_config + "\n" + lock_hash)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")
