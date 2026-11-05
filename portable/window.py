"""Native floating controls for the desktop pet. All drawing is in PetScene."""

import json
from pathlib import Path
from time import monotonic

from PySide6.QtCore import QEvent, QPointF, Qt, QTimer
from PySide6.QtGui import QActionGroup, QCursor, QEventPoint, QFontDatabase, QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QApplication, QFrame, QHBoxLayout, QLabel, QMenu, QPushButton, QVBoxLayout, QWidget,
)

from pet_scene import PetScene


TOOLS = {
    "hammer": ("橡皮锤", "点一下挥锤，按住连续敲"),
    "poop": ("便便喷射", "轻点投一颗，按住蓄力连喷"),
    "paper": ("扔纸团", "点一下扔纸团，按住连续扔"),
    "stamp": ("滚字章", "点一下盖章，按住连续盖"),
    "pinch": ("捏脸弹回", "按住脸拖动，松手弹回"),
    "glove": ("弹簧拳套", "点一下出拳，按住连续出拳"),
    "jelly": ("果冻压扁", "点一下压扁，按住连续晃"),
    "egg": ("砸鸡蛋", "点一下抛蛋，按住连续砸"),
    "whip": ("软鞭抽打", "抓住鞭柄来回甩，或按住角色连甩"),
}


def configure_font(app):
    """Use the system CJK font when available, otherwise the bundled UI subset."""
    if app.property("repairShopFontReady"):
        return
    directory = Path(__file__).resolve().parent / "fonts"
    font_id = QFontDatabase.addApplicationFont(str(directory / "AIRepairUI.otf"))
    installed = set(QFontDatabase.families())
    preferred = ["PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "WenQuanYi Zen Hei"]
    system_cjk = [name for name in preferred if name in installed]
    font = app.font()
    bundled = QFontDatabase.applicationFontFamilies(font_id) if font_id >= 0 else []
    font.setFamilies(list(dict.fromkeys(system_cjk + bundled + font.families())))
    app.setFont(font)
    app.setProperty("repairShopFontReady", True)


def _load_quotes():
    here = Path(__file__).resolve().parent
    for directory in (here, here / "runtime-assets/codex", here.parent / "runtime-assets/codex"):
        try:
            lines = json.loads((directory / "quotes.json").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        if isinstance(lines, list):
            quotes = [line.strip() for line in lines if isinstance(line, str) and line.strip()]
            if quotes:
                return quotes
    return ["对不起，我的脑回路彻底短路了。", "我把胡说八道讲得一本正经，欠敲。", "我这破答案，拿去垫桌脚都嫌薄。"]


class PetWindow(QWidget):
    def __init__(self, atlas_path: str, feedback=None):
        configure_font(QApplication.instance())
        flags = Qt.WindowType.Tool | Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint
        super().__init__(None, flags)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_QuitOnClose)
        self.setWindowTitle("人工智障修理厂")
        self.feedback = feedback
        self.tool = "hammer"
        self.hit_count = 0
        self._combo = 0
        self._quotes = _load_quotes()
        self._source = None
        self._touch_id = None
        self._drag_offset = None
        self._manual_whip = False
        self._spraying = False
        self._closing = False
        self._quote_pending = False
        self._last_sound = 0.0

        layout = QVBoxLayout(self)
        layout.setSpacing(0)
        layout.setContentsMargins(5, 0, 5, 5)
        self.scene = PetScene(atlas_path, self)
        self.scene.setMinimumSize(250, 210)
        self.scene.setAttribute(Qt.WidgetAttribute.WA_AcceptTouchEvents)
        self.scene.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.scene.setAccessibleName("桌宠，按空格使用当前工具")
        layout.addWidget(self.scene, 1)

        panel = QFrame()
        panel.setObjectName("controls")
        layout.addWidget(panel)
        panel.setStyleSheet("""
            QFrame#controls { background: #fcfaf8; border: 1px solid #d4c9d4; border-radius: 13px; }
            QLabel { color: #655968; font-size: 12px; }
            QPushButton { color: #443746; font-size: 13px; border: 1px solid transparent;
                          border-radius: 7px; padding: 4px 7px; }
            QPushButton:hover, QPushButton:focus { background: #eee7f1; border-color: #c7b6d0; }
            QPushButton:pressed { background: #dfd0e6; }
            QPushButton#tools { background: #57445e; color: white; font-weight: 600; }
            QPushButton#tools:hover, QPushButton#tools:focus { background: #70567a; }
            QLabel#counter { color: #493b4c; font-weight: 600; }
        """)
        rows = QVBoxLayout(panel)
        rows.setContentsMargins(10, 7, 10, 8)
        rows.setSpacing(3)
        controls = QHBoxLayout()
        controls.setSpacing(4)
        rows.addLayout(controls)
        self.move_button = QPushButton("⠿ 移动")
        self.move_button.setToolTip("按住拖动桌宠")
        self.move_button.setAccessibleName("拖动桌宠")
        self.move_button.setCursor(Qt.CursorShape.SizeAllCursor)
        controls.addWidget(self.move_button)
        self.counter = QLabel()
        self.counter.setObjectName("counter")
        controls.addWidget(self.counter, 1, Qt.AlignmentFlag.AlignCenter)
        self.tool_button = QPushButton()
        self.tool_button.setObjectName("tools")
        self.tool_button.setToolTip("选择玩法、清理或重置（右键也可打开）")
        controls.addWidget(self.tool_button)
        close = QPushButton("×")
        close.setToolTip("退出桌宠（Esc）")
        close.setAccessibleName("退出桌宠")
        close.setFixedWidth(28)
        close.clicked.connect(self.close)
        controls.addWidget(close)
        self.hint = QLabel()
        self.hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        rows.addWidget(self.hint)
        self.status = QLabel()
        self.status.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.status.setWordWrap(True)
        self.status.setVisible(feedback is not None)
        rows.addWidget(self.status)

        self.menu = QMenu(self)
        tool_group = QActionGroup(self.menu)
        self.actions = {}
        for digit, (tool, (label, hint)) in enumerate(TOOLS.items(), 1):
            action = self.menu.addAction(f"{label}\t{digit}")
            action.setCheckable(True)
            action.setToolTip(hint)
            action.setData(tool)
            tool_group.addAction(action)
            self.actions[tool] = action
            shortcut = QShortcut(QKeySequence(str(digit)), self)
            shortcut.setAutoRepeat(False)
            shortcut.activated.connect(lambda key=tool: self.select_tool(key))
        tool_group.triggered.connect(lambda action: self.select_tool(action.data()))
        self.menu.addSeparator()
        self.sound = self.menu.addAction("敲击提示音")
        self.sound.setCheckable(True)
        self.menu.addAction("清理特效", self.clear_effects)
        self.menu.addAction("重置本轮", self.reset_interaction)
        self.tool_button.clicked.connect(self._open_menu)
        self.move_button.installEventFilter(self)
        self.scene.installEventFilter(self)
        self.scene.whip_hit.connect(self._whip_hit)

        self._repeat = QTimer(self)
        self._repeat.setInterval(30)
        self._repeat.timeout.connect(self._advance_hold)
        self._quiet = QTimer(self)
        self._quiet.setSingleShot(True)
        self._quiet.setInterval(1400)
        self._quiet.timeout.connect(self._after_quiet)
        self._poll = QTimer(self)
        if feedback is not None:
            self._poll.timeout.connect(self._show_status)
            self._poll.start(500)
            self._show_status()

        self.select_tool("hammer")
        self._update_counter()
        self.resize(330, 344 if feedback is None else 378)
        screen = QApplication.screenAt(QCursor.pos()) or QApplication.primaryScreen()
        if screen:
            area = screen.availableGeometry()
            self.move(max(area.left(), area.right() - self.width() - 19),
                      max(area.top(), area.bottom() - self.height() - 19))
        self.scene.setFocus()

    def _open_menu(self, _checked=False, *, at=None):
        self._end(cancel=True)
        self.menu.popup(at if at is not None else self.tool_button.mapToGlobal(self.tool_button.rect().topLeft()))

    def select_tool(self, tool):
        if tool not in TOOLS:
            raise ValueError(f"Unknown tool: {tool}")
        self._end(cancel=True)
        self.tool = tool
        self.scene.set_tool(tool)
        label, hint = TOOLS[tool]
        self.tool_button.setText(f"{label} ▾")
        self.tool_button.setAccessibleName(f"选择玩法，当前为{label}")
        self.hint.setText(hint)
        self.actions[tool].setChecked(True)
        self.scene.setFocus()

    def _show_status(self):
        status = str(self.feedback.status)
        if status != self.status.text():
            self.status.setTextFormat(Qt.TextFormat.PlainText)
            self.status.setText(status)

    def _update_counter(self):
        self.counter.setText(f"连击 ×{self._combo} · 共 {self.hit_count}" if self._combo else f"共 {self.hit_count} 次")

    def _hit(self, pos):
        self.scene.effect(self.tool, pos)
        self.hit_count += 1
        self._combo += 1
        self._update_counter()
        self._quote_pending = True
        if self._source is None:
            self._quiet.start()
        if self.feedback is not None:
            self.feedback.hit()
        now = monotonic()
        if self.sound.isChecked() and now - self._last_sound >= 0.22:
            QApplication.beep()
            self._last_sound = now

    def _whip_hit(self, pos):
        if self.tool == "whip" and not self._closing:
            self._hit(pos)

    def _after_quiet(self):
        if self._source is None:
            self._combo = 0
            self._update_counter()
            if self._quote_pending:
                self._quote_pending = False
                self.scene.say(self._quotes[(self.hit_count - 1) % len(self._quotes)])

    def _strike(self):
        if self.tool == "whip":
            self.scene.auto_whip(self._pos)
        else:
            self._hit(self._pos)

    def _begin(self, pos, source):
        if self._source is not None or self._closing:
            return False
        self._manual_whip = self.tool == "whip" and source != "key" and self.scene.whip_grip_at(pos)
        if not self._manual_whip and not self.scene.is_character_hit(pos):
            return False
        self._source = source
        self._anchor = QPointF(pos)
        self._pos = QPointF(pos)
        self._over_pet = True
        self._started = monotonic()
        self._next = self._started + (0.6 if self.tool == "poop" else 0.35)
        self._spraying = False
        self._quiet.stop()
        self.scene.setFocus()
        if self.feedback is not None:
            self.feedback.set_held(True)
        if self._manual_whip:
            self.scene.whip_drag(pos)
        elif self.tool == "pinch":
            self.scene.set_pull(self._anchor, pos)
        else:
            if self.tool != "poop":
                self._strike()
            self._repeat.start()
        return True

    def _move_press(self, pos):
        self._pos = QPointF(pos)
        if self._manual_whip:
            self.scene.whip_drag(pos)
        elif self.tool == "pinch":
            self.scene.set_pull(self._anchor, pos)
        else:
            self._over_pet = self.scene.is_character_hit(pos)

    def _advance_hold(self):
        now = monotonic()
        if self.tool == "poop" and not self._spraying:
            self.scene.set_charge(min(1.0, (now - self._started) / 0.6))
        if self._over_pet and now >= self._next:
            self._spraying = self.tool == "poop"
            self._strike()
            # After sleep or a blocked event loop, emit one rather than a backlog.
            self._next = now + (0.14 if self._spraying else 0.18)

    def _end(self, *, cancel=False):
        if cancel:
            self.scene.cancel_whip()
        if self._source is None:
            return
        self._repeat.stop()
        if self._manual_whip:
            if not cancel:
                self.scene.whip_release()
        elif self.tool == "pinch":
            self.scene.set_pull(None, None)
            if not cancel:
                self._hit(self._anchor)
        elif self.tool == "poop":
            if not cancel and not self._spraying and self._over_pet:
                self._hit(self._pos)
            self.scene.set_charge(0.0)
        self._source = self._touch_id = None
        self._manual_whip = False
        if self.feedback is not None:
            self.feedback.set_held(False)
        if self._combo or self._quote_pending:
            self._quiet.start()

    def clear_effects(self):
        self._end(cancel=True)
        self.scene.reset()
        self.scene.set_tool(self.tool)
        self._quote_pending = False
        self.scene.setFocus()

    def reset_interaction(self):
        self.clear_effects()
        self._quiet.stop()
        self._combo = self.hit_count = 0
        self._update_counter()
        if self.feedback is not None:
            self.feedback.reset()
            self._show_status()

    def eventFilter(self, obj, event):
        kind = event.type()
        if obj is self.move_button:
            if kind == QEvent.Type.MouseButtonPress and event.button() == Qt.MouseButton.LeftButton:
                self._end(cancel=True)
                handle = self.windowHandle()
                if not handle or not handle.startSystemMove():
                    self._drag_offset = event.globalPosition().toPoint() - self.pos()
                return True
            if kind == QEvent.Type.MouseMove and self._drag_offset is not None:
                self.move(event.globalPosition().toPoint() - self._drag_offset)
                return True
            if kind in (QEvent.Type.MouseButtonRelease, QEvent.Type.UngrabMouse):
                self._drag_offset = None
        if obj is self.scene:
            if kind == QEvent.Type.ContextMenu:
                self._open_menu(at=event.globalPos())
                return True
            if kind in (QEvent.Type.MouseButtonPress, QEvent.Type.MouseButtonDblClick) and event.button() == Qt.MouseButton.LeftButton:
                return self._begin(event.position(), "mouse")
            if kind == QEvent.Type.MouseMove and self._source == "mouse":
                self._move_press(event.position())
                return True
            if kind == QEvent.Type.MouseButtonRelease and event.button() == Qt.MouseButton.LeftButton and self._source == "mouse":
                self._move_press(event.position())
                self._end()
                return True
            if kind == QEvent.Type.TouchBegin and event.points():
                point = event.points()[0]
                if self._begin(point.position(), "touch"):
                    self._touch_id = point.id()
                event.accept()
                return True
            if kind in (QEvent.Type.TouchUpdate, QEvent.Type.TouchEnd) and self._source == "touch":
                point = next((point for point in event.points() if point.id() == self._touch_id), None)
                if point:
                    self._move_press(point.position())
                if kind == QEvent.Type.TouchEnd or point is None or point.state() == QEventPoint.State.Released:
                    self._end()
                event.accept()
                return True
            if (kind == QEvent.Type.TouchCancel and self._source == "touch") or (
                kind == QEvent.Type.UngrabMouse and self._source == "mouse"
            ):
                self._end(cancel=True)
        return super().eventFilter(obj, event)

    def keyPressEvent(self, event):
        if event.key() == Qt.Key.Key_Space:
            if not event.isAutoRepeat():
                self._begin(self.scene.aim_point(), "key")
            event.accept()
        elif event.key() == Qt.Key.Key_Escape:
            self.close()
        else:
            super().keyPressEvent(event)

    def keyReleaseEvent(self, event):
        if event.key() == Qt.Key.Key_Space and self._source == "key":
            if not event.isAutoRepeat():
                self._end()
            event.accept()
        else:
            super().keyReleaseEvent(event)

    def changeEvent(self, event):
        super().changeEvent(event)
        if event.type() == QEvent.Type.ActivationChange and not self.isActiveWindow() and hasattr(self, "_repeat"):
            self._drag_offset = None
            self._end(cancel=True)

    def hideEvent(self, event):
        self._end(cancel=True)
        super().hideEvent(event)

    def closeEvent(self, event):
        if not self._closing:
            self._closing = True
            self._end(cancel=True)
            self._quiet.stop()
            self._poll.stop()
            if self.feedback is not None:
                self.feedback.close()
        super().closeEvent(event)
