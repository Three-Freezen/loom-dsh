#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""LOOM — DSH plugin guard. Builds the vector UI reference sheet and app icon."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import render as R

HERE = os.path.dirname(os.path.abspath(__file__))

# ---- DSH design tokens -----------------------------------------------------
THREAD = "#4176e6"  # DSH primary blue (deepseek-500)

DARK_THEME = dict(
    CANVAS="#141416", GLOW1="#1e3357", GLOW2="#153049", GLOW3="#241f33",
    BG="#151517", BASE="#151517", L1="#232324", L2="#2c2c2e", L3="#353638",
    TEXT="#f9fafb", TEXT2="#cfd3d6", TEXT3="#adb2b8", TEXT4="#81858c",
    LINK="#7aaaff", GREEN="#4ed17e", AMBER="#f59e0b", RED="#f25a5a",
    TOAST="#43454a", TOAST_FG="#ffffff", TOAST_BODY="#d9dde1",
    WHITE="#f9fafb", ON_DARK="#353638",
    BORDER1="#ffffff0f", BORDER2="#ffffff1f", BORDER3="#ffffff29",
    SIDEBAR="#1b1b1c", DIALOG_BG="#262729",
    PANEL_STOPS=[(0, "#2a2b2f"), (0.5, "#242527"), (1, "#202123")],
    SHADOW_PANEL="#0000008c", SHADOW_WINDOW="#000000a6",
    SHADOW_TOAST="#00000080", SHADOW_DIALOG="#00000073",
    SHADOW_BUTTON="#00000066", SHADOW_ICON="#00000066",
    TOKEN_TITLE="设计 TOKEN · 取自 DSH 0.2.2-rc.1 app.asar（深色）",
    TOKEN_COLORS=[("#151517", "背景"), ("#232324", "层 1"),
                  ("#2c2c2e", "层 2"), ("#43454a", "Toast"),
                  ("#f9fafb", "主文字"), ("#cfd3d6", "次文字"),
                  ("#7aaaff", "强调"), ("#4ed17e", "成功"),
                  ("#f59e0b", "警告"), ("#f25a5a", "危险")],
)
LIGHT_THEME = dict(
    CANVAS="#eef0f4", GLOW1="#d9e5fb", GLOW2="#dbe9f5", GLOW3="#e6e0f4",
    BG="#f2f4f7", BASE="#f2f4f7", L1="#ffffff", L2="#f7f8fa", L3="#eceef2",
    TEXT="#0f1115", TEXT2="#61666b", TEXT3="#81858c", TEXT4="#8b9097",
    LINK="#4176e6", GREEN="#22c55e", AMBER="#f59e0b", RED="#ef4444",
    TOAST="#43454a", TOAST_FG="#ffffff", TOAST_BODY="#e7eaee",
    WHITE="#0f1115", ON_DARK="#ffffff",
    BORDER1="#0000000f", BORDER2="#00000014", BORDER3="#0000001f",
    SIDEBAR="#f0f2f5", DIALOG_BG="#eef1f5",
    PANEL_STOPS=[(0, "#ffffff"), (0.5, "#fbfcfd"), (1, "#f4f6f9")],
    SHADOW_PANEL="#0f11152e", SHADOW_WINDOW="#0f111526",
    SHADOW_TOAST="#0f111526", SHADOW_DIALOG="#0f11151f",
    SHADOW_BUTTON="#0f111526", SHADOW_ICON="#0f111526",
    TOKEN_TITLE="设计 TOKEN · DSH light 主题",
    TOKEN_COLORS=[("#f2f4f7", "背景"), ("#ffffff", "层 1"),
                  ("#f7f8fa", "层 2"), ("#43454a", "Toast"),
                  ("#0f1115", "主文字"), ("#61666b", "次文字"),
                  ("#4176e6", "强调"), ("#22c55e", "成功"),
                  ("#f59e0b", "警告"), ("#ef4444", "危险")],
)
globals().update(DARK_THEME)
THEME_NAME = "dark"


def apply_theme(name):
    global THEME_NAME
    THEME_NAME = name
    globals().update(DARK_THEME if name == "dark" else LIGHT_THEME)


UI = "ui"
BRAND = "brand"
INK = "#191a1d"


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------
def cap(x, y, s, color=TEXT4, size=10.5, ls=0.6):
    R.text(x, y, s, size=size, color=color, weight=400, family=UI, ls=ls)


def hline(x1, x2, y, color=BORDER1, w=1):
    R.line(x1, y, x2, y, color=color, w=w)


def icon(name, cx, cy, s, color, w=1.35, fill=None):
    """16x16 icon grid, centered at (cx,cy), edge length s."""
    k = s / 16.0

    def P(x, y):
        return (cx - s / 2 + x * k, cy - s / 2 + y * k)

    def ln(x1, y1, x2, y2):
        R.line(*P(x1, y1), *P(x2, y2), color=color, w=w * k)

    def pl(pts, closed=False):
        R.poly([P(x, y) for x, y in pts], color=color, w=w * k, closed=closed)

    def ci(x, y, r, filled=False):
        R.circle(*P(x, y), r * k, fill=color if filled else None,
                 stroke=None if filled else color, sw=w * k)

    if name == "camera":
        pl([(2.2, 5.2), (2.2, 13.5), (13.8, 13.5), (13.8, 5.2), (11.0, 5.2),
            (10.2, 3.6), (5.8, 3.6), (5.0, 5.2)], closed=True)
        ci(8, 9.4, 3.0)
    elif name == "shield":
        pl([(8, 1.7), (13.9, 4.1), (13.9, 7.9), (13.9, 7.9), (13.5, 10.9),
            (11.4, 13.0), (8, 14.4), (4.6, 13.0), (2.5, 10.9), (2.1, 7.9),
            (2.1, 4.1)], closed=True)
        pl([(5.2, 8.0), (7.1, 10.0), (10.9, 6.1)])
    elif name == "folder":
        pl([(1.9, 3.9), (6.1, 3.9), (7.5, 5.6), (14.1, 5.6), (14.1, 13.2),
            (1.9, 13.2)], closed=True)
    elif name == "refresh":
        R.path([("M", *P(13.4, 8.2)),
                ("C", *P(13.4, 4.9), *P(11.0, 2.4), *P(7.9, 2.4)),
                ("C", *P(4.8, 2.4), *P(2.4, 4.8), *P(2.4, 7.9)),
                ("C", *P(2.4, 11.0), *P(4.8, 13.4), *P(7.9, 13.4)),
                ("C", *P(10.3, 13.4), *P(12.3, 12.0), *P(13.1, 9.9))],
               color=color, w=w * k)
        pl([(13.6, 9.9), (12.9, 6.6), (10.2, 7.6)])
    elif name == "gear":
        ci(8, 8, 2.4)
        import math as _m
        for a in range(0, 360, 45):
            r0, r1 = 4.6, 6.6
            x0 = 8 + r0 * _m.cos(_m.radians(a))
            y0 = 8 + r0 * _m.sin(_m.radians(a))
            x1 = 8 + r1 * _m.cos(_m.radians(a))
            y1 = 8 + r1 * _m.sin(_m.radians(a))
            ln(x0, y0, x1, y1)
    elif name == "power":
        ln(8, 2.2, 8, 7.6)
        R.path([("M", *P(4.7, 4.7)),
                ("C", *P(3.1, 6.2), *P(2.6, 8.4), *P(3.4, 10.6)),
                ("C", *P(4.4, 13.3), *P(6.0, 14.2), *P(8.0, 14.2)),
                ("C", *P(10.0, 14.2), *P(11.6, 13.3), *P(12.6, 10.6)),
                ("C", *P(13.4, 8.4), *P(12.9, 6.2), *P(11.3, 4.7))],
               color=color, w=w * k)
    elif name == "check":
        pl([(3.0, 8.4), (6.3, 11.7), (13.0, 4.3)])
    elif name == "arrow_up":
        ln(8, 13.2, 8, 3.4)
        pl([(4.7, 6.5), (8, 3.2), (11.3, 6.5)])
    elif name == "alert":
        pl([(8, 2.1), (14.3, 13.6), (1.7, 13.6)], closed=True)
        ln(8, 6.0, 8, 9.9)
        R.circle(*P(8, 11.9), 0.75 * k, fill=color)
    elif name == "close":
        ln(4.2, 4.2, 11.8, 11.8)
        ln(11.8, 4.2, 4.2, 11.8)
    elif name == "chevron":
        pl([(5.2, 6.6), (8, 9.4), (10.8, 6.6)])
    elif name == "sliders":
        ln(1.9, 4.4, 14.1, 4.4)
        ln(1.9, 8.0, 14.1, 8.0)
        ln(1.9, 11.6, 14.1, 11.6)
        R.circle(*P(5.4, 4.4), 1.55 * k, fill=color)
        R.circle(*P(10.6, 8.0), 1.55 * k, fill=color)
        R.circle(*P(6.8, 11.6), 1.55 * k, fill=color)
    elif name == "archive":
        pl([(2.0, 5.9), (14.0, 5.9), (14.0, 13.6), (2.0, 13.6)], closed=True)
        pl([(1.5, 2.4), (14.5, 2.4), (14.5, 5.0), (1.5, 5.0)], closed=True)
        ln(6.0, 9.2, 10.0, 9.2)
    elif name == "bell":
        R.path([("M", *P(8, 2.6)),
                ("C", *P(5.5, 2.6), *P(4.3, 4.4), *P(4.3, 6.7)),
                ("L", *P(4.3, 9.3)), ("L", *P(2.9, 11.4)),
                ("L", *P(13.1, 11.4)), ("L", *P(11.7, 9.3)),
                ("L", *P(11.7, 6.7)),
                ("C", *P(11.7, 4.4), *P(10.5, 2.6), *P(8, 2.6)),
                ("Z",)], color=color, w=w * k)
        ln(6.6, 13.0, 9.4, 13.0)
    elif name == "database":
        R.path([("M", *P(2.8, 4.0)),
                ("C", *P(2.8, 2.9), *P(5.1, 2.0), *P(8, 2.0)),
                ("C", *P(10.9, 2.0), *P(13.2, 2.9), *P(13.2, 4.0)),
                ("C", *P(13.2, 5.1), *P(10.9, 6.0), *P(8, 6.0)),
                ("C", *P(5.1, 6.0), *P(2.8, 5.1), *P(2.8, 4.0)),
                ("Z",)], color=color, w=w * k)
        ln(2.8, 4.0, 2.8, 12.0)
        ln(13.2, 4.0, 13.2, 12.0)
        R.path([("M", *P(2.8, 8.0)),
                ("C", *P(2.8, 9.1), *P(5.1, 10.0), *P(8, 10.0)),
                ("C", *P(10.9, 10.0), *P(13.2, 9.1), *P(13.2, 8.0))],
               color=color, w=w * k)
        R.path([("M", *P(2.8, 12.0)),
                ("C", *P(2.8, 13.1), *P(5.1, 14.0), *P(8, 14.0)),
                ("C", *P(10.9, 14.0), *P(13.2, 13.1), *P(13.2, 12.0))],
               color=color, w=w * k)
    elif name == "info":
        ci(8, 8, 6.3)
        ln(8, 7.3, 8, 11.4)
        R.circle(*P(8, 4.9), 0.8 * k, fill=color)
    elif name == "arrow_right":
        ln(3.2, 8, 12.4, 8)
        pl([(9.2, 4.8), (12.6, 8), (9.2, 11.2)])
    elif name == "dot":
        R.circle(*P(8, 8), 2.6 * k, fill=color)
    elif name == "minus":
        ln(3.5, 8, 12.5, 8)
    elif name == "square":
        pl([(3.4, 3.4), (12.6, 3.4), (12.6, 12.6), (3.4, 12.6)], closed=True)
    else:
        raise ValueError(name)


# ---------------------------------------------------------------------------
# LOOM mark
# ---------------------------------------------------------------------------
def draw_squircle(ox, oy, size, light=True):
    r = size * 0.22
    if light:
        R.rect(ox, oy, size, size, r=r,
               grad=dict(angle=135, stops=[(0, "#ffffff"), (0.55, "#f7f8fa"),
                                           (1, "#e9ecf2")]))
        R.rect(ox + 0.5, oy + 0.5, size - 1, size - 1, r=r - 0.5,
               stroke="#0f11150f", sw=1)
    else:
        R.rect(ox, oy, size, size, r=r,
               grad=dict(angle=135, stops=[(0, "#2e3034"), (0.55, "#232427"),
                                           (1, "#1a1b1e")]))
        R.rect(ox + 0.5, oy + 0.5, size - 1, size - 1, r=r - 0.5,
               stroke="#ffffff14", sw=1)


def draw_mark(ox, oy, size, ink=INK, thread=THREAD, bead_fill=None, end="check"):
    """Full mark in 1024 design space, scaled into `size` at (ox,oy)."""
    sc = size / 1024.0

    def X(v):
        return ox + v * sc

    def Y(v):
        return oy + v * sc

    def L(v):
        return v * sc

    soft = dict(dx=0, dy=10, blur=20, color="#0f11151c")
    bars = [(292, 251, 440), (332, 375, 360), (372, 499, 280)]
    for x, y, w in bars:
        R.rect(X(x), Y(y), L(w), L(70), r=L(35), fill=ink, shadow=soft)
    # warp thread: woven through all layers, ending in the knot bead
    R.path([("M", X(512), Y(251)),
            ("C", X(336), Y(412), X(688), Y(560), X(512), Y(661))],
           color=thread, w=L(50))
    R.rect(X(414), Y(375), L(196), L(70), r=0, fill=ink)  # under bar2
    bead = bead_fill if bead_fill is not None else ink
    R.rect(X(446), Y(641), L(132), L(132), r=L(36), fill=bead, shadow=soft)
    if end == "check":
        R.path([("M", X(478), Y(704)), ("L", X(504), Y(730)),
                ("L", X(552), Y(682))], color=thread, w=L(18))
    else:  # knot dot
        R.circle(X(512), Y(707), L(25), fill=thread)


def draw_mark_simple(cx, cy, size, color, end="check"):
    """16-grid simplified mark for small sizes."""
    k = size / 16.0

    def X(v):
        return cx - size / 2 + v * k

    def Y(v):
        return cy - size / 2 + v * k

    R.rect(X(2.2), Y(3.2), 11.6 * k, 2.1 * k, r=1.05 * k, fill=color)
    R.rect(X(3.4), Y(8.2), 9.2 * k, 2.1 * k, r=1.05 * k, fill=color)
    if end == "knot":
        R.path([("M", X(8), Y(1.2)),
                ("C", X(5.1), Y(4.2), X(10.9), Y(7.4), X(8), Y(11.6))],
               color=color, w=2.1 * k)
        R.circle(X(8), Y(13.9), 1.7 * k, fill=color)
    else:
        R.path([("M", X(8), Y(1.2)),
                ("C", X(5.1), Y(4.2), X(10.9), Y(7.6), X(7.4), Y(12.0))],
               color=color, w=2.1 * k)
        R.poly([(X(6.2), Y(13.2)), (X(7.6), Y(14.6)), (X(10.4), Y(11.9))],
               color=color, w=1.7 * k)


def draw_app_icon(ox, oy, size, light=True, simple=False, shadow=False,
                  end="check"):
    if shadow:
        R.rect(ox, oy + size * 0.05, size, size, r=size * 0.22,
               shadow=dict(dx=0, dy=size * 0.14, blur=size * 0.22,
                           color=SHADOW_ICON))
    draw_squircle(ox, oy, size, light=light)
    if simple or size < 120:
        draw_mark_simple(ox + size / 2, oy + size / 2, size * 0.60,
                         INK if light else "#f9fafb", end=end)
    else:
        draw_mark(ox, oy, size,
                  ink=INK if light else "#f9fafb",
                  thread=THREAD,
                  bead_fill=None if light else "#f9fafb",
                  end=end)


def draw_mono_icon(x, y, size, color):
    draw_mark_simple(x + size / 2, y + size / 2, size, color)


# ---------------------------------------------------------------------------
# sheet: background / chrome
# ---------------------------------------------------------------------------
def background():
    R.rect(0, 0, 1840, 1080, fill=CANVAS)
    R.glow(200, 60, 620, GLOW1, opacity=0.55)
    R.glow(1560, 980, 640, GLOW2, opacity=0.45)
    R.glow(1050, 260, 500, GLOW3, opacity=0.30)


def header():
    draw_app_icon(64, 40, 62, shadow=True)
    R.text(144, 74, "LOOM", size=27, color=TEXT, weight=300, family=BRAND,
           ls=6.5)
    cap(146, 97, "DSH PLUGIN GUARD  ·  插件更新守护", color=TEXT3, size=10,
        ls=1.7)
    # working title chip
    R.rect(1626, 56, 150, 26, r=8, fill=L1, stroke=BORDER2, sw=1)
    R.text(1701, 73, "UI 参考稿 v0.2", size=10.5, color=TEXT2, anchor="middle")
    cap(1580, 98,
        ("深色" if THEME_NAME == "dark" else "浅色") + "主题 · 对齐 DSH 原生界面",
        color=TEXT4, size=9.5, ls=0.4)
    # thin accent hairline
    R.rect(64, 120, 1712, 1, fill=BORDER1)


def footer():
    cap(64, 1034, "LOOM · 命名已定（织机）· 矢量参考稿 v0.2", color=TEXT4,
        size=9.5, ls=0.5)
    R.text(1776, 1034,
           "DESIGNED FOR DEEPSEEK HARNESS  ·  颜色 / 圆角 / 字体取自 DSH 0.2.2-rc.1",
           size=8.6, color=TEXT4, anchor="end", ls=0.7)


# ---------------------------------------------------------------------------
# flyout
# ---------------------------------------------------------------------------
def flyout(ox, oy):
    W, H = 372, 600
    R.rect(ox, oy, W, H, r=20,
           grad=dict(angle=160, stops=PANEL_STOPS),
           stroke=BORDER2, sw=1,
           shadow=dict(dx=0, dy=22, blur=54, color=SHADOW_PANEL))
    # header
    draw_app_icon(ox + 18, oy + 16, 30, simple=True, shadow=True)
    R.text(ox + 58, oy + 38, "LOOM", size=14, color=TEXT, weight=500,
           family=BRAND, ls=2.8)
    cap(ox + 59, oy + 54, "DSH 插件守护", color=TEXT4, size=9.5, ls=0.8)
    R.circle(ox + 319, oy + 32, 3.2, fill=GREEN)
    R.text(ox + 329, oy + 36, "正常", size=10.5, color=TEXT2)
    hline(ox + 18, ox + 354, oy + 70, BORDER1)

    # status card
    cx0, cy0, cw, ch = ox + 18, oy + 84, W - 36, 124
    R.rect(cx0, cy0, cw, ch, r=14, fill=L2, stroke=BORDER2, sw=1)
    R.text(cx0 + 16, cy0 + 24, "DeepSeek Harness", size=12.5, color=TEXT,
           weight=500)
    R.text(cx0 + cw - 16, cy0 + 24, "0.2.2-rc.1", size=10.5, color=TEXT3,
           anchor="end")
    icon("shield", cx0 + 25, cy0 + 46, 13, GREEN, w=1.5)
    R.text(cx0 + 38, cy0 + 50, "3 个插件已保护", size=11.5, color=TEXT2)
    R.text(cx0 + 16, cy0 + 74, "上次快照 10:46 · 自动检查开启", size=10,
           color=TEXT4)
    hline(cx0 + 16, cx0 + cw - 16, cy0 + 88, BORDER1)
    R.text(cx0 + 16, cy0 + 108, "备份 42.6 MB", size=9.5, color=TEXT4)
    R.text(cx0 + cw - 16, cy0 + 108, "每 30 分钟检查", size=9.5, color=TEXT4,
           anchor="end")

    # primary action
    R.rect(ox + 18, oy + 224, W - 36, 44, r=12, fill=WHITE,
           shadow=dict(dx=0, dy=6, blur=16, color=SHADOW_BUTTON))
    icon("camera", ox + 130, oy + 246, 15, ON_DARK, w=1.5)
    R.text(ox + 148, oy + 250, "立即快照", size=13, color=ON_DARK, weight=500)

    # secondary actions
    sw_ = (W - 36 - 10) / 2
    for i, (label, icn) in enumerate((("检查兼容性", "shield"),
                                      ("打开备份目录", "folder"))):
        bx = ox + 18 + i * (sw_ + 10)
        R.rect(bx, oy + 280, sw_, 38, r=10, fill=L3, stroke=BORDER2, sw=1)
        icon(icn, bx + sw_ / 2 - 32, oy + 299, 13, TEXT2, w=1.4)
        R.text(bx + sw_ / 2 - 14, oy + 303, label, size=11, color=TEXT)

    # activity
    R.text(ox + 18, oy + 348, "最近活动", size=10, color=TEXT4, ls=0.6)
    R.text(ox + 354, oy + 348, "全部", size=10, color=LINK, anchor="end")
    rows = [("check", GREEN, "快照完成 · 3 个插件", "10:46"),
            ("shield", TEXT3, "兼容检查通过", "10:46"),
            ("arrow_up", LINK, "自动升级 wallpaper-engine", "10:32")]
    for i, (icn, col, title, time) in enumerate(rows):
        ry = oy + 352 + i * 46
        R.circle(ox + 33, ry + 20, 13, fill=L1, stroke=BORDER1, sw=1)
        icon(icn, ox + 33, ry + 20, 12, col, w=1.5)
        R.text(ox + 56, ry + 24, title, size=11.5, color=TEXT)
        R.text(ox + 354, ry + 24, time, size=9.5, color=TEXT4, anchor="end")
    hline(ox + 18, ox + 354, oy + 504, BORDER1)
    R.text(ox + 18, oy + 530, "查看报告", size=10.5, color=LINK)
    for i, icn in enumerate(("folder", "gear", "power")):
        bxx = ox + 282 + i * 36
        icon(icn, bxx, oy + 526, 14, TEXT3, w=1.3)
    cap(ox + 18, oy + 566, "LOOM 0.1.0 · 后台守护已开启", color=TEXT4, size=9)


# ---------------------------------------------------------------------------
# toasts
# ---------------------------------------------------------------------------
def toasts(ox, oy):
    items = [
        ("已备份 3 个插件", "DSH 0.2.2-rc.1 · 10:46", "check", GREEN),
        ("发现新版本 0.3.0", "2 个插件将在退出 DSH 后自动升级", "arrow_up", LINK),
    ]
    for i, (title, body, icn, col) in enumerate(items):
        ty = oy + i * 90
        R.rect(ox, ty, 372, 78, r=14, fill=TOAST, stroke=BORDER2, sw=1,
               shadow=dict(dx=0, dy=14, blur=34, color=SHADOW_TOAST))
        R.rect(ox + 14, ty + 15, 28, 28, r=8, fill=L3)
        icon(icn, ox + 28, ty + 29, 13, col, w=1.7)
        R.text(ox + 54, ty + 32, title, size=12, color=TOAST_FG, weight=500)
        R.text(ox + 54, ty + 52, body, size=10, color=TOAST_BODY)
        icon("close", ox + 349, ty + 24, 9, "#cfd3d6", w=1.2)


# ---------------------------------------------------------------------------
# settings window
# ---------------------------------------------------------------------------
def toggle(x, y, on=True):
    w, h = 44, 26
    if on:
        R.rect(x, y, w, h, r=h / 2, fill=WHITE)
        R.circle(x + w - h / 2, y + h / 2, h / 2 - 3, fill=ON_DARK)
    else:
        R.rect(x, y, w, h, r=h / 2, fill=L3, stroke=BORDER3, sw=1)
        R.circle(x + h / 2, y + h / 2, h / 2 - 3, fill=TEXT3)


def settings(ox, oy):
    W, H = 824, 612
    R.rect(ox, oy, W, H, r=18, fill=L1, stroke=BORDER2, sw=1,
           shadow=dict(dx=0, dy=24, blur=56, color=SHADOW_WINDOW))
    # sidebar
    R.rect(ox, oy, 196, H, r=18, fill=SIDEBAR)
    R.rect(ox + 100, oy, 96, H, r=0, fill=SIDEBAR)
    R.line(ox + 196, oy + 52, ox + 196, oy + H, color=BORDER1, w=1)

    draw_app_icon(ox + 18, oy + 15, 22, simple=True, shadow=True)
    R.text(ox + 48, oy + 32, "LOOM", size=12.5, color=TEXT, weight=500,
           family=BRAND, ls=2.2)
    for i, icn in enumerate(("minus", "square", "close")):
        icon(icn, ox + W - 68 + i * 24, oy + 24, 9, TEXT4, w=1.2)

    # nav
    nav = [("sliders", "通用", False), ("archive", "备份与还原", False),
           ("refresh", "更新与兼容", True), ("bell", "通知", False),
           ("database", "存储", False), ("info", "关于", False)]
    for i, (icn, label, active) in enumerate(nav):
        ny = oy + 64 + i * 34
        if active:
            R.rect(ox + 12, ny, 172, 32, r=8, fill=L3)
        col = TEXT if active else TEXT3
        icon(icn, ox + 30, ny + 16, 13, col, w=1.4)
        R.text(ox + 48, ny + 20, label, size=11.5, color=col,
               weight=500 if active else 400)

    # sidebar bottom status
    R.rect(ox + 12, oy + 516, 172, 76, r=12, fill=L1, stroke=BORDER1,
           sw=1)
    R.circle(ox + 26, oy + 536, 3.5, fill=GREEN)
    R.text(ox + 36, oy + 540, "守护运行中", size=10.5, color=TEXT)
    R.text(ox + 24, oy + 562, "DSH 0.2.2-rc.1", size=9.5, color=TEXT4)
    R.text(ox + 24, oy + 578, "下次检查 00:59", size=9.5, color=TEXT4)

    # content header
    R.text(ox + 224, oy + 92, "更新与兼容", size=17, color=TEXT, weight=500)
    R.text(ox + 224, oy + 116, "决定 DSH 更新后如何对待旧插件",
           size=10.5, color=TEXT4)

    # card 1 — compatibility policy
    c1x, c1y, c1w, c1h = ox + 224, oy + 136, 576, 168
    R.rect(c1x, c1y, c1w, c1h, r=14, fill=L2, stroke=BORDER2, sw=1)
    R.text(c1x + 20, c1y + 26, "兼容性处理", size=12.5, color=TEXT, weight=500)
    opts = [("自动升级到兼容版本（推荐）", True),
            ("仅提示，不自动处理", False),
            ("保留旧版并风险豁免（需确认）", False)]
    for i, (label, sel) in enumerate(opts):
        yy = c1y + 50 + i * 24
        if sel:
            R.circle(c1x + 26, yy, 7, stroke=WHITE, sw=1.6)
            R.circle(c1x + 26, yy, 3.4, fill=WHITE)
        else:
            R.circle(c1x + 26, yy, 7, stroke=TEXT4, sw=1.4)
        R.text(c1x + 42, yy + 4, label, size=11.5,
               color=TEXT if sel else TEXT2)
    R.text(c1x + 20, c1y + 146,
           "需联网；找不到兼容版本时会提示你手动处理", size=9.5,
           color=TEXT4)

    # card 2 — auto restore
    c2y = oy + 318
    R.rect(c1x, c2y, c1w, 84, r=14, fill=L2, stroke=BORDER2, sw=1)
    R.text(c1x + 20, c2y + 26, "更新后自动恢复", size=12.5, color=TEXT,
           weight=500)
    R.text(c1x + 20, c2y + 50,
           "重启后补齐插件启用项与配置，必要时重启一次生效", size=9.5,
           color=TEXT4)
    toggle(c1x + c1w - 64, c2y + 22, on=True)

    # card 3 — snapshot & storage
    c3y = oy + 416
    R.rect(c1x, c3y, c1w, 140, r=14, fill=L2, stroke=BORDER2, sw=1)
    R.text(c1x + 20, c3y + 26, "快照与存储", size=12.5, color=TEXT, weight=500)
    R.text(c1x + 20, c3y + 54, "保留份数", size=11, color=TEXT2)
    R.rect(c1x + c1w - 116, c3y + 38, 96, 28, r=8, fill=L3, stroke=BORDER2,
           sw=1)
    R.text(c1x + c1w - 104, c3y + 56, "5 份", size=10.5, color=TEXT)
    icon("chevron", c1x + c1w - 32, c3y + 52, 9, TEXT3, w=1.3)
    R.text(c1x + 20, c3y + 88, "包含离线插件包", size=11, color=TEXT2)
    toggle(c1x + c1w - 64, c3y + 74, on=True)
    R.text(c1x + 20, c3y + 118, "占用 42.6 MB", size=9.5, color=TEXT4)
    R.text(c1x + c1w - 20, c3y + 118, "清理…", size=9.5, color=LINK,
           anchor="end")
    R.text(c1x + c1w, oy + 588, "更改会自动保存", size=9.5, color=TEXT4,
           anchor="end")


# ---------------------------------------------------------------------------
# icon card / compatibility card / notes / tokens
# ---------------------------------------------------------------------------
def icon_card(ox, oy):
    R.rect(ox, oy, 444, 340, r=16, fill=L2, stroke=BORDER2, sw=1)
    cap(ox + 20, oy + 24, "应用图标", color=TEXT4, size=10, ls=0.8)
    R.text(ox + 424, oy + 24, "1024 × 1024", size=9, color=TEXT4,
           anchor="end")
    draw_app_icon(ox + 20, oy + 40, 168, shadow=True)
    R.text(ox + 212, oy + 116, "LOOM", size=36, color=TEXT, weight=300,
           family=BRAND, ls=8.0)
    cap(ox + 214, oy + 138, "DSH PLUGIN GUARD", color=TEXT3, size=9.5, ls=2.6)
    R.text(ox + 214, oy + 168, "更新再多，插件都在。", size=11, color=TEXT2)
    hline(ox + 20, ox + 424, oy + 228, BORDER1)
    cap(ox + 20, oy + 252, "图标变体 · 托盘 16 / 24 / 32", color=TEXT4,
        size=9, ls=0.6)
    R.text(ox + 424, oy + 252, "主蓝 #4176e6", size=9, color=TEXT4,
           anchor="end")
    variants = [
        (ox + 20, "light-check", "浅色"),
        (ox + 84, "dark-check", "深色"),
        (ox + 148, "mono", "16 px"),
    ]
    for x, kind, label in variants:
        yy = oy + 264
        if kind == "mono":
            R.rect(x, yy, 48, 48, r=12, fill=L3, stroke=BORDER2, sw=1)
            draw_mono_icon(x + 16, yy + 16, 16, TEXT)
        else:
            light = kind.startswith("light")
            draw_app_icon(x, yy, 48, light=light, simple=True, end="check")
        R.text(x + 24, yy + 64, label, size=8.5, color=TEXT4, anchor="middle")


def compat_card(ox, oy):
    R.rect(ox, oy, 444, 258, r=16, fill=L2, stroke=BORDER2, sw=1)
    cap(ox + 20, oy + 24, "更新后的兼容性提示", color=TEXT4, size=10, ls=0.6)
    dx, dy, dw, dh = ox + 20, oy + 38, 404, 190
    R.rect(dx, dy, dw, dh, r=14, fill=DIALOG_BG, stroke=BORDER2, sw=1,
           shadow=dict(dx=0, dy=12, blur=28, color=SHADOW_DIALOG))
    icon("alert", dx + 28, dy + 26, 17, AMBER, w=1.6)
    R.text(dx + 46, dy + 31, "2 个插件将自动升级", size=12.5, color=TEXT,
           weight=500)
    cap(dx + 46, dy + 49, "DSH 0.3.0 生效后", color=TEXT4, size=9)
    rows = [("dsh-plugin-wallpaper-engine", "1.4.2", "1.5.0"),
            ("dshmarket", "1.73.19", "1.74.2")]
    for i, (name, old, new) in enumerate(rows):
        ry = dy + 78 + i * 22
        R.circle(dx + 26, ry - 3, 2.4, fill=LINK)
        R.text(dx + 36, ry, name, size=10, color=TEXT2, family="mono")
        R.text(dx + 36 + 210, ry, f"{old} → {new}", size=10, color=TEXT3,
               family="mono")
    R.text(dx + 22, dy + 140,
           "找不到兼容版本时才需要你确认风险豁免", size=9, color=TEXT4)
    bw2, bw1 = 84, 68
    R.rect(dx + dw - 22 - bw2 - 8 - bw1, dy + dh - 46, bw1, 28, r=8,
           fill=L3, stroke=BORDER2, sw=1)
    R.text(dx + dw - 22 - bw2 - 8 - bw1 + bw1 / 2, dy + dh - 27, "稍后",
           size=10, color=TEXT2, anchor="middle")
    R.rect(dx + dw - 22 - bw2, dy + dh - 46, bw2, 28, r=8, fill=WHITE)
    R.text(dx + dw - 22 - bw2 / 2, dy + dh - 27, "立即处理", size=10,
           color=ON_DARK, weight=500, anchor="middle")


def notes_card(ox, oy):
    R.rect(ox, oy, 444, 190, r=16, fill=L2, stroke=BORDER2, sw=1)
    cap(ox + 20, oy + 24, "设计要点", color=TEXT4, size=10, ls=0.8)
    items = [
        "深色优先：颜色直接取 DSH 深色别名 token",
        "主按钮白底深字，强调色只用于状态与链接",
        "面板与菜单半透明 + 1px 细描边，贴近 DSH",
        "层级：状态 → 一键动作 → 最近活动",
        "通知与面板同色系，不打断当前使用",
    ]
    for i, s in enumerate(items):
        yy = oy + 50 + i * 26
        R.circle(ox + 24, yy - 3.5, 2.2, fill=LINK)
        R.text(ox + 34, yy, s, size=10, color=TEXT2)


def token_strip(ox, oy):
    R.rect(ox, oy, 824, 190, r=16, fill=L2, stroke=BORDER2, sw=1)
    cap(ox + 20, oy + 24, TOKEN_TITLE, color=TEXT4, size=10, ls=0.6)
    R.text(ox + 804, oy + 24, "深色 / 浅色", size=9, color=TEXT4, anchor="end")

    colors = TOKEN_COLORS
    R.text(ox + 20, oy + 52, "颜色", size=9, color=TEXT4)
    for i, (hexv, label) in enumerate(colors):
        x = ox + 62 + i * 74
        R.rect(x, oy + 38, 24, 24, r=7, fill=hexv, stroke=BORDER2, sw=1)
        R.text(x + 12, oy + 76, label, size=8.5, color=TEXT4, anchor="middle")

    cols = [
        (ox + 20, "字体", "Montserrat 300 / 500（品牌）",
         "Segoe UI · 微软雅黑 400 / 500（界面）"),
        (ox + 300, "圆角", "24 · 18 · 14 · 12 · 10 px",
         "原生弹窗 / 窗口 / 卡片 / 按钮"),
        (ox + 580, "字号", "16 · 14 · 13 · 12 · 11 px",
         "标题 / 正文 / 说明 / 注释"),
    ]
    for x, label, v1, v2 in cols:
        R.text(x, oy + 112, label, size=9, color=TEXT4)
        R.text(x, oy + 136, v1, size=11, color=TEXT2)
        R.text(x, oy + 158, v2, size=9, color=TEXT4)


# ---------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------
def render_sheet(theme):
    apply_theme(theme)
    R.SCENE.clear()
    background()
    header()

    cap(64, 142, "托盘弹层 · TRAY FLYOUT", color=TEXT3, size=9.5, ls=1.2)
    flyout(64, 154)

    cap(64, 792, "通知 · WINDOWS TOAST", color=TEXT3, size=9.5, ls=1.2)
    toasts(64, 804)

    cap(476, 142, "设置窗口 · SETTINGS", color=TEXT3, size=9.5, ls=1.2)
    settings(476, 154)

    cap(1332, 142, "图标 · APP ICON", color=TEXT3, size=9.5, ls=1.2)
    icon_card(1332, 154)

    cap(1332, 506, "兼容性提示 · 更新后", color=TEXT3, size=9.5, ls=1.2)
    compat_card(1332, 518)

    cap(476, 792, "设计 TOKEN", color=TEXT3, size=9.5, ls=1.2)
    token_strip(476, 804)

    cap(1332, 792, "设计要点", color=TEXT3, size=9.5, ls=1.2)
    notes_card(1332, 804)

    footer()
    svg = R.to_svg(1840, 1080, bg=CANVAS)
    with open(os.path.join(HERE, f"LOOM-UI-{theme}.svg"), "w",
              encoding="utf-8") as f:
        f.write(svg)
    png = R.render_png(1840, 1080, bg=CANVAS)
    png = png.resize((1840, 1080), 1)
    png.save(os.path.join(HERE, f"LOOM-UI-{theme}.png"))
    print(f"sheet {theme} written")


def render_icon_variant(shade, end):
    R.SCENE.clear()
    R.rect(0, 0, 1024, 1024, fill="#00000000")
    draw_app_icon(32, 32, 960, light=(shade == "light"), end=end)
    svg = R.to_svg(1024, 1024, embed_fonts=False)
    base = f"LOOM-icon-{shade}-{end}"
    with open(os.path.join(HERE, base + ".svg"), "w",
              encoding="utf-8") as f:
        f.write(svg)
    png = R.render_png(1024, 1024).resize((1024, 1024), 1)
    png.save(os.path.join(HERE, base + ".png"))
    return png


def render_icon_sheet():
    """Final icon direction: light + dark, check-mark ending."""
    R.SCENE.clear()
    W, H = 1120, 570
    R.rect(0, 0, W, H, fill="#141416")
    R.glow(180, 100, 380, "#1e3357", opacity=0.5)
    R.glow(980, 520, 420, "#153049", opacity=0.4)
    R.text(40, 66, "LOOM", size=24, color="#f9fafb", weight=300,
           family=BRAND, ls=5.5)
    R.text(40, 90,
           "定稿图标 · 对钩收尾 · 浅底 / 深底 · DSH 主蓝 #4176e6",
           size=10, color="#adb2b8", ls=0.8)
    cards = [
        ("light", "浅底 · 对钩", "桌面 / 浅色场景 · 白底深纹"),
        ("dark", "深底 · 对钩", "深色任务栏 / 深色背景 · 深底浅纹"),
    ]
    for i, (shade, label, sub) in enumerate(cards):
        x = 40 + i * 550
        y = 108
        R.rect(x, y, 510, 332, r=18, fill="#232324", stroke="#ffffff1f", sw=1)
        draw_app_icon(x + 145, y + 26, 220, light=(shade == "light"),
                      end="check", shadow=True)
        R.text(x + 255, y + 300, label, size=13, color="#f9fafb",
               anchor="middle")
        R.text(x + 255, y + 322, sub, size=9, color="#81858c",
               anchor="middle")
    # tray sizes
    R.text(40, 476, "托盘尺寸 · 16 / 24 / 32 px（简化标记）", size=9.5,
           color="#81858c", ls=0.8)
    for vi, shade in enumerate(("light", "dark")):
        for j, sz in enumerate((16, 24, 32)):
            cx = 40 + vi * 550 + 64 + j * 76
            chip = "#353638" if shade == "light" else "#f1f3f5"
            R.rect(cx - 30, 490, 60, 56, r=14, fill=chip)
            draw_app_icon(cx - sz / 2, 518 - sz / 2, sz,
                          light=(shade == "light"), end="check")
            R.text(cx, 560, f"{sz}", size=7.5, color="#81858c",
                   anchor="middle")
    R.text(1080, 560, "圆点结版本未采用，存档于 alternatives/", size=8.5,
           color="#81858c", anchor="end")
    svg = R.to_svg(W, H, bg="#141416")
    with open(os.path.join(HERE, "LOOM-icons.svg"), "w",
              encoding="utf-8") as f:
        f.write(svg)
    png = R.render_png(W, H, bg="#141416")
    png = png.resize((W, H), 1)
    png.save(os.path.join(HERE, "LOOM-icons.png"))
    print("icon sheet written")


if __name__ == "__main__":
    for shade in ("light", "dark"):
        render_icon_variant(shade, "check")
    render_icon_sheet()
    render_sheet("dark")
    render_sheet("light")
