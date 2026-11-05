"""Run from portable/ with python -m unittest test_feedback. All peers are local fakes."""

import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

import feedback as fb


def entry(item_id, text, stamp, kind="userMessage"):
    return {"item": {"type": kind, "id": item_id, "content": [{"type": "text", "text": text}]},
            "startedAtMs": stamp}


def page(data, cursor=None):
    return {"data": data, "nextCursor": cursor}


def turns(item):
    return page([{"id": "turn", "items": [item["item"]]}])


class History(unittest.TestCase):
    def scripted(self, steps):
        def call(method, params):
            expected, result, checks = steps.pop(0)
            self.assertEqual(expected, method)
            for key, value in checks.items():
                self.assertEqual(params[key], value)
            return result
        return call

    def test_mixed_history_paginates_back_to_first_hit(self):
        later = entry("steer", "new human", 4000)
        before = entry("opaque", "the human at first hit", 2000)
        rows = [entry(str(i), prefix + "feedback", None) for i, prefix in enumerate(fb.OWN_PREFIXES)]
        steps = [
            ("thread/turns/list", turns(later), {"itemsView": "summary", "cursor": None}),
            ("thread/items/read", page([later]), {"itemIds": ["steer"]}),
            ("thread/items/list", page(rows, "next"), {"cursor": {"type": "item", "itemId": "steer"}}),
            ("thread/items/list", page([before]), {"cursor": "next", "sortDirection": "desc"}),
        ]
        self.assertEqual(2000, fb._latest_human(self.scripted(steps), "thread", 2000))
        self.assertFalse(steps)

    def test_missing_time_or_message_never_falls_back(self):
        human = entry("person", "hi", None)
        for rows in ([human], [], [entry("other", "hi", 1)]):
            with self.subTest(rows=rows):
                steps = [("thread/turns/list", turns(human), {}),
                         ("thread/items/read", page(rows), {})]
                with self.assertRaises(fb.Uncertain):
                    fb._latest_human(self.scripted(steps), "thread", 2000)

    def test_pagination_limit_fails_closed(self):
        def call(method, params):
            return page([], "more")
        with self.assertRaises(fb.Uncertain):
            fb._target_at(call, 100)

    def test_different_chats_at_same_millisecond_are_ambiguous(self):
        peer = Peer()
        peer.add("other", "simultaneous", peer.chats["A"][0]["startedAtMs"])
        self.assertIsNone(fb._target_at(peer.call, time.time_ns() // 1_000_000))


def wait_until(check, timeout=3):
    deadline = time.monotonic() + timeout
    while not check():
        if time.monotonic() > deadline:
            raise AssertionError("Timed out waiting for test condition")
        time.sleep(0.005)


class Peer:
    def __init__(self):
        self.chats = {}
        self.submissions = []
        self.before_queue = lambda: None
        self.queue_reply = lambda: {"queuedSubmission": {"id": "accepted"}}
        self.add("A", "original human", time.time_ns() // 1_000_000 - 3000)
        self.closed = False

    def add(self, chat, text, stamp):
        messages = self.chats.setdefault(chat, [])
        messages.append(entry(str(len(messages)), text, stamp))

    def call(self, method, p):
        if method == "thread/list":
            threads = [{"id": chat, "name": chat, "updatedAt": max(e["startedAtMs"] for e in rows) // 1000}
                       for chat, rows in list(self.chats.items())]
            return page(sorted(threads, key=lambda t: t["updatedAt"], reverse=True))
        rows = self.chats[p["threadId"]]
        if method == "thread/turns/list":
            return turns(rows[-1])
        if method == "thread/items/read":
            return page([e for e in rows if e["item"]["id"] in p["itemIds"]])
        if method == "thread/items/list":
            anchor = next(i for i, e in enumerate(rows) if e["item"]["id"] == p["cursor"]["itemId"])
            return page(list(reversed(rows[:anchor])))
        if method == "thread/queue/add":
            self.before_queue()
            self.submissions.append(p)
            return self.queue_reply()
        raise AssertionError(method)

    def close(self):
        self.closed = True

    def interrupt(self):
        pass


class Controller(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.peer = Peer()
        for mock in (patch.object(fb, "_Rpc", return_value=self.peer),
                     patch.object(fb, "QUIET_SECONDS", 0.08), patch.object(fb, "REFRESH_SECONDS", 0.04)):
            mock.start()
            self.addCleanup(mock.stop)

    def start(self, bound=True):
        pet = fb.Feedback(self.root, "unused")
        self.addCleanup(pet.close)
        if bound:
            wait_until(lambda: pet._target is not None)
        return pet

    def read_cycle(self):
        return json.loads((self.root / "feedback-cycle.json").read_text())

    def test_hold_cutoff_ack_receipt_and_reset(self):
        pet = self.start()
        durable_states = []
        self.peer.before_queue = lambda: durable_states.append(self.read_cycle())
        pet.set_held(True)
        pet.hit()
        time.sleep(0.02)
        self.peer.add("B", "arrived during the gesture", time.time_ns() // 1_000_000)
        pet.hit()
        time.sleep(0.13)
        self.assertEqual([], self.peer.submissions)
        pet.set_held(False)
        time.sleep(0.02)
        self.assertEqual([], self.peer.submissions)
        wait_until(lambda: "已入队" in pet.status)
        request, = self.peer.submissions
        self.assertEqual({"threadId", "clientUserMessageId", "input"}, set(request))
        self.assertEqual("A", request["threadId"])
        self.assertTrue(durable_states[0]["consumed"])
        self.assertEqual(durable_states[0]["cycleId"], request["clientUserMessageId"])
        text = request["input"][0]["text"]
        self.assertEqual(f'{fb.PROMPT_PREFIX}{request["clientUserMessageId"]}] '
                         '你这次做得不好，我很生气。请检查刚才的回答并纠正。', text)
        self.peer.add("A", text, time.time_ns() // 1_000_000)
        wait_until(lambda: "聊天已收到" in pet.status)
        pet.hit()
        time.sleep(0.13)
        self.assertEqual(1, len(self.peer.submissions))
        pet.reset()
        pet.hit()
        wait_until(lambda: len(self.peer.submissions) == 2)
        self.assertEqual("B", self.peer.submissions[1]["threadId"])
        self.assertNotEqual(request["clientUserMessageId"], self.peer.submissions[1]["clientUserMessageId"])

    def test_disconnect_after_write_is_not_replayed_after_restart(self):
        def lost_reply():
            raise EOFError()
        self.peer.queue_reply = lost_reply
        pet = self.start()
        pet.hit()
        wait_until(lambda: "不确定" in pet.status)
        pet.hit()
        pet.close()
        reopened = self.start()
        reopened.hit()
        time.sleep(0.13)
        self.assertEqual(1, len(self.peer.submissions))
        reopened.reset()
        reopened.close()
        self.assertFalse(self.read_cycle()["consumed"])

    def test_reset_discards_draft_and_ignores_an_inflight_reply(self):
        pet = self.start()
        pet.hit()
        pet.reset()
        time.sleep(0.12)
        self.assertEqual([], self.peer.submissions)
        release = threading.Event()
        self.addCleanup(release.set)

        def held_reply():
            if not release.wait(2):
                raise TimeoutError()
            return {"queuedSubmission": {"id": "old ack"}}
        self.peer.queue_reply = held_reply
        pet.hit()
        wait_until(lambda: bool(self.peer.submissions))
        pet.reset()
        reply = self.peer.submissions[0]["input"][0]["text"]
        self.peer.add("A", reply, time.time_ns() // 1_000_000)
        release.set()
        wait_until(lambda: not self.read_cycle()["consumed"])
        time.sleep(0.08)
        self.assertIn("已重置", pet.status)

    def test_unbound_first_hit_cannot_attach_after_connect(self):
        self.peer.chats.clear()
        pet = self.start(bound=False)
        wait_until(lambda: "尚未找到" in pet.status)
        pet.hit()
        self.peer.add("later", "recovered chat", time.time_ns() // 1_000_000 - 500)
        wait_until(lambda: "本轮未连接" in pet.status)
        self.assertTrue(self.read_cycle()["consumed"])
        self.assertEqual([], self.peer.submissions)

    def test_disappeared_newest_chat_does_not_redirect_to_older_chat(self):
        self.peer.add("B", "newest at first hit", time.time_ns() // 1_000_000 - 100)
        pet = self.start()
        pet.hit()
        del self.peer.chats["B"]
        wait_until(lambda: "无法确认" in pet.status)
        self.assertEqual([], self.peer.submissions)

    def test_corruption_and_contention_fail_closed(self):
        (self.root / "feedback-cycle.json").write_text('{"consumed":false}')
        pet = self.start()
        pet.hit()
        self.assertIn("本轮已结束", pet.status)
        other = self.start(bound=False)
        wait_until(lambda: "存储不可用" in other.status)
        other.reset()
        other.hit()
        self.assertIn("存储不可用", other.status)
        pet.reset()
        pet.hit()
        wait_until(lambda: len(self.peer.submissions) == 1)


class Transport(unittest.TestCase):
    def test_private_stdio_handshake_error_and_cleanup(self):
        # Spawn a real local pipe peer, never a Codex binary. Verify context isolation too.
        peer = """
import json, os, sys
assert not any(k in os.environ for k in ('CODEX_THREAD_ID', 'CODEX_TOOL_CALL_ID',
                                          'CODEX_SESSION_ID', 'CODEX_APP_TOOLS_PIPE_PATH'))
read = lambda: json.loads(input())
def reply(obj): print(json.dumps(obj), flush=True)
init = read()
assert init['method'] == 'initialize' and init['params']['capabilities']['experimentalApi']
reply({'method': 'backgroundNotification'})
reply({'id': init['id'], 'result': {}})
assert read() == {'method': 'initialized'}
request = read()
assert request['method'] == 'thread/list'
reply({'id': request['id'], 'error': {'message': 'denied'}})
input()
"""
        real_popen = subprocess.Popen
        def spawn(args, **kwargs):
            self.assertEqual(["mock-codex", "app-server"], args)
            return real_popen([sys.executable, "-u", "-c", peer], **kwargs)
        with patch.object(subprocess, "Popen", side_effect=spawn), patch.dict(os.environ, {
            name: "private" for name in ("CODEX_THREAD_ID", "CODEX_TOOL_CALL_ID",
                                         "CODEX_SESSION_ID", "CODEX_APP_TOOLS_PIPE_PATH")
        }):
            rpc = fb._Rpc("mock-codex", threading.Event())
            try:
                with self.assertRaises(fb.RpcRejected):
                    rpc.call("thread/list", {})
            finally:
                rpc.close()
        self.assertIsNotNone(rpc.proc.poll())
        self.assertFalse(rpc.reader.is_alive())


if __name__ == "__main__":
    unittest.main()
