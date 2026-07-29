"""歌词文字渲染（PIL）：超采样 + 图层缓存 + 逐字扫过 + 切行动画。

输出预乘 alpha 的 BGRA 位图，供 UpdateLayeredWindow 使用。
视觉风格对标 Win11 原生：纯白文字、逐字为「唱过变亮/未唱变暗」的深浅变化、
按钮悬停有浅色圆角背景、歌词切换时旧行上滑淡出、新行下滑淡入。
"""
import os
import winreg

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

_FONT_CACHE: dict = {}
_FONT_PATH_CACHE: dict = {}

SS = 3                    # 超采样倍数
CONTROL_CELL = 26         # 播放控制按钮单格宽度（像素）
PENDING_ALPHA = 140       # 未唱到歌词的 alpha（深浅变化中的「暗」）
HOVER_FILL = (255, 255, 255, 22)  # 按钮悬停底色

# Segoe MDL2 Assets 图标字体的按钮字形（Win10/11 原生）
_GLYPH_PREV = ""    # Previous
_GLYPH_NEXT = ""    # Next
_GLYPH_PLAY = ""    # Play
_GLYPH_PAUSE = ""   # Pause
_MDL2_FONT = "Segoe MDL2 Assets"


# ---- 字体 ----

def _font_path(family: str, bold: bool) -> str:
    """按字体名从注册表找字体文件，找不到回退微软雅黑/黑体。"""
    key = (family.lower(), bold)
    if key in _FONT_PATH_CACHE:
        return _FONT_PATH_CACHE[key]
    fonts_dir = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "Fonts")
    path = None
    for root in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        try:
            with winreg.OpenKey(root, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts") as reg:
                i = 0
                while True:
                    try:
                        name, value, _ = winreg.EnumValue(reg, i)
                    except OSError:
                        break
                    i += 1
                    name_l = name.lower()
                    if family.lower() in name_l and ("bold" in name_l) == bold:
                        path = value if os.path.isabs(value) else os.path.join(fonts_dir, value)
                        break
        except OSError:
            continue
        if path:
            break
    if not path:  # 回退
        for fallback in ("msyhbd.ttc" if bold else "msyh.ttc", "msyh.ttc", "simhei.ttf"):
            candidate = os.path.join(fonts_dir, fallback)
            if os.path.exists(candidate):
                path = candidate
                break
    _FONT_PATH_CACHE[key] = path
    return path


def get_font(family: str, size_px: int, bold: bool = True) -> ImageFont.FreeTypeFont:
    key = (family.lower(), size_px, bold)
    if key not in _FONT_CACHE:
        _FONT_CACHE[key] = ImageFont.truetype(_font_path(family, bold), size_px)
    return _FONT_CACHE[key]


def _mdl2_font(size_px: int):
    """加载 Segoe MDL2 Assets 图标字体，失败返回 None。"""
    try:
        return get_font(_MDL2_FONT, size_px, bold=False)
    except Exception:
        pass
    try:
        path = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "Fonts", "segmdl2.ttf")
        return ImageFont.truetype(path, size_px)
    except Exception:
        return None


def pt_to_px(pt: float) -> int:
    """Qt 语义的 pt 转像素（96 DPI）。"""
    return max(1, round(pt * 96 / 72))


def _hex_to_rgba(color: str, alpha: int = 255) -> tuple[int, int, int, int]:
    color = color.lstrip("#")
    return int(color[0:2], 16), int(color[2:4], 16), int(color[4:6], 16), alpha


def _fit_font(family: str, text: str, size_px: int, max_width: int, draw: ImageDraw.ImageDraw):
    """文字超宽时逐级缩小字号直到放得下。"""
    while size_px > 8:
        font = get_font(family, size_px)
        if draw.textlength(text, font=font) <= max_width:
            return font, size_px
        size_px -= 1
    return get_font(family, 8), 8


# ---- 布局助手（overlay 命中测试也用）----

def controls_origin(has_cover_zone: bool, height: int) -> int:
    """控制按钮区起点 x（封面区在按钮左侧，宽度=高度）。"""
    return height if has_cover_zone else 0


def controls_width() -> int:
    return CONTROL_CELL * 3


# ---- 逐字 ----

def _karaoke_boundary_x(draw, font, karaoke, x0: float) -> float:
    """逐字高亮的右边界 x：已唱完的字全亮，正在唱的字按进度比例亮。"""
    words, elapsed = karaoke
    passed = ""
    current = ""
    frac = 0.0
    for wstart, wdur, ch in words:
        if elapsed >= wstart + wdur:
            passed += ch
        elif elapsed >= wstart:
            current = ch
            frac = min(1.0, (elapsed - wstart) / max(1, wdur))
            break
        else:
            break
    boundary = x0 + draw.textlength(passed, font=font)
    if current:
        boundary += draw.textlength(current, font=font) * frac
    return boundary


# ---- 图层缓存与动画 ----

# key -> (base_small, accent_small|None, font_big, x0_big)
_LAYER_CACHE: dict = {}
_MEASURE_DRAW = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
_PREV_BASE: Image.Image | None = None  # 上一行底图（切行动画用）


def karaoke_boundary_px(karaoke) -> int | None:
    """逐字高亮边界的像素位置（最终尺寸），用于按像素粒度决定是否重绘。"""
    if not karaoke or not _LAYER_CACHE:
        return None
    _, _, font_big, x0_big, _ = next(iter(_LAYER_CACHE.values()))
    if font_big is None:
        return None
    return int(_karaoke_boundary_x(_MEASURE_DRAW, font_big, karaoke, x0_big) / SS)


def _with_alpha(img: Image.Image, p: float) -> Image.Image:
    if p <= 0:
        return Image.new("RGBA", img.size, (0, 0, 0, 0))
    if p >= 1:
        return img
    out = img.copy()
    out.putalpha(out.getchannel("A").point(lambda v: int(v * p)))
    return out


def _composite_offset(dst: Image.Image, src: Image.Image, dx: int, dy: int) -> None:
    """把 src 以偏移合成到 dst 上（自动裁剪出界部分）。"""
    x0, y0 = max(0, dx), max(0, dy)
    x1, y1 = min(dst.width, dx + src.width), min(dst.height, dy + src.height)
    if x1 <= x0 or y1 <= y0:
        return
    dst.alpha_composite(src.crop((x0 - dx, y0 - dy, x1 - dx, y1 - dy)), (x0, y0))


def render_lyric(
    original: str,
    translation: str,
    width: int,
    height: int,
    *,
    family: str,
    orig_pt: float,
    trans_pt: float,
    color: str,
    trans_color: str,
    shadow: bool,
    karaoke=None,
    controls_playing: bool | None = None,
    buttons_fade: float = 1.0,
    has_cover_zone: bool = False,
    cover: Image.Image | None = None,
    cover_key: str = "",
    hover_zone: int = -1,
    anim_p: float = 1.0,
) -> bytes:
    """渲染一帧歌词，返回预乘 alpha 的 BGRA 字节串。

    - karaoke: (words, elapsed_ms) 逐字扫过；唱过为纯白，未唱为暗色
    - controls_playing: 非 None 时绘制播放控制按钮（值为播放状态），
      buttons_fade 控制按钮整体透明度（悬停浮现动画，按钮叠加在歌词区左侧）
    - has_cover_zone / cover: 封面区（宽度=高度）与封面图
    - hover_zone: 悬停的按钮序号（-1 无），Win11 风格浅色圆角背景
    - anim_p: 行切换动画进度 0~1（旧行上滑淡出、新行下滑淡入）
    """
    global _PREV_BASE
    key = (original, translation, width, height, family, orig_pt, trans_pt,
           color, trans_color, shadow, karaoke is not None, controls_playing,
           has_cover_zone, cover_key)
    cached = _LAYER_CACHE.get(key)
    if cached is None:
        if _LAYER_CACHE:  # 换行：保存旧底图用于切换动画
            _PREV_BASE = next(iter(_LAYER_CACHE.values()))[0]
        cached = _build_layers(
            original, translation, width, height,
            family=family, orig_pt=orig_pt, trans_pt=trans_pt,
            color=color, trans_color=trans_color, shadow=shadow,
            with_accent=karaoke is not None,
            controls_playing=controls_playing,
            has_cover_zone=has_cover_zone, cover=cover,
        )
        _LAYER_CACHE.clear()  # 只留当前行，防止无限增长
        _LAYER_CACHE[key] = cached

    base, accent, font_big, x0_big, buttons = cached

    # 行切换动画帧
    if anim_p < 1.0 and _PREV_BASE is not None:
        frame = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        slide = max(2, height // 4)
        _composite_offset(frame, _with_alpha(_PREV_BASE, 1.0 - anim_p), 0, int(-slide * anim_p))
        _composite_offset(frame, _with_alpha(base, anim_p), 0, int(slide * (1.0 - anim_p)))
    else:
        need_compose = ((accent is not None and karaoke is not None)
                        or (buttons is not None and buttons_fade > 0))
        frame = base.copy() if need_compose else base

        # 逐字扫过（唱过纯白 / 未唱暗色）
        if accent is not None and karaoke is not None:
            boundary = _karaoke_boundary_x(_MEASURE_DRAW, font_big, karaoke, x0_big) / SS
            if boundary > 0:
                frame.alpha_composite(accent.crop((0, 0, min(width, int(boundary)), height)))

        # 悬停浮现的播放控制按钮（叠加在歌词区左侧）
        if buttons is not None and buttons_fade > 0:
            frame = frame.copy() if frame is base else frame
            _composite_offset(frame, _with_alpha(buttons, buttons_fade),
                              controls_origin(has_cover_zone, height), 0)

    # 按钮悬停背景
    if hover_zone >= 0 and controls_playing is not None and buttons_fade > 0.5:
        frame = frame.copy() if frame is base else frame
        d = ImageDraw.Draw(frame)
        x0 = controls_origin(has_cover_zone, height) + hover_zone * CONTROL_CELL
        d.rounded_rectangle(
            [x0 + 2, 6, x0 + CONTROL_CELL - 2, height - 6],
            radius=5, fill=HOVER_FILL,
        )
    return _to_premultiplied_bgra(frame)


def _build_layers(
    original: str,
    translation: str,
    width: int,
    height: int,
    *,
    family: str,
    orig_pt: float,
    trans_pt: float,
    color: str,
    trans_color: str,
    shadow: bool,
    with_accent: bool,
    controls_playing: bool | None,
    has_cover_zone: bool,
    cover: Image.Image | None,
):
    """构建静态图层：最终尺寸的底图 + 逐字高亮图 + 按钮层，附大字体的测量信息。"""
    W, H = width * SS, height * SS
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    # 文字区起点 = 封面区（按钮改为悬停浮现的叠加层，不占文字宽度）
    left_off = H if has_cover_zone else 0
    margin = 12 * SS
    max_w = max(1, W - left_off - margin * 2)

    lines = []  # (text, font, size_px, rgba)
    if original:
        font, size_px = _fit_font(family, original, pt_to_px(orig_pt) * SS, max_w, draw)
        # 逐字模式下原文先画暗色，唱过部分由高亮层覆盖为纯白
        alpha = PENDING_ALPHA if with_accent else 255
        lines.append((original, font, size_px, _hex_to_rgba(color, alpha)))
    if translation:
        font, size_px = _fit_font(family, translation, pt_to_px(trans_pt) * SS, max_w, draw)
        lines.append((translation, font, size_px, _hex_to_rgba(trans_color)))

    if not lines and controls_playing is None and cover is None:
        empty = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        return empty, None, None, 0.0, None

    # 整体垂直居中（按字形实际包围盒居中，而不是按字号——否则视觉上偏下）
    line_gap = 2 * SS
    metrics = []  # (bbox_top, line_h)
    for text, font, size_px, _ in lines:
        bbox = draw.textbbox((0, 0), text, font=font)
        metrics.append((bbox[1], max(1, bbox[3] - bbox[1])))
    total_h = sum(h for _, h in metrics) + line_gap * (len(lines) - 1) if lines else 0
    y = max(0, (H - total_h) // 2)

    orig_font_big = lines[0][1] if lines else None
    orig_x0_big = 0.0

    def draw_lines(target_draw: ImageDraw.ImageDraw, fill_override=None) -> None:
        nonlocal orig_x0_big
        ly = y
        for (text, font, size_px, rgba), (bbox_top, h) in zip(lines, metrics):
            tw = target_draw.textlength(text, font=font)
            x0 = left_off + (W - left_off - tw) / 2
            if fill_override is None and text is lines[0][0]:
                orig_x0_big = x0  # 记录原文行起始 x（逐字边界用）
            stroke_w = max(1, size_px // 16) if shadow else 0
            target_draw.text(
                (x0, ly - bbox_top), text, font=font,
                fill=fill_override or rgba,
                stroke_width=stroke_w,
                stroke_fill=(0, 0, 0, 220) if fill_override is None else fill_override,
            )
            ly += h + line_gap

    # 柔光层：同一批文字画黑色、模糊后垫在下面
    if shadow:
        glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        draw_lines(ImageDraw.Draw(glow), fill_override=(0, 0, 0, 160))
        glow = glow.filter(ImageFilter.GaussianBlur(2.0 * SS))
        img = Image.alpha_composite(img, glow)
        draw = ImageDraw.Draw(img)

    draw_lines(draw)  # 主文字

    # 专辑封面（圆角方形，位于封面区中央）
    if cover is not None:
        side = H - 8 * SS
        pic = cover.convert("RGBA").resize((side, side), Image.LANCZOS)
        mask = Image.new("L", (side, side), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, side - 1, side - 1], radius=4 * SS, fill=255)
        img.paste(pic, (4 * SS, 4 * SS), mask)
        draw = ImageDraw.Draw(img)

    # 播放控制按钮：独立图层（悬停浮现时按透明度叠加，叠加在歌词区左侧）
    buttons_small = None
    if controls_playing is not None:
        buttons = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        bdraw = ImageDraw.Draw(buttons)
        cell = CONTROL_CELL * SS
        glyphs = [_GLYPH_PREV, _GLYPH_PAUSE if controls_playing else _GLYPH_PLAY, _GLYPH_NEXT]
        bfont = _mdl2_font(pt_to_px(11) * SS)
        for i, glyph in enumerate(glyphs):
            f, text_g = bfont, glyph
            if f is None:  # 图标字体缺失时退化为 ASCII
                f = get_font(family, pt_to_px(11) * SS)
                text_g = ["<<", "||" if controls_playing else ">", ">>"][i]
            tw = bdraw.textlength(text_g, font=f)
            bbox = bdraw.textbbox((0, 0), text_g, font=f)
            bdraw.text(
                (i * cell + (cell - tw) / 2 - bbox[0], (H - (bbox[3] - bbox[1])) / 2 - bbox[1]),
                text_g, font=f, fill=_hex_to_rgba(color),
                stroke_width=max(1, pt_to_px(11) * SS // 16) if shadow else 0,
                stroke_fill=(0, 0, 0, 220),
            )
        buttons_small = buttons.resize(
            (CONTROL_CELL * 3, height), Image.LANCZOS,
            box=(0, 0, CONTROL_CELL * 3 * SS, H),
        )

    # 逐字高亮层：原文行整行纯白，逐帧按边界裁剪合成（未唱部分保持暗色）
    accent_small = None
    if with_accent and original and lines:
        accent = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        adraw = ImageDraw.Draw(accent)
        text, font, size_px, _ = lines[0]
        bbox_top = metrics[0][0]
        stroke_w = max(1, size_px // 16) if shadow else 0
        adraw.text(
            (orig_x0_big, y - bbox_top), text, font=font,
            fill=_hex_to_rgba(color),
            stroke_width=stroke_w,
            stroke_fill=(0, 0, 0, 220),
        )
        accent_small = accent.resize((width, height), Image.LANCZOS)

    base_small = img.resize((width, height), Image.LANCZOS)
    return base_small, accent_small, orig_font_big, orig_x0_big, buttons_small


def _to_premultiplied_bgra(img: Image.Image) -> bytes:
    """RGBA（直 alpha）→ BGRA（预乘 alpha），供 AC_SRC_ALPHA 使用。"""
    r, g, b, a = img.split()
    # ImageChops.multiply 按 C 速度做 通道×alpha/255
    r = ImageChops.multiply(r, a)
    g = ImageChops.multiply(g, a)
    b = ImageChops.multiply(b, a)
    return Image.merge("RGBA", (b, g, r, a)).tobytes()
