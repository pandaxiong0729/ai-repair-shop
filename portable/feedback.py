"""Local Codex feedback for the desktop pet. Only the worker thread touches disk or RPC."""

import json
import os
import queue
import signal
import subprocess
import tempfile
import threading
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


PROMPT_PREFIX = "[人工智障修理厂反馈:"
OWN_PREFIXES = (PROMPT_PREFIX, "【人工智障修理厂连接测试", "【人工智障修理厂自动通道自测")
QUIET_SECONDS = 2.0
REFRESH_SECONDS = 2.0


class Uncertain(ValueError):
    """An incomplete snapshot cannot identify a destination."""


class RpcRejected(RuntimeError):
    pass


@dataclass(frozen=True)
class Target:
    thread: str
    title: str
    timestamp: int


def _id(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 256:
        raise Uncertain("Missing identifier")
    return value


def _text(item):
    return "\n".join(part["text"] for part in item["content"] if "text" in part)


def _latest_entry(entries, cutoff):
    latest = None
    for entry in entries:
        item = entry["item"]
        if item["type"] != "userMessage" or _text(item).startswith(OWN_PREFIXES):
            continue
        stamp = entry["startedAtMs"]
        if type(stamp) is not int or not 0 <= stamp <= 253402300799999:
            raise Uncertain("Invalid human message time")
        if stamp <= cutoff and (latest is None or stamp > latest):
            latest = stamp
    return latest


def _pages(call, method, params, limit, cursor=None):
    for _ in range(limit):
        page = call(method, dict(params, cursor=cursor))
        yield page["data"]
        cursor = page["nextCursor"]
        if cursor is None:
            return
        if not isinstance(cursor, str) or not cursor:
            raise Uncertain("Invalid history cursor")
    raise Uncertain("Cannot verify the whole relevant time window")


def _latest_human(call, thread, cutoff):
    turns = _pages(call, "thread/turns/list", {
        "threadId": thread, "limit": 10, "itemsView": "summary", "sortDirection": "desc",
    }, 20)
    for page in turns:
        for turn in page:
            ids = [_id(i["id"]) for i in turn["items"] if i["type"] == "userMessage"]
            if not ids:
                continue
            if len(ids) > 100:
                raise Uncertain("Too many summary items")
            scope = {"threadId": thread, "turnId": _id(turn["id"])}
            entries = call("thread/items/read", dict(scope, itemIds=ids))["data"]
            actual = [e["item"]["id"] for e in entries if e["item"]["type"] == "userMessage"]
            if sorted(actual) != sorted(ids) or len(actual) != len(entries):
                raise Uncertain("Summary changed while resolving the target")
            latest = _latest_entry(entries, cutoff)
            if latest is not None:
                return latest
            # Steering or our own submission can hide a human message in the same turn.
            older = _pages(call, "thread/items/list", dict(scope, limit=100, sortDirection="desc"),
                           100, {"type": "item", "itemId": ids[0]})
            for earlier in older:
                latest = _latest_entry(earlier, cutoff)
                if latest is not None:
                    return latest
    return None


def _target_at(call, cutoff):
    newest = None
    tied = False
    pages = _pages(call, "thread/list", {
        "limit": 20, "sortKey": "updated_at", "useStateDbOnly": True,
        "sourceKinds": ["vscode", "appServer", "cli"],
    }, 10)
    for page in pages:
        for chat in page:
            updated = chat["updatedAt"]
            if type(updated) is not int:
                raise Uncertain("Invalid updatedAt")
            if newest and (updated + 1) * 1000 < newest.timestamp:
                return None if tied else newest
            thread = _id(chat["id"])
            stamp = _latest_human(call, thread, cutoff)
            if stamp is None:
                continue
            if newest is None or stamp > newest.timestamp:
                title = " ".join((chat.get("name") or "").split()) or "Codex 聊天 " + thread[:8]
                newest, tied = Target(thread, title[:120], stamp), False
            elif stamp == newest.timestamp and thread != newest.thread:
                tied = True
    return None if tied else newest


class _Rpc:
    """One private stdio app-server. Every request is serialized on the worker."""

    def __init__(self, executable, stopped):
        if not executable:
            raise FileNotFoundError("Codex CLI")
        env = os.environ.copy()
        for key in ("CODEX_THREAD_ID", "CODEX_SESSION_ID", "CODEX_TOOL_CALL_ID", "CODEX_APP_TOOLS_PIPE_PATH"):
            env.pop(key, None)
        self.proc = subprocess.Popen(
            [str(executable), "app-server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, env=env, start_new_session=os.name == "posix",
            # Start suspended on Windows so no child can escape before job assignment.
            creationflags=(subprocess.CREATE_NO_WINDOW | 0x00000004) if os.name == "nt" else 0,
        )
        try:
            self._job = _windows_job(self.proc) if os.name == "nt" else None
        except BaseException:
            self.proc.kill()
            self.proc.wait()
            self.proc.stdin.close()
            self.proc.stdout.close()
            raise
        self.stopped = stopped
        self.responses = queue.Queue()
        self._stdout_eof = threading.Event()
        self.counter = 0
        self._kill_lock = threading.Lock()
        self._terminated = False
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        try:
            self.call("initialize", {
                "clientInfo": {"name": "ai_repair_shop", "version": "1.0"},
                "capabilities": {"experimentalApi": True},
            })
            self._write({"method": "initialized"})
        except Exception:
            self.close()
            raise

    def _read(self):
        try:
            for line in self.proc.stdout:
                response = json.loads(line)
                if "id" in response and "method" not in response:
                    self.responses.put(response)
            self._stdout_eof.set()
        except (ValueError, OSError):
            pass
        finally:
            self.responses.put(None)

    def _write(self, obj):
        self.proc.stdin.write(json.dumps(obj, separators=(",", ":")).encode("ascii") + b"\n")
        self.proc.stdin.flush()

    def call(self, method, params):
        if self.stopped.is_set():
            raise InterruptedError()
        self.counter += 1
        self._write({"id": self.counter, "method": method, "params": params})
        deadline = time.monotonic() + 20
        while not self.stopped.is_set():
            if time.monotonic() >= deadline:
                raise TimeoutError(method)
            try:
                message = self.responses.get(timeout=0.1)
            except queue.Empty:
                continue
            if message is None:
                raise EOFError(method)
            if message["id"] == self.counter:
                if "error" in message:
                    raise RpcRejected(method)
                return message["result"]
        raise InterruptedError()

    def interrupt(self):
        # The process group belongs only to the private client (including CLI launchers).
        with self._kill_lock:
            if self._terminated:
                return
            if os.name == "posix":
                # EOF means no descendant still owns the pipe. If the group leader
                # has also exited, reap it and never signal its old numeric ID.
                if self._stdout_eof.is_set():
                    try:
                        self.proc.wait(timeout=0.25)
                    except subprocess.TimeoutExpired:
                        pass
                    else:
                        self._terminated = True
                        return
                try:
                    os.killpg(self.proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            else:
                # Closing our non-inheritable job kills only assigned descendants.
                import _winapi
                _winapi.CloseHandle(self._job)
            self._terminated = True

    def close(self):
        try:
            # EOF is the stdio server's normal shutdown. Give it time to flush and
            # exit, then dispose of any remaining helpers in its private group/job.
            try:
                self.proc.stdin.close()
            except BrokenPipeError:
                pass
            self.reader.join(timeout=1)
        finally:
            self.interrupt()
            self.proc.wait()
            self.reader.join(timeout=1)
            self.proc.stdout.close()


def _windows_job(proc):
    """Assign our suspended Popen child to a kill-on-close job before its first instruction."""
    import ctypes as c
    from ctypes import wintypes as w
    import _winapi

    dll = c.WinDLL("kernel32", use_last_error=True)

    def api(name, result, *args):
        fn = getattr(dll, name)
        fn.argtypes, fn.restype = args, result
        return fn

    # JOBOBJECT_EXTENDED_LIMIT_INFORMATION, with native pointer-size alignment.
    class BasicLimits(c.Structure):
        _fields_ = [("process_time", c.c_int64), ("job_time", c.c_int64), ("flags", w.DWORD),
                    ("min_ws", c.c_size_t), ("max_ws", c.c_size_t), ("process_limit", w.DWORD),
                    ("affinity", c.c_size_t), ("priority", w.DWORD), ("scheduling", w.DWORD)]

    class ExtendedLimits(c.Structure):
        _fields_ = [("basic", BasicLimits), ("io_counters", c.c_uint64 * 6), ("memory", c.c_size_t * 4)]

    class ThreadEntry(c.Structure):
        _fields_ = [("size", w.DWORD), ("usage", w.DWORD), ("tid", w.DWORD), ("pid", w.DWORD),
                    ("base_priority", w.LONG), ("delta", w.LONG), ("flags", w.DWORD)]

    job = api("CreateJobObjectW", w.HANDLE, w.LPVOID, w.LPCWSTR)(None, None)
    if not job:
        raise c.WinError(c.get_last_error())
    try:
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        configure = api("SetInformationJobObject", w.BOOL, w.HANDLE, c.c_int, w.LPVOID, w.DWORD)
        assign = api("AssignProcessToJobObject", w.BOOL, w.HANDLE, w.HANDLE)
        if not configure(job, 9, c.byref(limits), c.sizeof(limits)) or not assign(job, int(proc._handle)):
            raise c.WinError(c.get_last_error())
        # Popen closes the initial thread handle. Reopen only a thread owned by
        # this still-suspended child, which cannot yet have created another one.
        snapshot = api("CreateToolhelp32Snapshot", w.HANDLE, w.DWORD, w.DWORD)(4, 0)
        if snapshot == w.HANDLE(-1).value:
            raise c.WinError(c.get_last_error())
        try:
            entry = ThreadEntry()
            entry.size = c.sizeof(entry)
            first = api("Thread32First", w.BOOL, w.HANDLE, c.POINTER(ThreadEntry))
            following = api("Thread32Next", w.BOOL, w.HANDLE, c.POINTER(ThreadEntry))
            present = first(snapshot, c.byref(entry))
            while present and entry.pid != proc.pid:
                present = following(snapshot, c.byref(entry))
            if not present:
                raise OSError("Cannot identify the private Codex client's initial thread")
            thread = api("OpenThread", w.HANDLE, w.DWORD, w.BOOL, w.DWORD)(2, False, entry.tid)
            if not thread:
                raise c.WinError(c.get_last_error())
            try:
                if api("ResumeThread", w.DWORD, w.HANDLE)(thread) == 0xffffffff:
                    raise c.WinError(c.get_last_error())
            finally:
                _winapi.CloseHandle(thread)
        finally:
            _winapi.CloseHandle(snapshot)
        return job
    except BaseException:
        _winapi.CloseHandle(job)
        raise


class _CycleFile:
    """Hold a writer lease and atomically persist the reset boundary before submission."""

    def __init__(self, root):
        root.mkdir(parents=True, exist_ok=True)
        self.path = root / "feedback-cycle.json"
        self.lease = (root / "feedback.lock").open("a+b")
        try:
            if os.name == "posix":
                import fcntl
                fcntl.flock(self.lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
            else:
                import msvcrt
                self.lease.seek(0)
                msvcrt.locking(self.lease.fileno(), msvcrt.LK_NBLCK, 1)
        except BaseException:
            self.lease.close()
            raise

    def load(self):
        try:
            with self.path.open("rb") as f:
                raw = f.read(4097)
            if len(raw) > 4096:
                raise ValueError()
            data = json.loads(raw)
            cycle = uuid.UUID(data["cycleId"])
            updated = datetime.fromisoformat(data["updatedUtc"].replace("Z", "+00:00"))
            if (not cycle.int or type(data["consumed"]) is not bool or
                    type(data["schema"]) is not int or data["schema"] != 1 or updated.tzinfo is None):
                raise ValueError()
            return str(cycle), data["consumed"]
        except FileNotFoundError:
            cycle = str(uuid.uuid4())
            self.save(cycle, False)
            return cycle, False
        except (KeyError, ValueError, TypeError, AttributeError, OSError):
            # Preserve the unreadable file. Only an explicit reset may replace it.
            return str(uuid.uuid4()), True

    def save(self, cycle, consumed):
        state = {"schema": 1, "cycleId": cycle, "consumed": consumed,
                 "updatedUtc": datetime.now(timezone.utc).isoformat()}
        fd, temporary = tempfile.mkstemp(dir=self.path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(state, f)
                f.flush()
                os.fsync(f.fileno())
            # Windows readers can briefly deny replacement while inspecting status.
            for attempt in range(4):
                try:
                    os.replace(temporary, self.path)
                    break
                except PermissionError:
                    if os.name != "nt" or attempt == 3:
                        raise
                    time.sleep(0.015)
            if os.name == "posix":
                directory_fd = os.open(self.path.parent, os.O_RDONLY)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
        finally:
            Path(temporary).unlink(missing_ok=True)

    def close(self):
        self.lease.close()


class Feedback:
    """Thread-safe desktop API. Hits use wall-clock milliseconds, pauses use monotonic time."""

    def __init__(self, data_root, codex_path):
        self._root = Path(data_root)
        self._path = codex_path
        self._stop = threading.Event()
        self._changed = threading.Condition()
        self._epoch = 0
        self._disabled = False
        self._consumed = False
        self._target = self._anchor = None
        self._first_ms = None
        self._count = 0
        self._held = False
        self._last_activity = 0.0
        self._connection = "正在连接 Codex…"
        self._delivery = ""
        self._rpc = None
        self._receipt = None
        self._worker = threading.Thread(target=self._run, name="codex-feedback", daemon=True)
        self._worker.start()

    @property
    def status(self):
        with self._changed:
            return self._delivery or self._connection

    def hit(self):
        with self._changed:
            if self._consumed or self._stop.is_set():
                return
            if self._first_ms is None:
                self._first_ms = time.time_ns() // 1_000_000
                self._anchor = self._target
            self._last_activity = time.monotonic()
            self._count += 1
            self._delivery = f"本轮 {self._count} 下 · 松手后自动反馈一次"
            self._changed.notify()

    def set_held(self, held):
        with self._changed:
            if self._held != held:
                self._held = held
                self._last_activity = time.monotonic()
                self._changed.notify()

    def reset(self):
        with self._changed:
            if self._disabled or self._stop.is_set():
                return
            self._epoch += 1
            self._consumed = self._held = False
            self._first_ms = self._anchor = None
            self._count = 0
            self._delivery = "已重置 · 下一轮敲打自动反馈一次"
            self._changed.notify()

    def close(self):
        self._stop.set()
        with self._changed:
            self._changed.notify()
        # A hung peer may have stopped draining stdin halfway through a long request.
        # Killing our private process also unblocks that write before joining the worker.
        self._worker.join(timeout=1.25)
        rpc = self._rpc
        if rpc is not None:
            rpc.interrupt()
        self._worker.join()

    def _current(self, epoch):
        return not self._stop.is_set() and self._epoch == epoch

    def _publish(self, epoch, message):
        with self._changed:
            if self._current(epoch):
                self._delivery = message

    def _disconnect(self):
        with self._changed:
            self._target = None
            self._connection = "Codex 暂未连接"
        if self._rpc is not None:
            self._rpc.close()
            self._rpc = None

    def _refresh(self):
        try:
            if self._rpc is None:
                self._rpc = _Rpc(self._path, self._stop)
            target = _target_at(self._rpc.call, time.time_ns() // 1_000_000)
            with self._changed:
                self._target = target
                self._connection = ("已连接 · " + target.title) if target else "尚未找到可确认的聊天"
            if self._receipt:
                epoch, cycle, target = self._receipt
                turns = self._rpc.call("thread/turns/list", {
                    "threadId": target.thread, "limit": 5, "itemsView": "summary", "sortDirection": "desc",
                })
                if any(i["type"] == "userMessage" and _text(i).startswith(f"{PROMPT_PREFIX}{cycle}]")
                       for turn in turns["data"] for i in turn["items"]):
                    self._publish(epoch, "聊天已收到反馈 → " + target.title)
                    self._receipt = None
        except Exception:
            self._disconnect()

    def _deliver(self, store, epoch, cycle, cutoff, anchor):
        attempted = False
        try:
            store.save(cycle, True)
            if anchor is None or self._rpc is None:
                self._publish(epoch, "本轮未连接聊天 · 重置后再试")
                return
            target = _target_at(self._rpc.call, cutoff)
            # A disappeared chat must not make an older or tied chat take over this round.
            if (target is None or target.timestamp < anchor.timestamp or
                    (target.timestamp == anchor.timestamp and target.thread != anchor.thread)):
                self._publish(epoch, "无法确认敲打时的聊天 · 重置后再试")
                return
            prompt = f"{PROMPT_PREFIX}{cycle}] 你这次做得不好，我很生气。请检查刚才的回答并纠正。"
            # Commit to exactly one request. A reset during the subsequent RPC starts a
            # new cycle and cannot retract this in-flight request or inherit its result.
            with self._changed:
                if not self._current(epoch):
                    return
                attempted = True
            result = self._rpc.call("thread/queue/add", {
                "threadId": target.thread, "clientUserMessageId": cycle,
                "input": [{"type": "text", "text": prompt, "text_elements": []}],
            })
            _id(result["queuedSubmission"]["id"])
            self._receipt = (epoch, cycle, target)
            self._publish(epoch, "反馈已入队 → " + target.title + " · 等待 Codex 处理")
        except Exception as error:
            if attempted and not isinstance(error, RpcRejected):
                self._publish(epoch, "发送结果不确定 · 不会重发，请重置")
            else:
                self._publish(epoch, "本轮未能发送 · 重置后再试")
            self._disconnect()

    def _run(self):
        store = None
        try:
            store = _CycleFile(self._root)
            cycle, consumed = store.load()
            saved_epoch = 0
            with self._changed:
                if self._epoch == 0 and consumed:
                    self._consumed = True
                    self._delivery = "本轮已结束 · 重置后可再次反馈"
            next_poll = 0
            while True:
                with self._changed:
                    epoch = self._epoch
                    now = time.monotonic()
                    active = not self._consumed and self._first_ms is not None and not self._held
                    due = self._last_activity + QUIET_SECONDS
                    if saved_epoch != epoch:
                        action = "reset"
                    elif self._stop.is_set():
                        return
                    elif active and now >= due:
                        action = "deliver"
                        self._consumed = True
                        cutoff, anchor = self._first_ms, self._anchor
                    elif now >= next_poll:
                        action = "poll"
                    else:
                        wake = min(next_poll, due) if active else next_poll
                        self._changed.wait(timeout=max(0, wake - now))
                        continue
                if action == "reset":
                    cycle = str(uuid.uuid4())
                    self._receipt = None
                    try:
                        store.save(cycle, False)
                    except OSError:
                        with self._changed:
                            if self._current(epoch):
                                self._consumed = True
                                self._delivery = "重置未能保存 · 请再次重置"
                    saved_epoch = epoch
                elif action == "deliver":
                    self._deliver(store, epoch, cycle, cutoff, anchor)
                else:
                    self._refresh()
                    next_poll = time.monotonic() + REFRESH_SECONDS
        except Exception:
            with self._changed:
                self._disabled = True
                self._consumed = True
                self._delivery = "反馈存储不可用 · 请关闭其他桌宠实例并重新打开"
        finally:
            self._disconnect()
            if store:
                store.close()
