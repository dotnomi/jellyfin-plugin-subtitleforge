#!/usr/bin/env python3
"""Jellyfin plugin repository manifest.

  update-manifest.py changelog                        print the changelog from build.yaml
  update-manifest.py add manifest.json VER URL MD5    add/replace version VER in manifest.json

Metadata (guid, name, overview, description, category, owner, targetAbi, changelog) comes from build.yaml.
"""
import datetime
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")


def build_yaml():
    """Tiny parser for the flat build.yaml (scalars, folded '>' blocks); no PyYAML needed."""
    data, key, block = {}, None, []
    with open(os.path.join(ROOT, "build.yaml"), encoding="utf-8") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if key and (line.startswith("  ") or line == ""):
                block.append(line.strip())
                continue
            if key:
                data[key] = " ".join(b for b in block if b).strip()
                key, block = None, []
            m = re.match(r'^([A-Za-z]+):\s*(.*)$', line)
            if not m:
                continue
            k, v = m.group(1), m.group(2)
            if v in (">", "|"):
                key = k
            elif v:
                data[k] = v.strip().strip('"')
    if key:
        data[key] = " ".join(b for b in block if b).strip()
    return data


def main():
    meta = build_yaml()
    if len(sys.argv) >= 2 and sys.argv[1] == "changelog":
        print(meta.get("changelog", ""))
        return
    if len(sys.argv) != 6 or sys.argv[1] != "add":
        sys.exit(__doc__)
    path, version, url, md5 = sys.argv[2:6]
    manifest = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as fh:
            manifest = json.load(fh)
    plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        manifest.append(plugin)
    plugin.update({
        "name": meta["name"],
        "overview": meta.get("overview", ""),
        "description": meta.get("description", ""),
        "owner": meta.get("owner", ""),
        "category": meta.get("category", "General"),
    })
    entry = {
        "version": version,
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": url,
        "checksum": md5,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin["versions"] if v.get("version") != version]
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"{path}: {meta['name']} {version}")


if __name__ == "__main__":
    main()
