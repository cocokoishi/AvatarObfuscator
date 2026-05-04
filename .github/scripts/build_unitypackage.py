#!/usr/bin/env python3
"""
Build a Unity .unitypackage from a directory tree, using only the .meta files
that Unity has already generated. No Unity license / install needed.

Format reference (community-documented):
  A .unitypackage is a gzipped tar archive whose top-level entries are folders
  named after each asset's GUID. Inside each GUID folder:
      asset         The file's binary content (omit for folders)
      asset.meta    The YAML .meta file as-is
      pathname      UTF-8, the destination path of the asset, e.g.
                    "Assets/dev.cocokoishi.avatar-obfuscator/Editor/Plugin.cs"
      preview.png   (optional thumbnail; we don't generate one)

Usage:
    python build_unitypackage.py \\
        --source staging/dev.cocokoishi.avatar-obfuscator \\
        --asset-root Assets/dev.cocokoishi.avatar-obfuscator \\
        --output dist/dev.cocokoishi.avatar-obfuscator-0.1.0.unitypackage
"""

from __future__ import annotations

import argparse
import io
import os
import re
import sys
import tarfile
from pathlib import Path

# A .meta file starts with YAML like:
#     fileFormatVersion: 2
#     guid: 1a2b3c4d5e6f7890abcdef1234567890
GUID_RE = re.compile(rb"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)


def read_guid(meta_path: Path) -> str:
    """Return the GUID string from a Unity .meta file."""
    data = meta_path.read_bytes()
    m = GUID_RE.search(data)
    if not m:
        raise ValueError(f"No 'guid:' line found in {meta_path}")
    return m.group(1).decode("ascii").lower()


def _add_text_member(
    tar: tarfile.TarFile, name: str, data: bytes, mtime: float
) -> None:
    info = tarfile.TarInfo(name=name)
    info.size = len(data)
    info.mtime = int(mtime)
    info.mode = 0o644
    tar.addfile(info, io.BytesIO(data))


def _add_file_member(
    tar: tarfile.TarFile, name: str, src: Path, mtime: float
) -> None:
    info = tarfile.TarInfo(name=name)
    info.size = src.stat().st_size
    info.mtime = int(mtime)
    info.mode = 0o644
    with src.open("rb") as fh:
        tar.addfile(info, fh)


def build(source: Path, asset_root: str, output: Path) -> None:
    """Walk *source* and emit *output* (.unitypackage)."""
    if not source.is_dir():
        sys.exit(f"--source must be a directory: {source}")

    asset_root = asset_root.strip("/").replace("\\", "/")

    # Collect all (relative_path, real_path, meta_path, is_dir) tuples.
    entries: list[tuple[str, Path, Path, bool]] = []

    for root, dirs, files in os.walk(source):
        # We deliberately walk in deterministic order so the output archive
        # is byte-stable across runs (handy for diffing & supply-chain audits).
        dirs.sort()
        files.sort()

        rel_root = Path(root).relative_to(source)

        # Folders themselves get an entry (no `asset`, only `asset.meta`).
        for d in dirs:
            real = Path(root) / d
            meta = real.with_suffix(real.suffix + ".meta") if real.suffix else Path(str(real) + ".meta")
            if not meta.exists():
                # Unity sometimes places the meta as <name>.meta even when the
                # folder has no extension. Try the canonical form again to be
                # explicit.
                meta = Path(str(real) + ".meta")
            if not meta.exists():
                print(f"::warning::Missing folder meta for {real}, skipping", file=sys.stderr)
                continue
            rel = (rel_root / d).as_posix()
            entries.append((rel, real, meta, True))

        # Files get `asset`, `asset.meta` and `pathname`.
        for f in files:
            if f.endswith(".meta"):
                continue  # processed alongside its asset
            real = Path(root) / f
            meta = Path(str(real) + ".meta")
            if not meta.exists():
                # No .meta, no GUID, no entry — Unity wouldn't import it anyway.
                print(f"::warning::Missing meta for {real}, skipping", file=sys.stderr)
                continue
            rel = (rel_root / f).as_posix()
            entries.append((rel, real, meta, False))

    if not entries:
        sys.exit(f"No assets with .meta files found under {source}")

    seen_guids: dict[str, str] = {}
    output.parent.mkdir(parents=True, exist_ok=True)

    print(f"Packing {len(entries)} entries into {output}")

    # Gzipped tar; mtime fixed so archives are reproducible-ish.
    fixed_mtime = 1_700_000_000  # 2023-11-14 — arbitrary stable epoch

    with tarfile.open(output, mode="w:gz", format=tarfile.GNU_FORMAT) as tar:
        for rel, real, meta, is_dir in entries:
            try:
                guid = read_guid(meta)
            except ValueError as e:
                print(f"::warning::{e}; skipping", file=sys.stderr)
                continue

            if guid in seen_guids:
                print(
                    f"::error::GUID collision: {guid} used by both "
                    f"{seen_guids[guid]} and {rel}",
                    file=sys.stderr,
                )
                sys.exit(1)
            seen_guids[guid] = rel

            asset_path = f"{asset_root}/{rel}"

            # asset.meta
            _add_text_member(
                tar, f"{guid}/asset.meta", meta.read_bytes(), fixed_mtime
            )
            # pathname
            _add_text_member(
                tar,
                f"{guid}/pathname",
                asset_path.encode("utf-8"),
                fixed_mtime,
            )
            # asset (skip for folders)
            if not is_dir:
                _add_file_member(tar, f"{guid}/asset", real, fixed_mtime)

    size_mb = output.stat().st_size / (1024 * 1024)
    print(
        f"Wrote {output} "
        f"({size_mb:.2f} MiB, {len(seen_guids)} unique assets)"
    )


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    ap.add_argument(
        "--source", required=True, type=Path,
        help="Path to the package directory whose contents will be packed.",
    )
    ap.add_argument(
        "--asset-root", required=True,
        help="Destination prefix inside the package, e.g. 'Assets/dev.cocokoishi.avatar-obfuscator'.",
    )
    ap.add_argument(
        "--output", required=True, type=Path,
        help="Output .unitypackage path.",
    )
    args = ap.parse_args()
    build(args.source, args.asset_root, args.output)


if __name__ == "__main__":
    main()
