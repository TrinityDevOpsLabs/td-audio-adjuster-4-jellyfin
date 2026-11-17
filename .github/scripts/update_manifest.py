"""Add a published GitHub release ZIP to Jellyfin's plugin catalog."""

import argparse
import hashlib
import json
import re
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZipFile


def jellyfin_version(value):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", value or ""):
        raise ValueError(f"Expected a numeric release version, got {value!r}")
    parts = value.split(".")
    return ".".join([*parts, *(["0"] * (4 - len(parts)))])


def update(manifest, project, archive, repository, tag, release):
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Expected a stable release tag such as v0.1.0")
    if release["isDraft"] or release["isPrerelease"] or not release["publishedAt"]:
        raise ValueError("Publish a stable GitHub release before updating the catalog")

    project_xml = ElementTree.parse(project)
    target_abi = jellyfin_version(project_xml.findtext(".//JellyfinVersion"))
    if archive.name != f"audioadjuster4jellyfin_{tag[1:]}.zip":
        raise ValueError("Archive filename does not match the release tag")
    with ZipFile(archive) as package:
        if sorted(package.namelist()) != ["AudioAdjuster4Jellyfin.dll", "LICENSE", "NOTICE"] or package.testzip():
            raise ValueError("The plugin ZIP must contain the plugin DLL, LICENSE and NOTICE at its root")
    with archive.open("rb") as stream:
        checksum = hashlib.file_digest(stream, lambda: hashlib.md5(usedforsecurity=False)).hexdigest()

    entry = {
        "version": jellyfin_version(tag[1:]),
        "targetAbi": target_abi,
        "sourceUrl": f"https://github.com/{repository}/releases/download/{tag}/{archive.name}",
        "checksum": checksum,
        "timestamp": release["publishedAt"],
        "changelog": release["body"] or "",
    }

    catalog = json.loads(manifest.read_text(encoding="utf-8"))
    versions = catalog[0]["versions"]
    existing = next((item for item in versions if item["version"] == entry["version"]), None)
    if existing and any(existing[key] != entry[key] for key in ("checksum", "targetAbi", "sourceUrl")):
        raise ValueError("This version already points to a different package. Publish a new version.")
    if existing:
        versions.remove(existing)
    versions.append(entry)
    versions.sort(key=lambda item: tuple(map(int, item["version"].split("."))), reverse=True)
    manifest.write_text(json.dumps(catalog, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repository")
    parser.add_argument("tag")
    parser.add_argument("project", type=Path)
    parser.add_argument("archive", type=Path)
    parser.add_argument("release", type=Path)
    parser.add_argument("--manifest", type=Path, default=Path("manifest.json"))
    args = parser.parse_args()
    update(args.manifest, args.project, args.archive, args.repository, args.tag,
           json.loads(args.release.read_text(encoding="utf-8")))
