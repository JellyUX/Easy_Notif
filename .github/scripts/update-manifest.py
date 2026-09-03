#!/usr/bin/env python3
"""
Called by semantic-release prepareCmd after @semantic-release/changelog updates CHANGELOG.md.
Runs `jprm repo add`, then injects the release notes into manifest.json.
Usage: python3 .github/scripts/update-manifest.py <semver>  (e.g. "1.2.0")
"""
import json
import os
import re
import subprocess
import sys


def get_repo_https_url() -> str:
    result = subprocess.run(
        ["git", "remote", "get-url", "origin"],
        capture_output=True, text=True, check=True,
    )
    url = result.stdout.strip()
    if url.endswith(".git"):
        url = url[:-4]
    if url.startswith("git@github.com:"):
        url = "https://github.com/" + url[len("git@github.com:"):]
    return url


def find_zip(artifacts_dir: str) -> str:
    zips = sorted(f for f in os.listdir(artifacts_dir) if f.endswith(".zip"))
    if not zips:
        print(f"ERROR: no zip found in {artifacts_dir}/", file=sys.stderr)
        sys.exit(1)
    return zips[-1]


def extract_changelog_section(version: str) -> str:
    try:
        with open("CHANGELOG.md", encoding="utf-8") as f:
            content = f.read()
    except FileNotFoundError:
        return ""

    pattern = (
        r"##\s+\[?" + re.escape(version) + r"\]?[^\n]*\n"
        r"(.*?)(?=\n##\s+|\Z)"
    )
    m = re.search(pattern, content, re.DOTALL)
    return m.group(1).strip() if m else ""


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: update-manifest.py <semver>", file=sys.stderr)
        sys.exit(1)

    version = sys.argv[1]            # e.g. "1.2.0"
    tag = f"v{version}"
    version_4parts = f"{version}.0"
    artifacts_dir = "./artifacts"

    filename = find_zip(artifacts_dir)
    repo_url = get_repo_https_url()
    plugin_url = f"{repo_url}/releases/download/{tag}/{filename}"

    subprocess.run(
        ["jprm", "repo", "add",
         f"--plugin-url={plugin_url}",
         "./", f"{artifacts_dir}/{filename}"],
        check=True,
    )

    release_notes = extract_changelog_section(version)
    if not release_notes:
        print(f"WARNING: no changelog section found for {version}", file=sys.stderr)

    with open("manifest.json", encoding="utf-8") as f:
        manifest = json.load(f)

    patched = False
    for plugin in manifest:
        for v in plugin.get("versions", []):
            if v["version"] == version_4parts:
                v["changelog"] = release_notes
                patched = True
                break

    if not patched:
        print(f"WARNING: {version_4parts} not found in manifest.json", file=sys.stderr)

    with open("manifest.json", "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=4, ensure_ascii=False)
        f.write("\n")

    print(f"manifest.json updated: {version_4parts}, changelog {len(release_notes)} chars")


if __name__ == "__main__":
    main()
