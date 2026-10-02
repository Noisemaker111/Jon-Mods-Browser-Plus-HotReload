"""Build the complete, pinned MIT OpenPencil app with our local game bridge.

Upstream source/dependencies and output live only in checkout-home .scratch.
No game assets or third-party source are vendored into this repository.
"""
import argparse
import hashlib
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import server

COMMIT = "6cf1748e31a0e43d3794d43abbd003ce9caa3c6f"
SHA256 = "bd2dc53a8dee265b529c23b2c198814bc1795d7099df10e322d3a52bc9f542ce"


def build(install=True):
    reference = server.scratch() / "reference"
    reference.mkdir(parents=True, exist_ok=True)
    archive = reference / "open-pencil-v0.15.1.tar.gz"
    if not archive.exists():
        urllib.request.urlretrieve(f"https://codeload.github.com/open-pencil/open-pencil/tar.gz/{COMMIT}", archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
        raise RuntimeError("OpenPencil source checksum mismatch; refusing to build")
    source = reference / "open-pencil-v0.15.1"
    source.mkdir(exist_ok=True)
    # Restore pristine tracked sources on each build; retain the dependency cache.
    with tarfile.open(archive) as tar:
        for member in tar:
            parts = Path(member.name).parts[1:]
            if not parts:
                continue
            member.name = str(Path(*parts))
            tar.extract(member, source, filter="data")
    bun = shutil.which("bun")
    if not bun:
        raise RuntimeError("Install Bun to build OpenPencil (not needed to run an existing build)")
    if install:
        subprocess.run([bun, "install", "--frozen-lockfile"], cwd=source, check=True)
    shutil.copyfile(HERE / "bridge.js", source / "src/lab-bridge.js")
    shutil.copyfile(HERE / "adapter.js", source / "src/lab-adapter.js")
    main = source / "src/main.ts"
    text = main.read_text(encoding="utf-8")
    text = text[:text.index("if (!IS_TAURI)")]
    main.write_text(text + "\nimport './lab-bridge.js'\n", encoding="utf-8")
    router = source / "src/router.ts"
    router.write_text(router.read_text().replace("createWebHistory", "createWebHashHistory"), encoding="utf-8")
    brand = source / "src/components/brand/BrandMark.vue"
    brand.write_text(brand.read_text().replace("return `/brand/", "return `/pencil/brand/"), encoding="utf-8")
    fonts = source / "packages/core/src/text/fonts.ts"
    fonts.write_text(fonts.read_text().replace("': '/", "': '/pencil/"), encoding="utf-8")
    canvaskit = source / "packages/core/src/canvaskit.ts"
    canvaskit.write_text(canvaskit.read_text().replace(
        "'env' in import.meta ? import.meta.env.BASE_URL : '/'", "'/pencil/'"), encoding="utf-8")
    # Keep every upstream editor component, but no service worker, relay or remote
    # automation endpoint in this local embed. The game bridge is parent-only.
    shutil.copyfile(HERE / "vite.config.ts", source / "lab.vite.config.ts")
    home = server.workspace() / "pencil"
    home.mkdir(parents=True, exist_ok=True)
    output = home / ("build-" + uuid.uuid4().hex)
    subprocess.run([bun, "run", "--bun", "vite", "build", "--config", "lab.vite.config.ts",
                    "--outDir", str(output)], cwd=source, check=True)
    shutil.copyfile(source / "LICENSE", output / "LICENSE.txt")
    (output / "build.json").write_text(
        '{"name":"OpenPencil","version":"0.15.1","commit":"' + COMMIT + '","license":"MIT"}', encoding="utf-8")
    active = home / "app"
    if active.exists():
        active.rename(home / ("previous-" + uuid.uuid4().hex))
    output.rename(active)
    print(f"Full OpenPencil editor built: {active}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-install", action="store_true")
    build(not parser.parse_args().no_install)
