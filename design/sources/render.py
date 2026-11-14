#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tiny scene renderer: one scene description -> SVG (vector) + PNG (PIL preview).

Only the primitives this design needs are implemented. Coordinates are in
design pixels (1x); the PNG renderer supersamples by SCALE for crisp previews.
"""
import base64
import math
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT_WIN = r"C:\Windows\Fonts"

SCENE = []
DEFS = []  # svg defs
_ids = {"g": 0, "f": 0, "r": 0, "clip": 0}


# --------------------------------------------------------------------------
# scene primitives
# --------------------------------------------------------------------------
def rect(x, y, w, h, r=0, fill=None, stroke=None, sw=1, shadow=None,
         grad=None, opacity=1.0):
    SCENE.append(dict(k="rect", x=x, y=y, w=w, h=h, r=r, fill=fill,
                      stroke=stroke, sw=sw, shadow=shadow, grad=grad,
                      opacity=opacity))


def text(x, y, s, size=12, color="#f9fafb", weight=400, family="ui",
         anchor="start", ls=0.0, opacity=1.0):
    """x,y is the baseline start (SVG semantics)."""
    SCENE.append(dict(k="text", x=x, y=y, s=s, size=size, color=color,
                      weight=weight, family=family, anchor=anchor, ls=ls,
                      opacity=opacity))


def line(x1, y1, x2, y2, color, w=1, opacity=1.0, cap="round"):
    SCENE.append(dict(k="line", x1=x1, y1=y1, x2=x2, y2=y2, color=color,
                      w=w, opacity=opacity, cap=cap))


def poly(pts, color=None, w=0, fill=None, closed=False, opacity=1.0,
         cap="round", join="round"):
    SCENE.append(dict(k="poly", pts=list(pts), color=color, w=w, fill=fill,
                      closed=closed, opacity=opacity, cap=cap, join=join))


def path(cmds, color=None, w=0, fill=None, opacity=1.0, cap="round",
         join="round"):
    SCENE.append(dict(k="path", cmds=list(cmds), color=color, w=w, fill=fill,
                      opacity=opacity, cap=cap, join=join))


def circle(cx, cy, r, fill=None, stroke=None, sw=1, opacity=1.0, grad=None,
           shadow=None):
    SCENE.append(dict(k="circle", cx=cx, cy=cy, r=r, fill=fill, stroke=stroke,
                      sw=sw, opacity=opacity, grad=grad, shadow=shadow))


def glow(cx, cy, r, color, opacity=1.0):
    SCENE.append(dict(k="glow", cx=cx, cy=cy, r=r, color=color,
                      opacity=opacity))


# --------------------------------------------------------------------------
# colors
# --------------------------------------------------------------------------
def parse_color(c):
    """-> (r,g,b,a) with alpha 0..1. Accepts #rgb #rrggbb #rrggbbaa and tuples."""
    if isinstance(c, tuple):
        if len(c) == 4:
            return c
        if len(c) == 3:
            return (c[0], c[1], c[2], 1.0)
        raise ValueError(c)
    c = c.strip().lstrip("#")
    if len(c) == 3:
        c = "".join(ch * 2 for ch in c)
    if len(c) == 6:
        c += "ff"
    return (int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16),
            int(c[6:8], 16) / 255.0)


def css_rgba(c):
    r, g, b, a = parse_color(c)
    return f"rgba({r},{g},{b},{a:.4f})"


def lerp(a, b, t):
    return a + (b - a) * t


def rgb_str(c):
    r, g, b, _ = parse_color(c)
    return f"#{r:02x}{g:02x}{b:02x}"


# --------------------------------------------------------------------------
# SVG emitter
# --------------------------------------------------------------------------
def _svg_filter_shadow(sh):
    key = ("shadow", sh["dx"], sh["dy"], sh["blur"], str(sh["color"]))
    for name, k in _filter_cache.items():
        if k == key:
            return name
    _ids["f"] += 1
    name = f"sh{_ids['f']}"
    color = css_rgba(sh["color"])
    DEFS.append(
        f'<filter id="{name}" x="-40%" y="-40%" width="180%" height="200%">'
        f'<feDropShadow dx="{sh["dx"]}" dy="{sh["dy"]}" '
        f'stdDeviation="{sh["blur"]}" flood-color="{color}"/></filter>')
    _filter_cache[name] = key
    return name


def _svg_gradient(grad):
    key = (grad["angle"], str(grad["stops"]))
    for name, k in _grad_cache.items():
        if k == key:
            return name
    _ids["g"] += 1
    name = f"g{_ids['g']}"
    ang = math.radians(grad["angle"])
    x1, y1 = 0.5 - 0.5 * math.cos(ang), 0.5 - 0.5 * math.sin(ang)
    x2, y2 = 0.5 + 0.5 * math.cos(ang), 0.5 + 0.5 * math.sin(ang)
    stops = []
    for off, c in grad["stops"]:
        r, g, b, a = parse_color(c)
        stops.append(f'<stop offset="{off}" stop-color="rgb({r},{g},{b})" '
                     f'stop-opacity="{a:.4f}"/>')
    DEFS.append(f'<linearGradient id="{name}" x1="{x1:.4f}" y1="{y1:.4f}" '
                f'x2="{x2:.4f}" y2="{y2:.4f}">{"".join(stops)}'
                f'</linearGradient>')
    _grad_cache[name] = key
    return name


def _svg_radial(cx, cy, r, color, opacity):
    _ids["r"] += 1
    name = f"r{_ids['r']}"
    rr, gg, bb, aa = parse_color(color)
    DEFS.append(
        f'<radialGradient id="{name}" cx="50%" cy="50%" r="50%">'
        f'<stop offset="0" stop-color="rgb({rr},{gg},{bb})" '
        f'stop-opacity="{aa:.4f}"/>'
        f'<stop offset="1" stop-color="rgb({rr},{gg},{bb})" stop-opacity="0"/>'
        f"</radialGradient>")
    return name


def _x(s):
    return (str(s).replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace('"', "&quot;"))


def _svg_path_d(cmds):
    out = []
    for c in cmds:
        if c[0] == "M":
            out.append(f"M {c[1]:.3f} {c[2]:.3f}")
        elif c[0] == "L":
            out.append(f"L {c[1]:.3f} {c[2]:.3f}")
        elif c[0] == "C":
            out.append(f"C {c[1]:.3f} {c[2]:.3f} {c[3]:.3f} {c[4]:.3f} "
                       f"{c[5]:.3f} {c[6]:.3f}")
        elif c[0] == "Z":
            out.append("Z")
    return " ".join(out)


_FONT_STACKS = {
    "ui": "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Microsoft YaHei', sans-serif",
    "brand": "Montserrat, 'Segoe UI', 'Microsoft YaHei', sans-serif",
    "mono": "Consolas, 'Cascadia Mono', monospace",
}


def to_svg(width, height, bg=None, embed_fonts=True):
    _filter_cache.clear()
    _grad_cache.clear()
    DEFS.clear()
    body = []
    if embed_fonts:
        css = []
        for fam, fname, wgt in (("Montserrat", "montserrat-light.woff2", 300),
                                ("Montserrat", "montserrat-regular.woff2", 400),
                                ("Montserrat", "montserrat-medium.woff2", 500)):
            fp = os.path.join(HERE, "fonts", fname)
            if not os.path.exists(fp):
                continue
            b64 = base64.b64encode(open(fp, "rb").read()).decode()
            css.append(f"@font-face{{font-family:'{fam}';font-style:normal;"
                       f"font-weight:{wgt};src:url(data:font/woff2;base64,"
                       f"{b64}) format('woff2');}}")
        if css:
            DEFS.append("<style>" + "".join(css) + "</style>")

    for d in SCENE:
        k = d["k"]
        op = d.get("opacity", 1.0)
        opattr = f' opacity="{op:.4f}"' if op < 1 else ""
        if k == "rect":
            fill = "none"
            attrs = []
            if d["shadow"]:
                attrs.append(f'filter="url(#{_svg_filter_shadow(d["shadow"])})"')
            if d["grad"]:
                fill = f'url(#{_svg_gradient(d["grad"])})'
            elif d["fill"]:
                fill = rgb_str(d["fill"])
                r, g, b, a = parse_color(d["fill"])
                if a < 1:
                    attrs.append(f'fill-opacity="{a:.4f}"')
            stroke = d["stroke"]
            if stroke:
                attrs.append(f'stroke="{rgb_str(stroke)}"')
                attrs.append(f'stroke-width="{d["sw"]}"')
                r, g, b, a = parse_color(stroke)
                if a < 1:
                    attrs.append(f'stroke-opacity="{a:.4f}"')
            if d["r"]:
                attrs.append(f'rx="{d["r"]}"')
            body.append(
                f'<rect x="{d["x"]}" y="{d["y"]}" width="{d["w"]}" '
                f'height="{d["h"]}" fill="{fill}" {" ".join(attrs)}{opattr}/>')
        elif k == "circle":
            attrs = []
            fill = "none"
            if d["shadow"]:
                attrs.append(f'filter="url(#{_svg_filter_shadow(d["shadow"])})"')
            if d["grad"]:
                fill = f'url(#{_svg_gradient(d["grad"])})'
            elif d["fill"]:
                fill = rgb_str(d["fill"])
                r, g, b, a = parse_color(d["fill"])
                if a < 1:
                    attrs.append(f'fill-opacity="{a:.4f}"')
            if d["stroke"]:
                attrs.append(f'stroke="{rgb_str(d["stroke"])}"')
                attrs.append(f'stroke-width="{d["sw"]}"')
                r, g, b, a = parse_color(d["stroke"])
                if a < 1:
                    attrs.append(f'stroke-opacity="{a:.4f}"')
            body.append(f'<circle cx="{d["cx"]}" cy="{d["cy"]}" r="{d["r"]}" '
                        f'fill="{fill}" {" ".join(attrs)}{opattr}/>')
        elif k == "line":
            r, g, b, a = parse_color(d["color"])
            body.append(
                f'<line x1="{d["x1"]}" y1="{d["y1"]}" x2="{d["x2"]}" '
                f'y2="{d["y2"]}" stroke="rgb({r},{g},{b})" '
                f'stroke-opacity="{a:.4f}" stroke-width="{d["w"]}" '
                f'stroke-linecap="{d["cap"]}"{opattr}/>')
        elif k == "poly":
            pts = " ".join(f"{p[0]:.2f},{p[1]:.2f}" for p in d["pts"])
            attrs = []
            fill = "none"
            if d["fill"]:
                fill = rgb_str(d["fill"])
            if d["color"]:
                r, g, b, a = parse_color(d["color"])
                attrs.append(f'stroke="rgb({r},{g},{b})" stroke-opacity="{a:.4f}"')
                attrs.append(f'stroke-width="{d["w"]}"')
                attrs.append(f'stroke-linecap="{d["cap"]}" '
                             f'stroke-linejoin="{d["join"]}"')
            shape = "polygon" if d["closed"] else "polyline"
            body.append(f'<{shape} points="{pts}" fill="{fill}" '
                        f'{" ".join(attrs)}{opattr}/>')
        elif k == "path":
            attrs = []
            fill = "none"
            if d["fill"]:
                fill = rgb_str(d["fill"])
            if d["color"]:
                r, g, b, a = parse_color(d["color"])
                attrs.append(f'stroke="rgb({r},{g},{b})" stroke-opacity="{a:.4f}"')
                attrs.append(f'stroke-width="{d["w"]}" '
                             f'stroke-linecap="{d["cap"]}" '
                             f'stroke-linejoin="{d["join"]}"')
            body.append(f'<path d="{_svg_path_d(d["cmds"])}" fill="{fill}" '
                        f'{" ".join(attrs)}{opattr}/>')
        elif k == "glow":
            name = _svg_radial(d["cx"], d["cy"], d["r"], d["color"],
                               d.get("opacity", 1.0))
            body.append(
                f'<circle cx="{d["cx"]}" cy="{d["cy"]}" r="{d["r"]}" '
                f'fill="url(#{name})" opacity="{op:.4f}"/>')
        elif k == "text":
            r, g, b, a = parse_color(d["color"])
            fam = _FONT_STACKS.get(d["family"], _FONT_STACKS["ui"])
            attrs = [f'font-family="{fam}"', f'font-size="{d["size"]}"',
                     f'font-weight="{d["weight"]}"']
            if d["ls"]:
                attrs.append(f'letter-spacing="{d["ls"]}"')
            if a < 1:
                attrs.append(f'fill-opacity="{a:.4f}"')
            body.append(
                f'<text x="{d["x"]}" y="{d["y"]}" fill="rgb({r},{g},{b})" '
                f'text-anchor="{d["anchor"]}" {" ".join(attrs)}{opattr}>'
                f'{_x(d["s"])}</text>')

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" '
           f'height="{height}" viewBox="0 0 {width} {height}">']
    if bg:
        out.append(f'<rect width="{width}" height="{height}" '
                   f'fill="{css_rgba(bg)}"/>')
    if DEFS:
        out.append("<defs>" + "".join(DEFS) + "</defs>")
    out.append('<g>' + "".join(body) + "</g></svg>")
    return "".join(out)


# --------------------------------------------------------------------------
# PNG renderer (PIL)
# --------------------------------------------------------------------------
_FONT_CACHE = {}
_S = 2  # supersampling


def _font(family, weight, size):
    key = (family, weight, size)
    if key in _FONT_CACHE:
        return _FONT_CACHE[key]
    if family == "brand":
        cand = {300: ["segoeuil.ttf", "segoeui.ttf"],
                400: ["segoeui.ttf"],
                500: ["seguisb.ttf", "segoeuisb.ttf", "segoeui.ttf"],
                600: ["seguisb.ttf", "segoeuisb.ttf", "segoeuib.ttf"]}
    else:
        cand = {300: ["segoeuil.ttf", "segoeui.ttf"],
                400: ["segoeui.ttf"],
                500: ["seguisb.ttf", "segoeuisb.ttf", "segoeui.ttf"],
                600: ["seguisb.ttf", "segoeuisb.ttf", "segoeuib.ttf"],
                700: ["segoeuib.ttf"]}
    names = cand.get(weight, cand[400])
    fp = None
    for nm in names:
        p = os.path.join(FONT_WIN, nm)
        if os.path.exists(p):
            fp = p
            break
    if fp is None:
        fp = os.path.join(FONT_WIN, "segoeui.ttf")
    f = ImageFont.truetype(fp, int(round(size * _S)))
    _FONT_CACHE[key] = f
    return f


def _is_cjk(s):
    return any("\u4e00" <= ch <= "\u9fff" or "\u3000" <= ch <= "\u303f"
               or "\uff00" <= ch <= "\uffef" for ch in s)


def _font_for_text(family, weight, size, s):
    if _is_cjk(s):
        w = 300 if weight <= 300 else (700 if weight >= 600 else 400)
        fp = os.path.join(FONT_WIN, {300: "msyhl.ttc", 400: "msyh.ttc",
                                     700: "msyhbd.ttc"}[w])
        key = ("cjk", w, size)
        if key not in _FONT_CACHE:
            try:
                _FONT_CACHE[key] = ImageFont.truetype(fp, int(round(size * _S)))
            except OSError:
                _FONT_CACHE[key] = ImageFont.truetype(
                    os.path.join(FONT_WIN, "msyh.ttc"), int(round(size * _S)))
        return _FONT_CACHE[key], True
    return _font(family, weight, size), False


def _sc(v):
    return int(round(v * _S))


def _flatten_path(cmds):
    pts = []
    cur = None
    for c in cmds:
        if c[0] == "M":
            cur = (c[1], c[2])
            pts.append(cur)
        elif c[0] == "L":
            cur = (c[1], c[2])
            pts.append(cur)
        elif c[0] == "C":
            p0 = cur
            p1 = (c[1], c[2])
            p2 = (c[3], c[4])
            p3 = (c[5], c[6])
            for i in range(1, 25):
                t = i / 24.0
                mt = 1 - t
                x = (mt ** 3 * p0[0] + 3 * mt ** 2 * t * p1[0]
                     + 3 * mt * t ** 2 * p2[0] + t ** 3 * p3[0])
                y = (mt ** 3 * p0[1] + 3 * mt ** 2 * t * p1[1]
                     + 3 * mt * t ** 2 * p2[1] + t ** 3 * p3[1])
                pts.append((x, y))
            cur = p3
        elif c[0] == "Z":
            pass
    return pts


def _grad_image(w, h, grad):
    ang = math.radians(grad["angle"])
    dx, dy = math.cos(ang), math.sin(ang)
    ys, xs = np.mgrid[0:h, 0:w]
    proj = (xs - w / 2) * dx + (ys - h / 2) * dy
    span = max(abs(proj.min()), abs(proj.max())) or 1
    t = (proj / (2 * span) + 0.5).clip(0, 1)
    stops = grad["stops"]
    colors = [(off, np.array(parse_color(c)[:3], dtype=np.float32))
              for off, c in stops]
    img = np.zeros((h, w, 3), dtype=np.float32)
    if len(colors) == 1:
        img[:] = colors[0][1]
    else:
        for i in range(len(colors) - 1):
            o0, c0 = colors[i]
            o1, c1 = colors[i + 1]
            mask = (t >= o0) & (t <= o1)
            tt = ((t[mask] - o0) / max(o1 - o0, 1e-6))[:, None]
            img[mask] = c0 + (c1 - c0) * tt
        img[t < colors[0][0]] = colors[0][1]
        img[t > colors[-1][0]] = colors[-1][1]
    return img.clip(0, 255).astype(np.uint8)


def _pil(c):
    r, g, b, a = parse_color(c)
    return (r, g, b, int(round(a * 255)))


def render_png(width, height, bg=None):
    W, H = _sc(width), _sc(height)
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    if bg:
        im.alpha_composite(Image.new("RGBA", (W, H), _pil(bg)))

    for d in SCENE:
        k = d["k"]
        op = d.get("opacity", 1.0)

        def overlay():
            return Image.new("RGBA", (W, H), (0, 0, 0, 0))

        if k == "glow":
            r = d["r"] * _S
            cx, cy = d["cx"] * _S, d["cy"] * _S
            size = int(r * 2)
            yy, xx = np.mgrid[0:size, 0:size]
            dist = np.sqrt((xx - r) ** 2 + (yy - r) ** 2) / r
            alpha = ((1 - dist).clip(0, 1)) ** 1.6 * op
            rr, gg, bb, _ = parse_color(d["color"])
            lay = np.zeros((size, size, 4), dtype=np.uint8)
            lay[..., 0], lay[..., 1], lay[..., 2] = rr, gg, bb
            lay[..., 3] = (alpha * 255).astype(np.uint8)
            im.alpha_composite(Image.fromarray(lay, "RGBA"),
                               (int(cx - r), int(cy - r)))
            continue

        lay = overlay()
        dr = ImageDraw.Draw(lay)
        if k == "rect":
            box = [_sc(d["x"]), _sc(d["y"]),
                   _sc(d["x"] + d["w"]), _sc(d["y"] + d["h"])]
            rad = _sc(d["r"])
            if d["shadow"]:
                sh = d["shadow"]
                sl = Image.new("RGBA", (W, H), (0, 0, 0, 0))
                sd = ImageDraw.Draw(sl)
                sd.rounded_rectangle(
                    [box[0] + _sc(sh["dx"]), box[1] + _sc(sh["dy"]),
                     box[2] + _sc(sh["dx"]), box[3] + _sc(sh["dy"])],
                    rad, fill=_pil(sh["color"]))
                sl = sl.filter(ImageFilter.GaussianBlur(_sc(sh["blur"])))
                im.alpha_composite(sl)
            if d["grad"]:
                gw, gh = int(_sc(d["w"])), int(_sc(d["h"]))
                gimg = Image.fromarray(_grad_image(gw, gh, d["grad"]), "RGB")
                mask = Image.new("L", (gw, gh), 0)
                ImageDraw.Draw(mask).rounded_rectangle(
                    [0, 0, gw - 1, gh - 1], rad, fill=255)
                gimg = gimg.convert("RGBA")
                gimg.putalpha(mask)
                im.alpha_composite(gimg, (_sc(d["x"]), _sc(d["y"])))
            elif d["fill"]:
                dr.rounded_rectangle(box, rad, fill=_pil(d["fill"]))
            if d["stroke"]:
                dr.rounded_rectangle(box, rad, outline=_pil(d["stroke"]),
                                     width=max(1, _sc(d["sw"])))
            if op < 1:
                lay.putalpha(lay.getchannel("A").point(lambda v: int(v * op)))
            im.alpha_composite(lay)
        elif k == "circle":
            cx, cy, r = _sc(d["cx"]), _sc(d["cy"]), _sc(d["r"])
            box = [cx - r, cy - r, cx + r, cy + r]
            if d["shadow"]:
                sh = d["shadow"]
                sl = Image.new("RGBA", (W, H), (0, 0, 0, 0))
                ImageDraw.Draw(sl).ellipse(
                    [box[0] + _sc(sh["dx"]), box[1] + _sc(sh["dy"]),
                     box[2] + _sc(sh["dx"]), box[3] + _sc(sh["dy"])],
                    fill=_pil(sh["color"]))
                sl = sl.filter(ImageFilter.GaussianBlur(_sc(sh["blur"])))
                im.alpha_composite(sl)
            if d["fill"]:
                dr.ellipse(box, fill=_pil(d["fill"]))
            if d["stroke"]:
                dr.ellipse(box, outline=_pil(d["stroke"]),
                           width=max(1, _sc(d["sw"])))
            if op < 1:
                lay.putalpha(lay.getchannel("A").point(lambda v: int(v * op)))
            im.alpha_composite(lay)
        elif k == "line":
            dr.line([_sc(d["x1"]), _sc(d["y1"]), _sc(d["x2"]), _sc(d["y2"])],
                    fill=_pil(d["color"]), width=max(1, _sc(d["w"])))
            im.alpha_composite(lay)
        elif k == "poly":
            pts = [(_sc(p[0]), _sc(p[1])) for p in d["pts"]]
            if d["fill"]:
                dr.polygon(pts, fill=_pil(d["fill"]))
            if d["color"]:
                dr.line(pts + ([pts[0]] if d["closed"] else []),
                        fill=_pil(d["color"]), width=max(1, _sc(d["w"])),
                        joint="curve")
            im.alpha_composite(lay)
        elif k == "path":
            pts = [(_sc(p[0]), _sc(p[1])) for p in _flatten_path(d["cmds"])]
            if d["fill"]:
                dr.polygon(pts, fill=_pil(d["fill"]))
            if d["color"]:
                w = max(1, _sc(d["w"]))
                dr.line(pts, fill=_pil(d["color"]), width=w, joint="curve")
                for p in (pts[0], pts[-1]):
                    dr.ellipse([p[0] - w / 2, p[1] - w / 2, p[0] + w / 2,
                                p[1] + w / 2], fill=_pil(d["color"]))
            im.alpha_composite(lay)
        elif k == "text":
            f, _ = _font_for_text(d["family"], d["weight"], d["size"], d["s"])
            col = _pil(d["color"])
            if op < 1:
                col = (col[0], col[1], col[2], int(col[3] * op))
            if not d["ls"]:
                dr.text((_sc(d["x"]), _sc(d["y"])), d["s"], font=f, fill=col,
                        anchor={"start": "ls", "middle": "ms",
                                "end": "rs"}[d["anchor"]])
            else:
                total = 0.0
                widths = []
                for ch in d["s"]:
                    cw = f.getlength(ch)
                    widths.append(cw)
                    total += cw + _sc(d["ls"])
                total -= _sc(d["ls"])
                x = _sc(d["x"])
                if d["anchor"] == "middle":
                    x -= total / 2
                elif d["anchor"] == "end":
                    x -= total
                for ch, cw in zip(d["s"], widths):
                    dr.text((x, _sc(d["y"])), ch, font=f, fill=col, anchor="ls")
                    x += cw + _sc(d["ls"])
            im.alpha_composite(lay)

    return im


_filter_cache = {}
_grad_cache = {}

