"""Check the frozen desktop pet against a real CLI and an empty, private Codex home."""

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time


def executables(npm_root, dist):
    machine = platform.machine().lower()
    arch = {"amd64": "x86_64", "x86_64": "x86_64", "arm64": "aarch64", "aarch64": "aarch64"}[machine]
    os_triple = {"darwin": "apple-darwin", "win32": "pc-windows-msvc", "linux": "unknown-linux-musl"}[sys.platform]
    triple = f"{arch}-{os_triple}"
    suffix = ".exe" if os.name == "nt" else ""
    candidates = [p for p in (npm_root / "node_modules").rglob("codex" + suffix)
                  if p.is_file() and "vendor" in p.parts and triple in p.parts]
    if len(candidates) != 1:
        raise RuntimeError(f"Expected one native Codex binary for {triple}, found {candidates}")
    name = "AI-Repair-Shop"
    pet = (dist / (name + ".app") / "Contents" / "MacOS" / name if sys.platform == "darwin"
           else dist / name / (name + suffix))
    if not pet.is_file():
        raise FileNotFoundError(pet)
    return candidates[0], pet


def wait_for_status(path, matches, timeout):
    deadline = time.monotonic() + timeout
    latest = None
    while time.monotonic() < deadline:
        try:
            latest = json.loads(path.read_bytes())
        except (FileNotFoundError, PermissionError):
            pass
        if latest is not None and matches(latest):
            return latest
        time.sleep(0.25)
    raise RuntimeError(f"Timed out waiting for {path.name}; last state was {latest!r}")


def check(npm_root, dist):
    cli, pet = executables(npm_root.resolve(), dist.resolve())
    version = subprocess.check_output([str(cli), "--version"], encoding="utf-8", timeout=20).strip()
    with tempfile.TemporaryDirectory(prefix="native-codex-check-") as temporary:
        root = Path(temporary)
        home = root / "codex-home"
        home.mkdir()
        state = root / "pet"
        assets = state / "assets"
        assets.mkdir(parents=True)
        # The build already downloaded and verified the public atlas. Keep this bridge
        # test offline and give the resident no earlier feedback state or chat history.
        atlas = dist.resolve().parent / "qa-state" / "assets" / "codex-spritesheet-v4.webp"
        shutil.copyfile(atlas, assets / atlas.name)
        env = {k: v for k, v in os.environ.items()
               if not k.startswith("CODEX_") and k not in ("OPENAI_API_KEY", "CHATGPT_API_KEY")}
        env.update(CODEX_HOME=str(home), CODEX_APP_RUNTIME_CLI_PATH=str(cli),
                   AI_REPAIR_SHOP_NATIVE_HOME=str(state))
        status = state / "status.json"

        def command(flag):
            subprocess.run([str(pet), flag], env=env, cwd=root, check=True, timeout=30)

        command("--summon")
        try:
            live = wait_for_status(status, lambda s: s.get("running") is True and
                                   s.get("status") == "尚未找到可确认的聊天", 60)
            if type(live.get("pid")) is not int or live["pid"] <= 0:
                raise RuntimeError("Resident did not record its process ID")
            cycle = json.loads((state / "feedback" / "feedback-cycle.json").read_bytes())
            if cycle.get("consumed") is not False:
                raise RuntimeError("Expected an untouched feedback cycle")
        finally:
            command("--stop")
            wait_for_status(status, lambda s: s.get("running") is False, 20)
            # QLockFile is released after the final status file is written.
            deadline = time.monotonic() + 10
            while (state / "feedback.lock").exists():
                if time.monotonic() >= deadline:
                    raise RuntimeError("Native resident failed to release its process lock")
                time.sleep(0.1)
        print(f"Verified {version}: frozen {platform.system()} pet opened an empty chat list and exited.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("npm_root", type=Path)
    parser.add_argument("dist", type=Path)
    args = parser.parse_args()
    check(args.npm_root, args.dist)
