"""Native Qt application and on-demand Codex Skill entry points."""
import argparse
import hashlib
import json
import os
import shlex
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path
from urllib.request import Request, urlopen

VERSION = "0.8.0"
SOURCE = "https://persistent.oaistatic.com/codex/pets/v1/codex-spritesheet-v4.webp"
DIGEST = "5cd7af9cfeae88fe9c6e47a72460e0dc4c6fb04baf7fdc6cca14a212f0bf44d1"
SIZE = 887954


def data_dir():
    custom = os.environ.get("AI_REPAIR_SHOP_NATIVE_HOME")
    if custom:
        return Path(custom).resolve()
    if sys.platform == "darwin":
        parent = Path.home() / "Library/Application Support"
    elif os.name == "nt":
        parent = Path(os.environ["LOCALAPPDATA"])
    else:
        parent = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    return parent / "AIRepairShop"


def atomic_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as file:
        temporary = Path(file.name)
        try:
            file.write(data)
            file.flush()
            os.fsync(file.fileno())
        except BaseException:
            file.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        for attempt in range(4):
            try:
                os.replace(temporary, path)
                break
            except PermissionError:
                if os.name != "nt" or attempt == 3:
                    raise
                time.sleep(.025)
    finally:
        temporary.unlink(missing_ok=True)


def atlas(root):
    name = SOURCE.rsplit("/", 1)[1]
    target = root / "assets" / name
    codex_home = Path(os.environ.get("CODEX_HOME", Path.home() / ".codex"))
    for path in (target, codex_home / "cache/tui-pets/v1/assets" / name):
        try:
            if path.stat().st_size != SIZE:
                continue
            data = path.read_bytes()
        except OSError:
            continue
        if hashlib.sha256(data).hexdigest() == DIGEST:
            if path != target:
                atomic_write(target, data)
            return target
    import certifi
    context = ssl.create_default_context()
    context.load_verify_locations(certifi.where())
    request = Request(SOURCE, headers={"User-Agent": "AI-Repair-Shop/" + VERSION, "Accept": "image/webp"})
    with urlopen(request, timeout=25, context=context) as response:
        data = response.read(SIZE + 1)
    if len(data) != SIZE or hashlib.sha256(data).hexdigest() != DIGEST:
        raise ValueError("官方图集校验失败，请检查发行版更新。")
    atomic_write(target, data)
    return target


def find_codex():
    candidates = [os.environ.get("CODEX_APP_RUNTIME_CLI_PATH"), shutil.which("codex"),
                  "/Applications/Codex.app/Contents/Resources/codex",
                  str(Path.home() / "Applications/Codex.app/Contents/Resources/codex"),
                  "/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                  str(Path.home() / ".local/bin/codex")]
    if os.name == "nt":
        base = Path(os.environ["LOCALAPPDATA"]) / "OpenAI/Codex/bin"
        candidates.extend(sorted(base.glob("*/codex.exe"), key=lambda p: p.stat().st_mtime, reverse=True))
    for item in candidates:
        if item and Path(item).is_file() and os.access(item, os.X_OK):
            return str(item)
    raise RuntimeError("请先安装并登录 Codex，然后在能运行 codex 的终端中重新唤出桌宠。")


def frozen_executable():
    if not getattr(sys, "frozen", False):
        raise RuntimeError("此命令需要使用 GitHub Release 中的桌宠程序。")
    return Path(sys.executable).resolve()


def install_skill(root):
    executable = frozen_executable()
    # Keep the complete signed .app / onedir runtime together, including Qt plugins.
    source = executable.parents[2] if sys.platform == "darwin" else executable.parent
    if sys.platform == "darwin" and source.suffix != ".app":
        raise ValueError("请从解压后的 .app 安装。")
    skill_parent = Path(os.environ.get("CODEX_HOME", Path.home() / ".codex")) / "skills"
    skill = skill_parent / "ai-repair-shop-native"
    marker = ".ai-repair-shop-native"
    if skill.is_symlink() or (skill.exists() and not (skill / marker).is_file()):
        raise FileExistsError(f"同名技能已存在，请检查 {skill}")
    apps = root / "apps"
    apps.mkdir(parents=True, exist_ok=True)
    destination = apps / VERSION
    runtime_folder = "AI-Repair-Shop.app" if sys.platform == "darwin" else "runtime"
    if not destination.exists():
        with tempfile.TemporaryDirectory(dir=apps) as tmp:
            stage = Path(tmp) / "payload"
            if stage.resolve().is_relative_to(source):
                raise ValueError("请先将下载包解压到独立的目录。")
            shutil.copytree(source, stage / runtime_folder, symlinks=True)
            (stage / marker).write_text(VERSION, encoding="utf-8")
            try:
                stage.rename(destination)
            except OSError:
                if not (destination / marker).is_file():
                    raise
    installed = destination / runtime_folder / executable.relative_to(source)
    if not (destination / marker).is_file() or not installed.is_file():
        raise RuntimeError(f"无法验证安装目录 {destination}")
    command = "& '" + str(installed).replace("'", "''") + "'" if os.name == "nt" else shlex.quote(str(installed))
    instructions = f"""---
name: ai-repair-shop-native
description: 按需启动、关闭人工智障修理厂的原生桌宠，并给最近发过真人消息的本地 Codex 聊天反馈。
---

用户要求启动时执行 `{command} --summon`。用户要求关闭时执行 `{command} --stop`。
用 `{command} --status` 检查连接及进程状态。每次重置间最多发送一条反馈。
收到桌宠的纠错反馈时结合当前任务纠正，无需再启动桌宠或重复发送消息。
"""
    skill.mkdir(parents=True, exist_ok=True)
    atomic_write(skill / marker, VERSION.encode())
    atomic_write(skill / "SKILL.md", instructions.encode("utf-8"))
    return installed


def summon(executable):
    options = {"start_new_session": True} if os.name != "nt" else {"creationflags": 0x00000008 | 0x00000200}
    subprocess.Popen([str(executable), "--feedback"], stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **options)


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group()
    for flag in ("feedback", "summon", "stop", "status", "install-skill"):
        group.add_argument("--" + flag, action="store_true")
    group.add_argument("--smoke", metavar="PNG")
    parser.add_argument("--start", action="store_true")
    opts = parser.parse_args()
    root = data_dir()
    root.mkdir(parents=True, exist_ok=True)
    stop = root / "feedback-stop"
    status = root / "status.json"
    if opts.start and not opts.install_skill:
        parser.error("--start requires --install-skill")
    if opts.install_skill:
        installed = install_skill(root)
        if opts.start:
            summon(installed)
        return 0
    if opts.summon:
        summon(frozen_executable())
        return 0
    if opts.stop:
        atomic_write(stop, uuid.uuid4().hex.encode())
        return 0
    if opts.status:
        from PySide6.QtCore import QLockFile
        probe = QLockFile(str(root / "feedback.lock"))
        probe.setStaleLockTime(0)
        if probe.tryLock(0):
            probe.unlock()
            state = '{"running":false}'
        else:
            state = status.read_text(encoding="utf-8") if status.exists() else '{"running":true}'
        if sys.stdout:
            print(state)
        return 0
    from PySide6.QtCore import QLockFile, QTimer
    from PySide6.QtWidgets import QApplication, QMessageBox
    from feedback import Feedback
    from window import PetWindow, configure_font
    app = QApplication(sys.argv[:1])
    configure_font(app)
    lock = QLockFile(str(root / ("feedback.lock" if opts.feedback else "toy.lock")))
    lock.setStaleLockTime(0)
    if not lock.tryLock(0):
        return 0
    feedback = None
    initial_stop = stop.read_bytes() if stop.exists() else None
    try:
        if opts.feedback:
            atomic_write(status, json.dumps({"running": True, "pid": os.getpid(), "status": "正在启动"}).encode())
        path = atlas(root)
        if opts.feedback:
            feedback = Feedback(root / "feedback", find_codex())
        pet = PetWindow(str(path), feedback)
        pet.show()
        if opts.smoke:
            app.processEvents()
            pet.select_tool("hammer")
            good = pet.grab().save(opts.smoke)
            pet.close()
            return 0 if good else 1
        if feedback:
            previous = None
            def poll():
                nonlocal previous
                if stop.exists() and stop.read_bytes() != initial_stop:
                    pet.close()
                    return
                state = str(feedback.status)
                if state != previous:
                    atomic_write(status, json.dumps({"running": True, "pid": os.getpid(), "status": state},
                                                    ensure_ascii=False).encode("utf-8"))
                    previous = state
            timer = QTimer(pet)
            timer.timeout.connect(poll)
            timer.start(350)
            poll()
        return app.exec()
    except Exception as error:
        if opts.smoke:
            raise
        QMessageBox.critical(None, "人工智障修理厂", str(error))
        return 1
    finally:
        if feedback:
            feedback.close()
        if opts.feedback:
            atomic_write(status, b'{"running":false}')
        lock.unlock()


if __name__ == "__main__":
    sys.exit(main())
