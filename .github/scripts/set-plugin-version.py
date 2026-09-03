#!/usr/bin/env python3
"""
Called by semantic-release prepareCmd, before `jprm plugin build`. Patches the compiled assembly's
own AssemblyVersion/FileVersion/Version to match the release version.

Without this, the .csproj's hardcoded "0.1.0.0" ships in every release's DLL. TransformationPatches
reads Plugin.Instance.Version to build the `?v=...` cache-busting query string on every injected
<script>/<link> tag, so a stale assembly version means the browser serves stale JS/CSS after an
update (this exact bug was confirmed live on the sibling JellyUX Homepage plugin).

Usage: python3 .github/scripts/set-plugin-version.py <semver>  (e.g. "1.2.3")
"""
import re
import sys

CSPROJ_PATH = "src/Jellyfin.Plugin.EasyNotif/Jellyfin.Plugin.EasyNotif.csproj"


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: set-plugin-version.py <semver>", file=sys.stderr)
        sys.exit(1)

    version_4parts = f"{sys.argv[1]}.0"  # e.g. "1.2.3.0"

    with open(CSPROJ_PATH, encoding="utf-8") as f:
        content = f.read()

    patched = content
    for tag in ("AssemblyVersion", "FileVersion", "Version"):
        patched, count = re.subn(
            rf"<{tag}>[^<]*</{tag}>",
            f"<{tag}>{version_4parts}</{tag}>",
            patched,
        )
        if count != 1:
            print(f"ERROR: expected exactly one <{tag}> element in {CSPROJ_PATH}, found {count}", file=sys.stderr)
            sys.exit(1)

    with open(CSPROJ_PATH, "w", encoding="utf-8") as f:
        f.write(patched)

    print(f"{CSPROJ_PATH} version fields set to {version_4parts}")


if __name__ == "__main__":
    main()
