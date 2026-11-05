"""Run on each target OS/architecture. Produces a relocatable native desktop app."""
import argparse
from importlib import metadata
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import zipfile

here = Path(__file__).resolve().parent
repo = here.parent


def run(*args, **kwargs):
    subprocess.run(list(map(str, args)), check=True, **kwargs)


def build(output):
    output.mkdir(parents=True)
    notices = output / "notices"
    shutil.copytree(here / "licenses", notices)
    for item in ("LICENSE", "NOTICE"):
        shutil.copyfile(repo / item, notices / item)
    for package in ["PySide6-Essentials", "shiboken6", "PyInstaller", "certifi"]:
        dist = metadata.distribution(package)
        for item in dist.files or []:
            if ".dist-info" in str(item) and any(s in str(item).lower() for s in ("license", "copying", "notice")):
                dest = notices / package / item.name
                dest.parent.mkdir(exist_ok=True)
                shutil.copyfile(dist.locate_file(item), dest)
    python_license = next((p for p in (Path(sys.base_prefix) / "LICENSE.txt",
                           Path(os.__file__).parent / "LICENSE.txt") if p.is_file()), None)
    if python_license:
        shutil.copyfile(python_license, notices / "Python-LICENSE.txt")
    name = "AI-Repair-Shop"
    distdir = output / "dist"
    run(sys.executable, "-m", "PyInstaller", "--noconfirm", "--windowed", "--onedir",
        "--name", name, "--distpath", distdir, "--workpath", output / "build",
        "--specpath", output, "--osx-bundle-identifier", "io.github.pandaxiong0729.airepairshop",
        "--add-data", str(notices) + os.pathsep + "licenses",
        "--add-data", str(repo / "runtime-assets/codex/quotes.json") + os.pathsep + "runtime-assets/codex",
        here / "app.py")
    arch = platform.machine().lower()
    if arch in ("amd64", "x86_64"):
        arch = "x64"
    if arch == "aarch64":
        arch = "arm64"
    system = {"Darwin": "macOS", "Linux": "Linux", "Windows": "Windows"}[platform.system()]
    label = f"{name}-{system}-{arch}"
    tree = distdir / (name + ".app" if sys.platform == "darwin" else name)
    exe = tree / ("Contents/MacOS/" + name if sys.platform == "darwin" else name + (".exe" if os.name == "nt" else ""))
    # Exercise the frozen executable, image decoder and actual window on the build host.
    test_env = dict(os.environ, AI_REPAIR_SHOP_NATIVE_HOME=str(output / "qa-state"))
    image = output / "window.png"
    run(exe, "--smoke", image, env=test_env, timeout=100)
    if image.stat().st_size < 3000:
        raise RuntimeError("Bundled pet did not render a usable frame")
    run(exe, "--install-skill", env={**test_env, "CODEX_HOME": str(output / "qa-skills")}, timeout=150)
    skill = output / "qa-skills/skills/ai-repair-shop-native/SKILL.md"
    if "--summon" not in skill.read_text(encoding="utf-8"):
        raise RuntimeError("Skill startup entry missing")
    publish = output / "publish"
    publish.mkdir()
    if sys.platform == "darwin":
        run("ditto", "-c", "-k", "--keepParent", tree, publish / (label + ".zip"))
    elif os.name == "nt":
        shutil.make_archive(str(publish / label), "zip", distdir, tree.name)
    else:
        with tarfile.open(publish / (label + ".tar.gz"), "w:gz") as archive:
            archive.add(tree, arcname=tree.name)
    print("Verified", system, arch, list(publish.iterdir())[0].name, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    build(parser.parse_args().output.resolve())
