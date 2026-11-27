"""Archive indexed, verified synthetic evidence without changing its raw bytes."""

import argparse
import gzip
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile

SENSITIVE_KEYS = {
    "password", "connectionstring", "username", "hostname", "authorization",
    "cookie", "accesstoken", "refreshtoken", "privatekey", "email", "clientip",
}
SENSITIVE_TEXT = re.compile(
    r"(?:BEGIN [A-Z ]*PRIVATE KEY|github_pat_[\w]{30,}|ghp_[\w]{30,}|"
    r"(?:user id|username|password|pwd)\s*=|\b[A-Za-z0-9_.+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b|"
    r"[A-Za-z]:[\\/]Users[\\/])",
    re.IGNORECASE,
)


def scan(value):
    if isinstance(value, dict):
        for key, item in value.items():
            if re.sub(r"[^A-Za-z]", "", key).lower() in SENSITIVE_KEYS:
                raise ValueError("Synthetic evidence contains a sensitive field")
            scan(item)
    elif isinstance(value, list):
        for item in value:
            scan(item)
    elif isinstance(value, str) and SENSITIVE_TEXT.search(value):
        raise ValueError("Synthetic evidence contains private configuration or identity")


def digest(path):
    with path.open("rb") as data:
        return hashlib.file_digest(data, "sha256").hexdigest()


def relative_file(root, name):
    raw = PurePosixPath(name)
    result = (root / name).resolve()
    if raw.is_absolute() or ".." in raw.parts or not result.is_relative_to(root):
        raise ValueError("Evidence path escaped its dataset")
    return result


def build_archive(root, output, first, last, legacy_hash=None, attempt_number=None):
    root = root.resolve()
    manifest_name = "manifest.json" if legacy_hash else "study-checkpoint.json"
    manifest_path = root / manifest_name
    manifest = json.loads(manifest_path.read_bytes())
    scan(manifest)
    if legacy_hash:
        if digest(manifest_path) != legacy_hash or manifest["plan"]["phase"] != "engineering":
            raise ValueError("Historical manifest does not match its published identity")
        validated = manifest["runs"]
        if len(validated) != 16:
            raise ValueError("Historical dataset must contain sixteen indexed runs")
    else:
        validated = [r for attempt in manifest["attempts"] if first <= attempt["block"] <= last
                     and (attempt_number is None or attempt["attempt"] == attempt_number)
                     for r in attempt["runs"] + attempt["failureEvidence"]]
        if not validated:
            raise ValueError("Requested range contains no evidence")

    selected = {manifest_name: (manifest_path, digest(manifest_path))}
    for item in validated:
        name = item["file"]
        path = relative_file(root, name)
        if name in selected or digest(path) != item["sha256"]:
            raise ValueError(f"Duplicate or mismatched evidence {name}")
        if name.endswith(".json.gz"):
            scan(json.loads(gzip.decompress(path.read_bytes())))
        elif name.endswith(".json"):
            scan(json.loads(path.read_bytes()))
        else:
            raise ValueError(f"Review unparseable interrupted artifact at {name} before publishing")
        selected[name] = path, item["sha256"]
    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, mode="x", compression=ZIP_DEFLATED, compresslevel=6) as archive:
        for name, (path, _) in selected.items():
            archive.write(path, name, compress_type=ZIP_STORED if name.endswith(".gz") else ZIP_DEFLATED)
    with ZipFile(output) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != selected.keys():
            raise ValueError("Evidence archive member or CRC check failed")
        for name, (_, expected) in selected.items():
            with archive.open(name) as member:
                if hashlib.file_digest(member, "sha256").hexdigest() != expected:
                    raise ValueError(f"Archived bytes differ for {name}")
    return {"members": len(selected), "bytes": output.stat().st_size,
            "archiveSha256": digest(output), "manifestSha256": selected[manifest_name][1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--first-block", type=int, default=1)
    parser.add_argument("--last-block", type=int, default=14)
    parser.add_argument("--legacy-manifest-sha256")
    parser.add_argument("--attempt", type=int)
    args = parser.parse_args()
    print(json.dumps(build_archive(args.dataset, args.archive, args.first_block, args.last_block,
                                   args.legacy_manifest_sha256, args.attempt), indent=2))


if __name__ == "__main__":
    main()
