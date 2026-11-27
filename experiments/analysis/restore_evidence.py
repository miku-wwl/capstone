"""Reconstruct a study dataset from the published archive inventories."""

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path, PurePosixPath
from tempfile import TemporaryDirectory
from zipfile import ZipFile

from pack_evidence import digest, relative_file

CHECKPOINT = "study-checkpoint.json"
RAW_MEMBER = re.compile(r"block-[0-9]{2}/attempt-[0-9]{2}/[A-Za-z0-9._-]+\.json(?:\.gz)?")


def restore(destination, inventories, archive_dir):
    if destination.exists():
        raise FileExistsError(f"Use a new dataset directory: {destination}")
    packages = [json.loads(p.read_bytes()) for p in inventories]
    archives = [item for package in packages for item in package["archives"]]
    final_hash = packages[-1].get("checkpointSha256") or packages[-1]["pilotManifestSha256"]
    if not archives or len({x["file"] for x in archives}) != len(archives):
        raise ValueError("Archive inventories must have unique files")

    destination.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix=".restore-", dir=destination.parent) as temporary:
        root = Path(temporary).resolve()
        stage = root / "dataset"
        stage.mkdir()
        expected = None
        members = set()
        for item in archives:
            if PurePosixPath(item["file"]).name != item["file"] or "\\" in item["file"]:
                raise ValueError("Unexpected archive name")
            path = relative_file(archive_dir, item["file"])
            if path.stat().st_size != item["bytes"] or digest(path) != item["archiveSha256"]:
                raise ValueError(f"Archive bytes differ: {path.name}")
            with ZipFile(path) as archive:
                names = archive.namelist()
                if len(names) != item["members"] or len(names) != len(set(names)) or CHECKPOINT not in names:
                    raise ValueError(f"Archive member list differs: {path.name}")
                data = archive.read(CHECKPOINT)
                manifest_hash = hashlib.sha256(data).hexdigest()
                if manifest_hash != item["manifestSha256"]:
                    raise ValueError(f"Archive checkpoint differs: {path.name}")
                if manifest_hash == final_hash and expected is None:
                    final = json.loads(data)
                    if not final["complete"]:
                        raise ValueError("The final checkpoint must describe a complete dataset")
                    records = [x for attempt in final["attempts"]
                               for x in attempt["runs"] + attempt["failureEvidence"]]
                    expected = {x["file"]: x["sha256"] for x in records}
                    if len(expected) != len(records):
                        raise ValueError("The final checkpoint contains duplicate paths")
                    (stage / CHECKPOINT).write_bytes(data)
                for name in names:
                    if name == CHECKPOINT:
                        continue
                    if name in members or not RAW_MEMBER.fullmatch(name):
                        raise ValueError(f"Unexpected or duplicated evidence member: {name}")
                    target = relative_file(stage, name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(name) as source, target.open("xb") as output:
                        shutil.copyfileobj(source, output)
                    members.add(name)
        if expected is None or members != expected.keys():
            raise ValueError("Restored evidence paths differ from the final checkpoint")
        for name, sha256 in expected.items():
            if digest(stage / name) != sha256:
                raise ValueError(f"Restored evidence hash differs: {name}")
        if destination.exists():
            raise FileExistsError(destination)
        stage.rename(destination)
    print(f"Restored final checkpoint and {len(members)} uniquely verified evidence members.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("inventories", nargs="+", type=Path, help="Use batch 1 before the final batch")
    parser.add_argument("--archives", type=Path, required=True, help="Directory holding downloaded ZIP files")
    args = parser.parse_args()
    restore(args.destination.resolve(), args.inventories, args.archives.resolve(strict=True))


if __name__ == "__main__":
    main()
