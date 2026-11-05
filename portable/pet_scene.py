"""Native Qt sprite renderer. Public point arguments and the whip signal use widget coordinates."""
from __future__ import annotations

from dataclasses import dataclass
from math import cos, exp, hypot, pi, sin
from time import monotonic

from PySide6.QtCore import QPointF, QRectF, QSize, Qt, QTimer, Signal
from PySide6.QtGui import (
    QColor, QFont, QImage, QLinearGradient, QPainter, QPainterPath,
    QPen, QPolygonF, QRadialGradient,
)
from PySide6.QtWidgets import QWidget


P = QPointF
R = QRectF
INK = "#49394e"
GOLD = "#f8bf59"
ROSE = "#e96778"
LILAC = "#aa8ad2"
MINT = "#92d9d0"
SPRITE = R(44, 18, 192, 208)
GRIP_HOME = P(43, 180)
TAU = 2 * pi
# Animation lifetime and exact contact time in seconds.
TIMING = {
    "hammer": (.62, .12), "poop": (2.5, .26), "paper": (1.65, .49),
    "stamp": (1.82, .22), "pinch": (.55, 0), "glove": (.73, .18),
    "jelly": (1.25, .12), "egg": (2.05, .29), "whip": (.4, 0),
}


def clamp(v, low=0.0, high=1.0):
    return min(high, max(low, v))


def pen(color, width=1):
    return QPen(QColor(color), width, Qt.PenStyle.SolidLine,
                Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)


def style(p, fill=None, outline=None, width=1):
    p.setPen(pen(outline, width) if outline else Qt.PenStyle.NoPen)
    p.setBrush(QColor(fill) if isinstance(fill, str) else
               fill if fill is not None else Qt.BrushStyle.NoBrush)


def rounded(p, x, y, w, h, radius, fill, outline=None, width=1):
    style(p, fill, outline, width)
    p.drawRoundedRect(R(x, y, w, h), radius, radius)


def oval(p, x, y, rx, ry, fill, outline=None, width=1):
    style(p, fill, outline, width)
    p.drawEllipse(P(x, y), rx, ry)


def line(p, a, b, color, width=1):
    p.setPen(pen(color, width))
    p.drawLine(a, b)


def burst(p, target, age, color):
    if not 0 <= age < .36:
        return
    p.save()
    progress = age / .36
    p.setOpacity(p.opacity() * (1 - progress))
    for i in range(7):
        angle = i * TAU / 7 + .21
        direction = P(cos(angle), sin(angle) * .85)
        inner = 12 + 34 * progress
        line(p, target + direction * inner, target + direction * (inner + 7), color, 2.5)
    p.restore()


def flight(start, end, t, arc):
    f = clamp(t)
    return start + (end - start) * f + P(0, -sin(f * pi) * arc)


@dataclass
class Impact:
    tool: str
    target: QPointF
    origin: QPointF
    born: float
    sequence: int
    charged: bool
    contacted: bool = False


class PetScene(QWidget):
    whip_hit = Signal(QPointF)

    def __init__(self, atlas_path, parent=None):
        super().__init__(parent)
        atlas = QImage(atlas_path) if isinstance(atlas_path, QImage) else QImage(str(atlas_path))
        if atlas.isNull() or atlas.size() != QSize(1536, 1872):
            raise ValueError("Codex 图集须为 1536 × 1872 的 WebP 或 PNG 图像")
        self._idle = [atlas.copy(i * 192, 0, 192, 208) for i in range(6)]
        self._reactions = [atlas.copy(i * 192, 5 * 208, 192, 208) for i in range(8)]
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setMouseTracking(True)
        self._tool = "hammer"
        self._start = monotonic()
        self._timer = QTimer(self)
        self._timer.setTimerType(Qt.TimerType.PreciseTimer)
        self._timer.timeout.connect(self._advance)
        self.reset()

    def sizeHint(self):
        return QSize(280, 240)

    def _view(self):
        scale = max(.01, min(self.width() / 280, self.height() / 240))
        return scale, P((self.width() - 280 * scale) / 2, (self.height() - 240 * scale) / 2)

    def _local(self, pos):
        scale, offset = self._view()
        return (pos - offset) / scale

    def _widget(self, pos):
        scale, offset = self._view()
        return offset + pos * scale

    def aim_point(self):
        return self._widget(P(140, 129))

    def is_character_hit(self, pos):
        point = self._local(pos) - SPRITE.topLeft()
        x, y = int(point.x()), int(point.y())
        if not self._idle[0].rect().contains(x, y):
            return False
        # A small touch allowance also makes it practical to catch an animated edge.
        frame = self._frame(monotonic())
        return any(frame.valid(x + dx, y + dy) and frame.pixelColor(x + dx, y + dy).alpha() > 32
                   for dx, dy in ((0, 0), (4, 0), (-4, 0), (0, 4), (0, -4)))

    def reset(self):
        self._effects = []
        self._marks = []
        self._last_hit = -1e9
        self._reaction_start = -1e9
        self._reaction_tool = None
        self._sequence = 0
        self._charge = 0
        self._pull_anchor = None
        self._pull = P()
        self._release = -1e9
        self._speech = ""
        self._speech_until = 0
        self.cancel_whip()
        self._wake()

    def set_tool(self, tool):
        if tool not in TIMING:
            raise ValueError(f"Unknown tool: {tool}")
        if tool != self._tool:
            self.cancel_whip()
            self.set_pull(None, None)
            self._charge = 0
            self._tool = tool
            self.update()

    def set_charge(self, progress):
        self._charge = clamp(progress)
        self.update()

    def say(self, text):
        self._speech = str(text)
        self._speech_until = monotonic() + 2.8
        self.update()

    def set_pull(self, start, end):
        if start is None or end is None:
            if self._pull_anchor is not None and self._release < 0:
                self._release = monotonic()
        else:
            self._pull_anchor = self._local(start)
            delta = self._local(end) - self._pull_anchor
            length = hypot(delta.x(), delta.y())
            self._pull = delta * min(1.0, 38 / max(1, length))
            self._release = -1e9
        self._wake()

    def effect(self, tool, pos, start=None):
        if tool not in TIMING:
            raise ValueError(f"Unknown tool: {tool}")
        self._sequence += 1
        now = monotonic()
        target = self._local(pos)
        origin = self._local(start) if start is not None else P(25 if self._sequence % 2 else 255, 177)
        if tool == "pinch":
            if self._pull_anchor is None or self._pull.manhattanLength() < 2:
                self._pull_anchor = origin if start is not None else target
                self._pull = P(-18 if self._pull_anchor.x() < 140 else 18, -4)
                self._release = now
            target = P(self._pull_anchor)
        item = Impact(tool, target, origin, now, self._sequence, self._charge >= .98)
        self._effects = self._effects[-27:] + [item]
        if TIMING[tool][1] == 0:
            self._contact(item, now)
        self._wake()

    def _contact(self, item, now):
        item.contacted = True
        if now - self._last_hit > .6:
            self._reaction_start = now
        self._last_hit = now
        self._reaction_tool = item.tool
        self._marks = self._marks[-5:] + [(item.target, now)]
        if item.tool == "stamp":
            self._effects = [e for e in self._effects if e is item or e.tool != "stamp" or not e.contacted]

    def _frame(self, now):
        if now - self._last_hit < .61:
            return self._reactions[int(max(0, now - self._reaction_start) * 13) % 8]
        return self._idle[int((now - self._start) * 8) % 6]

    def _wake(self):
        if self.isVisible() and (not self._timer.isActive() or self._timer.interval() != 16):
            self._timer.start(16)
        self.update()

    def showEvent(self, event):
        super().showEvent(event)
        self._last_tick = monotonic()
        self._wake()

    def hideEvent(self, event):
        self._timer.stop()
        super().hideEvent(event)

    def _advance(self):
        now = monotonic()
        dt = clamp(now - self._last_tick, 0, .05)
        self._last_tick = now
        self._effects = [e for e in self._effects if now - e.born < TIMING[e.tool][0]]
        for e in self._effects[:]:
            if not e.contacted and now - e.born >= TIMING[e.tool][1]:
                self._contact(e, e.born + TIMING[e.tool][1])
        self._marks = [m for m in self._marks if now - m[1] < 2.5]
        if self._release > 0 and now - self._release > .72:
            self._pull_anchor = None
        active_whip = self._tool == "whip" and (
            self._auto_target is not None or self._dragging or now - self._motion < 1.35)
        if active_whip:
            self._advance_whip(now, dt)
        active = active_whip or self._effects or self._pull_anchor is not None
        interval = 16 if active else 125
        if self._timer.interval() != interval:
            self._timer.start(interval)
        self.update()

    # All whip interactions move the same chain. Keeping the chain in stage coordinates
    # preserves its length across DPI and window-size changes.
    def cancel_whip(self):
        self._handle = P(GRIP_HOME)
        self._rest = [P(self._handle)]
        for angle in (1.0, .85, .65, .34, .03, -.28, -.57, -.91, -1.32, -1.77, -2.23, -2.68):
            self._rest.append(self._rest[-1] + P(cos(angle), sin(angle)) * 10)
        self._nodes = [P(q) for q in self._rest]
        self._prev = [P(q) for q in self._nodes]
        self._dragging = False
        self._motion = -1e9
        self._last_whip = -1e9
        self._auto_target = None
        self._physics_remainder = 0
        self.update()

    def whip_grip_at(self, pos):
        return self._tool == "whip" and (self._local(pos) - (self._handle + P(-4, 8))).manhattanLength() < 22

    def whip_drag(self, pos):
        if self._tool != "whip":
            return
        pointer = self._local(pos)
        if not self._dragging:
            self._drag_offset = self._handle - pointer
            self._dragging = True
            self._auto_target = None
        next_pos = pointer + self._drag_offset
        next_pos = P(clamp(next_pos.x(), 7, 273), clamp(next_pos.y(), 7, 228))
        if (self._handle - next_pos).manhattanLength() > .6:
            self._motion = monotonic()
        self._handle = next_pos
        self._wake()

    def whip_release(self):
        if self._dragging:
            self._dragging = False
            self._motion = monotonic()
            self._wake()

    def auto_whip(self, pos):
        if self._tool == "whip" and not self._dragging and self._auto_target is None:
            self._auto_target = self._local(pos)
            self._auto_born = monotonic()
            self._motion = self._auto_born
            self._auto_from = [P(q) for q in self._nodes]
            self._wake()

    def _advance_whip(self, now, dt):
        if self._auto_target is not None:
            # The curve straightens from the handle towards the contact point.
            t = clamp((now - self._auto_born) / .14)
            eased = t * t * (3 - 2 * t)
            direction = self._auto_target - GRIP_HOME
            reach = hypot(direction.x(), direction.y())
            direction /= max(1, reach)
            handle = self._auto_target - direction * min(reach, 117)
            normal = P(-direction.y(), direction.x())
            self._prev = [P(q) for q in self._nodes]
            for i, old in enumerate(self._auto_from):
                f = i / (len(self._nodes) - 1)
                arc = -sin(pi * f) * sin(pi * t) * 42
                swept = handle + (self._auto_target - handle) * f + normal * arc
                self._nodes[i] = old + (swept - old) * eased
            self._handle = P(self._nodes[0])
            if t >= 1:
                contact = self._widget(self._auto_target)
                self._auto_target = None
                self._motion = self._last_whip = now
                self.whip_hit.emit(contact)
            return
        step = 1 / 90
        self._physics_remainder += dt
        while self._physics_remainder >= step:
            self._physics_remainder -= step
            self._step_chain(now, step)

    def _step_chain(self, now, step):
        old = [P(q) for q in self._nodes]
        if not self._dragging:
            self._handle += (GRIP_HOME - self._handle) * .13
        self._nodes[0] = P(self._handle)
        for i in range(1, len(old)):
            velocity = (old[i] - self._prev[i]) * (.97 if self._dragging else .91)
            force = P(0, 360 * step * step)
            if not self._dragging:
                force += (self._rest[i] - old[i]) * .025
            self._nodes[i] += velocity + force
        self._prev = old
        for _ in range(5):
            for i in range(1, len(old)):
                gap = self._nodes[i] - self._nodes[i - 1]
                length = max(.001, hypot(gap.x(), gap.y()))
                correction = gap * ((length - 10) / length)
                if i == 1:
                    self._nodes[i] -= correction
                else:
                    self._nodes[i] -= correction * .5
                    self._nodes[i - 1] += correction * .5
                q = self._nodes[i]
                self._nodes[i] = P(clamp(q.x(), 3, 277), clamp(q.y(), 3, 224))
        if self._dragging and now - self._motion < .21 and now - self._last_whip > .16:
            # Sweep each moving segment, including its endpoints between physics frames.
            face = QPainterPath()
            face.addEllipse(R(83, 70, 112, 117))
            for i in range(len(old) - 1, 1, -1):
                if (self._nodes[i] - old[i]).manhattanLength() < 1.4:
                    continue
                sweep = QPainterPath()
                sweep.addPolygon(QPolygonF([old[i-1], old[i], self._nodes[i], self._nodes[i-1]]))
                sweep.closeSubpath()
                overlap = face.intersected(sweep)
                if not overlap.isEmpty():
                    self._last_whip = now
                    self.whip_hit.emit(self._widget(overlap.boundingRect().center()))
                    break

    def paintEvent(self, event):
        now = monotonic()
        p = QPainter(self)
        try:
            p.setRenderHint(QPainter.RenderHint.Antialiasing)
            p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
            scale, offset = self._view()
            p.translate(offset)
            p.scale(scale, scale)
            if self._charge > .01:
                rim = R(58, 48, 164, 164)
                style(p, None, "#87a4a9bc", 2)
                p.drawEllipse(rim)
                p.setPen(pen(GOLD, 3.6))
                p.drawArc(rim, 90 * 16, -int(360 * 16 * self._charge))
            self._draw_pet(p, now)
            for item in self._effects:
                p.save()
                age = max(0, now - item.born)
                p.setOpacity(clamp((TIMING[item.tool][0] - age) / .28))
                self._draw_effect(p, item, age)
                p.restore()
            if self._tool == "whip":
                self._draw_chain(p)
            if self._speech and now < self._speech_until:
                self._draw_speech(p, now)
        finally:
            p.end()

    def _draw_pet(self, p, now):
        age = now - self._last_hit
        frame = self._frame(now)
        if self._marks:
            # Composite against the atlas alpha channel, including holes and soft edges.
            frame = frame.convertToFormat(QImage.Format.Format_ARGB32_Premultiplied)
            overlay = QPainter(frame)
            overlay.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceAtop)
            for point, born in self._marks:
                strength = clamp(1 - (now - born) / 2.5)
                center = point - SPRITE.topLeft()
                wash = QRadialGradient(center, 27)
                wash.setColorAt(0, QColor(248, 69, 77, int(132 * strength)))
                wash.setColorAt(.56, QColor(244, 97, 104, int(72 * strength)))
                wash.setColorAt(1, QColor(244, 97, 104, 0))
                oval(overlay, center.x(), center.y(), 27, 27, wash)
            overlay.end()
        p.save()
        spring = sin(age * 28) * exp(-age * 7) if age < .8 else 0
        shake = sin(age * 67) * exp(-age * 10) * 5 if age < .5 else 0
        bounce = -abs(spring) * 6
        squash = .14 * spring
        if self._reaction_tool == "jelly" and age < 1.08:
            squash += .56 * (min(1, age / .08) if age < .18 else
                             cos((age - .18) * 16) * exp(-(age - .18) * 4.8))
        p.translate(140 + shake, 214 + bounce)
        if age < .64 and self._reaction_tool in ("glove", "whip"):
            p.rotate(13 * spring)
        p.scale(1 + squash * .65, 1 - squash)
        p.translate(-140, -214)
        if self._pull_anchor is None:
            p.drawImage(SPRITE, frame)
        else:
            velocity = P(self._pull)
            if self._release > 0:
                t = now - self._release
                velocity *= exp(-t * 8) * cos(t * 20)
            self._draw_pull(p, frame, velocity)
        p.restore()

    def _draw_pull(self, p, frame, delta):
        # Horizontal scanlines have a shared displaced seam. This keeps the far edge
        # of each cheek pinned while the grabbed area follows the pointer in both axes.
        anchor = self._pull_anchor - SPRITE.topLeft()
        seam = clamp(anchor.x(), 8, 184)
        centroid = clamp(anchor.y(), 20, 186)
        last_y = 0
        for y in range(0, 208, 2):
            weight = exp(-((y - centroid) / 48) ** 2)
            middle = clamp(seam + delta.x() * weight, 2, 190)
            next_y = y + 2 + delta.y() * exp(-((y + 2 - centroid) / 48) ** 2)
            height = max(.3, next_y - last_y + .03)
            for x, w, sx, sw in ((0, middle, 0, seam), (middle, 192-middle, seam, 192-seam)):
                p.drawImage(R(SPRITE.x() + x, SPRITE.y() + last_y, w, height),
                            frame, R(sx, y, sw, 2))
            last_y = next_y

    def _draw_chain(self, p):
        for i in range(1, len(self._nodes)):
            a, b = self._nodes[i-1:i+1]
            radius = 4.8 - 3.1 * i / (len(self._nodes) - 1)
            line(p, a, b, "#573b65", radius + 1.6)
            line(p, a, b, "#ad7db5", radius)
            mark = a + (b - a) * .56
            line(p, mark, mark + (b - a) * .22, "#d4accf", max(.65, radius * .53))
        p.save()
        p.translate(self._handle)
        p.rotate(29)
        rounded(p, -5, -3, 10, 27, 4, "#9b684a", INK, 1.4)
        for y in range(1, 22, 5):
            line(p, P(-3.7, y + 2), P(3.7, y - .4), "#d1a273", 1.7)
        rounded(p, -6, -3, 12, 5, 1.8, GOLD, INK, 1)
        p.restore()

    def _draw_speech(self, p, now):
        font = self.font()
        font.setPixelSize(12)
        font.setWeight(QFont.Weight.DemiBold)
        p.setFont(font)
        metrics = p.fontMetrics()
        # Prefer a readable two-line bubble for longer local quotes.
        flags = Qt.AlignmentFlag.AlignCenter | Qt.TextFlag.TextWordWrap
        bounds = metrics.boundingRect(R(0, 0, 250, 60).toRect(), int(flags), self._speech)
        width = clamp(bounds.width() + 22, 44, 268)
        height = clamp(bounds.height() + 10, 25, 58)
        left = (280 - width) / 2
        p.setOpacity(clamp((self._speech_until - now) / .24))
        rounded(p, left, 1, width, height, 9, "#fffaf4", "#6e6578", 1.2)
        style(p, "#fffaf4", "#6e6578", 1.2)
        tail = QPainterPath(P(134, height + .8))
        tail.lineTo(138, height + 7)
        tail.lineTo(146, height + .8)
        p.drawPath(tail)
        line(p, P(135, height), P(145, height), "#fffaf4", 2)
        p.setPen(QColor(INK))
        p.drawText(R(left + 5, 2, width - 10, height - 2), int(flags), self._speech)

    def _draw_effect(self, p, e, age):
        target, tool = e.target, e.tool
        contact = age - TIMING[tool][1]
        if tool in ("egg", "paper", "poop"):
            self._draw_throw(p, e, age, contact)
        elif tool == "hammer":
            if age < .37:
                angle = -66 * (1 - clamp(age / .12)) + 30 * clamp(contact / .25)
                p.save()
                pivot = target + P(24, 36)
                p.translate(pivot)
                p.rotate(angle - 27)
                rounded(p, -3.8, -39, 7.6, 48, 3, "#d5a564", INK, 1.3)
                rounded(p, -24, -52, 48, 24, 6, "#ac8bc8", INK, 1.75)
                rounded(p, -20, -49, 38, 5, 2, "#ded0eb")
                rounded(p, -24, -51, 7, 22, 3, "#cda7d9", INK, 1)
                p.restore()
            burst(p, target, contact, GOLD)
        elif tool == "stamp":
            if contact >= 0 and e.contacted:
                p.save()
                p.translate(target)
                p.rotate(-11)
                font = self.font()
                font.setPixelSize(44)
                font.setWeight(QFont.Weight.Black)
                p.setFont(font)
                p.setPen(QColor("#c73550"))
                if p.fontMetrics().inFontUcs4(ord("滚")):
                    p.drawText(R(-34, -33, 68, 66), Qt.AlignmentFlag.AlignCenter, "滚")
                else:
                    # A hand-drawn imprint for desktops without a CJK font.
                    p.setPen(pen("#c73550", 2.8))
                    for stroke in (
                        ((-17,-14),(-12,-11)), ((-18,-5),(-13,-2)), ((-18,14),(-11,4)),
                        ((0,-17),(3,-14)), ((-9,-11),(17,-11)), ((0,-8),(-7,-3)),
                        ((8,-8),(15,-3)), ((3,-7),(-1,-2),(-4,0),(8,-1)),
                        ((7,-5),(11,0)), ((3,1),(-2,6),(-10,10)), ((-2,6),(-2,17),(5,12)),
                        ((12,3),(7,8)), ((4,3),(8,9),(13,14),(18,16)),
                    ):
                        p.drawPolyline(QPolygonF([P(x,y) for x,y in stroke]))
                p.restore()
            if age < .5:
                lift = 68 * (1 - (age / .22) ** 2) if contact < 0 else 73 * (contact / .28) ** 1.4
                p.translate(target + P(0, -lift))
                rounded(p, -8, -32, 16, 27, 5, "#c88d55", INK, 1.3)
                oval(p, 0, -32, 13, 6, "#e5b370", INK, 1.3)
                rounded(p, -21, -9, 42, 12, 3, "#65516b", INK, 1.5)
                rounded(p, -19, -1, 38, 4, 1.5, ROSE)
        elif tool == "glove":
            extension = clamp(age / .18) if contact < .07 else clamp(1 - (contact - .07) / .35)
            if age < .64:
                origin = P(10, target.y() + 5)
                end = origin + (target - origin - P(16, 0)) * extension
                coil = QPainterPath(origin)
                for i in range(1, 23):
                    k = i / 22
                    coil.lineTo(origin + (end-origin) * k + P(0, 0 if i == 22 else (-1)**i * 4.5))
                style(p, None, "#665278", 2.4)
                p.drawPath(coil)
                p.save()
                p.translate(end)
                rounded(p, -8, -10, 15, 21, 3, "#bc4e69", INK, 1)
                rounded(p, 1, -16, 29, 27, 9, ROSE, INK, 1.5)
                oval(p, 7, 10, 7, 6, "#df5d72", INK, 1.1)
                rounded(p, 6, -13, 16, 3.3, 1.6, "#ffc2bc")
                p.restore()
            burst(p, target, contact, GOLD)
        elif tool == "jelly":
            if age < .47:
                down = clamp(age / .20) if age < .29 else 1 - clamp((age - .29) / .18)
                y = 21 + 110 * down
                line(p, P(140, y - 16), P(140, y + 1), "#68b3b0", 5)
                rounded(p, 59, y, 162, 9, 4, MINT, "#538c91", 1.4)
                rounded(p, 65, y + 1.4, 149, 2.5, 1.2, "#dcfbeb")
            if 0 <= contact < .74:
                progress = contact / .74
                oval(p, 140, 214, 68 + 23 * progress, 3 + 2 * sin(progress * 12), None,
                     QColor(91, 169, 160, int(155 * (1-progress))), 1.5)
        else:
            burst(p, target, contact, "#f79b73" if tool == "whip" else GOLD)

    def _draw_throw(self, p, e, age, contact):
        t = clamp(age / TIMING[e.tool][1])
        at = flight(e.origin, e.target, t, 39)
        if contact >= 0:
            if e.tool == "paper":
                side = 1 if e.sequence % 2 else -1
                at += P(side * contact * 30, 122 * contact ** 2 - 37 * contact)
            elif e.tool == "poop":
                at += P(sin(e.sequence) * max(0, contact-.5) * 3, max(0, contact-.46) ** 1.5 * 29)
            else:
                at += P(0, max(0, contact-.25) * 16)
        p.save()
        p.translate(at)
        if e.tool == "paper":
            p.rotate(age * 357 + e.sequence * 53)
            wobble = .12 * sin(age * 29)
            p.scale(1 + wobble, 1 - wobble)
            self._draw_paper(p)
        elif e.tool == "poop":
            grow = (1.65 if e.charged else 1) * (.52 + .48 * t)
            wobble = max(0, .15-contact) * .8 if contact >= 0 else 0
            p.scale(grow * (1+wobble), grow * (1-wobble))
            p.rotate(220 * t if contact < 0 else 9 * sin(e.sequence))
            self._draw_poop(p)
        elif contact < 0:
            p.rotate(272 * t)
            oval(p, 0, 0, 8, 12, "#fff7de", "#a88768", 1.3)
            oval(p, -2.9, -4, 2.6, 3.4, "#fffdf4")
        else:
            white = "#f5fff9ea"
            for i in range(7):
                a = TAU * i / 7 + e.sequence
                oval(p, 11*cos(a), 7*sin(a), 8.8, 7.4, white)
            rounded(p, -12, 3, 4.5, 7 + min(19, contact*18), 2.2, white)
            rounded(p, 9, 3, 3, 6 + min(14, contact*13), 1.5, white)
            oval(p, 0, 0, 10.7, 8.1, "#f6b72f", "#d3953b", .9)
            oval(p, -3, -2.7, 3.3, 1.7, "#ffdf7d")
            if contact < .55:
                for sign in (-1, 1):
                    p.save()
                    p.translate(sign * (20 + contact * 40), contact**2 * 88 - contact*46)
                    p.rotate(contact * sign * 180)
                    shell = QPainterPath(P(-6, 0))
                    for x, y in ((-3, -2), (0, 0), (3, -2), (6, 0)):
                        shell.lineTo(x, y)
                    shell.cubicTo(5, 9, -6, 8, -6, 0)
                    style(p, "#fff6d8", "#ae8d6a", 1)
                    p.drawPath(shell)
                    p.restore()
        p.restore()
        burst(p, e.target, contact, {"paper": LILAC, "poop": "#af815a", "egg": GOLD}[e.tool])

    @staticmethod
    def _draw_poop(p):
        body = QPainterPath(P(-11, 12))
        body.cubicTo(-19, 12, -19, 5, -12, 2)
        body.cubicTo(-15, -2, -11, -6, -6, -6)
        body.cubicTo(-7, -10, -1, -11, -2, -16)
        body.cubicTo(4, -13, 7, -11, 6, -7)
        body.cubicTo(13, -7, 15, -3, 11, 1)
        body.cubicTo(20, 5, 17, 12, 11, 12)
        body.quadTo(0, 15, -11, 12)
        grad = QLinearGradient(-9, -11, 10, 16)
        grad.setColorAt(0, QColor("#c18c64"))
        grad.setColorAt(1, QColor("#8c5947"))
        style(p, grad, "#694543", 1.4)
        p.drawPath(body)
        line(p, P(-7,-4), P(-2,-6), "#e3b38b", 1.7)
        for x in (-5, 5):
            oval(p, x, 3.5, 1.6, 2, "#4c3740")
            oval(p, x-.3, 2.8, .45, .55, "#fff2d9")
        smile = QPainterPath(P(-2.8, 7))
        smile.quadTo(0, 10, 2.8, 7)
        style(p, None, "#513943", 1.2)
        p.drawPath(smile)

    @staticmethod
    def _draw_paper(p):
        body = QPainterPath(P(-13,-5))
        for cx, cy, x, y in [(-14,-9,-9,-10),(-8,-16,-4,-13),(1,-17,5,-13),
                             (11,-14,11,-9),(16,-6,13,-2),(17,4,12,7),
                             (12,13,7,12),(3,17,-2,13),(-8,15,-10,10),
                             (-15,9,-13,4),(-17,1,-13,-5)]:
            body.quadTo(cx,cy,x,y)
        grad = QRadialGradient(P(-5,-6), 22)
        grad.setColorAt(0,QColor("#ffffff"))
        grad.setColorAt(.65,QColor("#f5f4e9"))
        grad.setColorAt(1,QColor("#bbc3b9"))
        style(p,grad,"#8d9a90",.85)
        p.drawPath(body)
        p.setClipPath(body, Qt.ClipOperation.IntersectClip)
        for x, y, angle in ((-9,-7,10),(-5,-12,50),(1,-8,25),(8,-9,94),(-12,1,25),
                            (-8,-3,50),(0,-4,65),(8,-2,110),(-6,4,70),(2,6,30),
                            (7,7,118),(-8,10,10),(-1,10,105)):
            p.save()
            p.translate(x,y)
            p.rotate(angle)
            crease = QPainterPath(P(0,0))
            crease.cubicTo(2,-1,3,1,4,2)
            crease.quadTo(5,4,7,3.5)
            crease.quadTo(3.3,5.4,2.7,2.8)
            crease.quadTo(1.8,1,0,0)
            shade = QLinearGradient(1,0,4,4)
            shade.setColorAt(0,QColor("#fefdf6"))
            shade.setColorAt(1,QColor("#b8c3b8"))
            style(p,shade)
            p.drawPath(crease)
            line(p,P(.3,-.25),P(2,.1),"#fffef6",.65)
            p.restore()
