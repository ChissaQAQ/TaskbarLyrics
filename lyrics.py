"""歌词获取与 LRC 解析。

来源优先级：网易云（含译文 tlyric，覆盖最好）→ QQ 音乐 → LRCLIB。
（网易云未登录搜索会过滤部分版权歌曲如周杰伦，此时自动回退后续来源。）
"""
import json
import re

import requests

# [分:秒]、[分:秒.毫秒]、[分:秒:厘秒] 时间戳都支持，一行可能有多个
_LRC_TIME = re.compile(r"\[(\d+):(\d+)(?:[.:](\d+))?\]")

_NETEASE_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
    "Referer": "https://music.163.com",
}
_QQ_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
    "Referer": "https://y.qq.com/portal/player.html",
}

# 歌词行结构：[(毫秒, 原文, 译文|None)]
LyricLines = list[tuple[int, str, str | None]]


def _to_ms(minutes: str, seconds: str, frac: str | None) -> int:
    ms = int(minutes) * 60_000 + int(seconds) * 1000
    if frac:
        # 1 位按十分之一秒、2 位按厘秒、3 位按毫秒
        ms += int(frac) * (1000 // (10 ** len(frac)))
    return ms


def parse_lrc(lrc_text: str) -> list[tuple[int, str]]:
    """把 LRC 文本解析成 [(毫秒, 歌词)]，按时间排序，去掉空行和元信息行。"""
    lines: list[tuple[int, str]] = []
    for raw in lrc_text.splitlines():
        stamps = _LRC_TIME.findall(raw)
        if not stamps:
            continue
        text = _LRC_TIME.sub("", raw).strip()
        if not text:
            continue
        for minutes, seconds, frac in stamps:
            lines.append((_to_ms(minutes, seconds, frac), text))
    lines.sort(key=lambda x: x[0])
    return lines


def _merge_translation(
    lines: list[tuple[int, str]], trans: list[tuple[int, str]], tol_ms: int = 800
) -> LyricLines:
    """把译文按最近时间戳并到原文行，每条译文最多用一次。"""
    used = [False] * len(trans)
    merged: LyricLines = []
    for ms, text in lines:
        best = -1
        for i, (tms, ttext) in enumerate(trans):
            if used[i] or abs(tms - ms) > tol_ms:
                continue
            if best < 0 or abs(tms - ms) < abs(trans[best][0] - ms):
                best = i
        if best >= 0:
            used[best] = True
            merged.append((ms, text, trans[best][1]))
        else:
            merged.append((ms, text, None))
    return merged


def _fetch_netease(title: str, artist: str, duration_s: float,
                   second_line: str = "translation") -> LyricLines | None:
    resp = requests.get(
        "https://music.163.com/api/search/get/web",
        params={"s": f"{title} {artist}", "type": 1, "limit": 30},
        headers=_NETEASE_HEADERS,
        timeout=5,
    )
    songs = resp.json().get("result", {}).get("songs", [])
    exact = [s for s in songs if any(a.get("name") == artist for a in s.get("artists", []))]
    if not exact:
        return None
    chosen = exact[0]
    if duration_s:
        for s in exact:
            if abs(s.get("duration", 0) / 1000 - duration_s) < 3:
                chosen = s
                break
    resp = requests.get(
        "https://music.163.com/api/song/lyric",
        params={"id": chosen["id"], "lv": 1, "kv": 1, "tv": -1},
        headers=_NETEASE_HEADERS,
        timeout=5,
    )
    data = resp.json()
    lines = parse_lrc(data.get("lrc", {}).get("lyric", ""))
    if not lines:
        return None
    # 第二行：译文（tlyric）或罗马音（romalrc）
    trans = []
    if second_line == "translation":
        trans = parse_lrc(data.get("tlyric", {}).get("lyric", "") or "")
    elif second_line == "romaji":
        trans = parse_lrc(data.get("romalrc", {}).get("lyric", "") or "")
    return _merge_translation(lines, trans)


def _fetch_qq(title: str, artist: str, duration_s: float) -> LyricLines | None:
    resp = requests.get(
        "https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
        params={"w": f"{title} {artist}", "format": "json", "n": 5},
        headers=_QQ_HEADERS,
        timeout=5,
    )
    songs = resp.json().get("data", {}).get("song", {}).get("list", [])
    if not songs:
        return None

    def singer_names(song: dict) -> str:
        return " ".join(x.get("name", "") for x in song.get("singer", []))

    # 优先：歌手匹配且时长接近；其次：歌手匹配；兜底：第一条
    chosen = songs[0]
    artist_ok = [s for s in songs if artist and artist in singer_names(s)]
    if artist_ok:
        chosen = artist_ok[0]
        for s in artist_ok:
            if duration_s and abs(s.get("interval", 0) - duration_s) < 3:
                chosen = s
                break

    resp = requests.get(
        "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg",
        params={"songmid": chosen["songmid"], "format": "json", "nobase64": 1, "g_tk": 5381},
        headers=_QQ_HEADERS,
        timeout=5,
    )
    text = resp.text.strip()
    if text.startswith("MusicJsonCallback"):  # 兼容 JSONP 包裹
        text = text[text.find("(") + 1 : text.rfind(")")]
    data = json.loads(text)
    lines = parse_lrc(data.get("lyric", ""))
    if not lines:
        return None
    trans = parse_lrc(data.get("trans", "") or "")
    return _merge_translation(lines, trans)


def _fetch_lrclib(title: str, artist: str, duration_s: float) -> LyricLines | None:
    params = {"track_name": title, "artist_name": artist}
    if duration_s:
        params["duration"] = int(duration_s)
    resp = requests.get(
        "https://lrclib.net/api/get",
        params=params,
        headers={"User-Agent": "taskbar-lyrics v0.1"},
        timeout=5,
    )
    if resp.status_code != 200:
        return None
    lines = parse_lrc(resp.json().get("syncedLyrics") or "")
    if not lines:
        return None
    return _merge_translation(lines, [])


_SOURCES = (_fetch_netease, _fetch_qq, _fetch_lrclib)


def fetch_lyrics(
    title: str, artist: str, duration_s: float = 0,
    with_karaoke: bool = True, second_line: str = "translation",
) -> tuple[LyricLines | None, dict, str]:
    """依次尝试各歌词源，返回 ([(毫秒, 原文, 译文|None)], 逐字时间表, 命中的源名)。

    逐字表：{主行时间ms: [(字偏移ms, 字持续ms, 字)]}，来自酷狗 KRC；
    取不到或匹配不上时为空 dict（退化为逐行显示）。
    second_line: translation 译文 | romaji 罗马音（仅网易云源支持）| off 关闭。
    """
    lines = None
    source_name = ""
    for source in _SOURCES:
        try:
            if source is _fetch_netease:
                lines = source(title, artist, duration_s, second_line)
            else:
                lines = source(title, artist, duration_s)
        except Exception:
            lines = None  # 单个源网络异常不致命，换下一个
        if lines:
            source_name = source.__name__
            break
    if not lines:
        return None, {}, ""

    karaoke = {}
    if with_karaoke:
        try:
            krc = _fetch_kugou_karaoke(title, artist, duration_s)
            if krc:
                karaoke = _attach_karaoke(lines, krc)
        except Exception:
            karaoke = {}  # 逐字失败不影响逐行
    return lines, karaoke, source_name


# ---- 酷狗 KRC 逐字歌词 ----

_KRC_LINE = re.compile(r"\[(\d+),(\d+)\](.*)")
_KRC_WORD = re.compile(r"<(\d+),(\d+),\d+>([^<]*)")
_KRC_KEY = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
            0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69]


def _decode_krc(b64_content: str) -> str:
    """KRC 解码：base64 → 跳过 4 字节头 → XOR → zlib 解压。"""
    import base64
    import zlib

    raw = base64.b64decode(b64_content)
    if raw[:4] != b"krc1":
        raise ValueError("不是 KRC 格式")
    body = bytes(b ^ _KRC_KEY[i % 16] for i, b in enumerate(raw[4:]))
    return zlib.decompress(body).decode("utf-8")


def _parse_krc(text: str) -> list[tuple[int, str, list]]:
    """解析 KRC，返回 [(行开始ms, 纯文本, [(字偏移ms, 字持续ms, 字)])]。"""
    offset = 0
    m = re.search(r"\[offset:(-?\d+)\]", text)
    if m:
        offset = int(m.group(1))
    lines = []
    for raw in text.splitlines():
        m = _KRC_LINE.match(raw)
        if not m:
            continue
        start = int(m.group(1)) - offset
        words = [(int(a), int(b), ch) for a, b, ch in _KRC_WORD.findall(m.group(3))]
        plain = "".join(ch for _, _, ch in words).strip()
        if words and plain:
            lines.append((start, plain, words))
    lines.sort(key=lambda x: x[0])
    return lines


def _fetch_kugou_karaoke(title: str, artist: str, duration_s: float):
    resp = requests.get(
        "http://mobilecdn.kugou.com/api/v3/search/song",
        params={"format": "json", "keyword": f"{title} {artist}", "page": 1, "pagesize": 5},
        timeout=5,
    )
    songs = resp.json().get("data", {}).get("info", [])
    if not songs:
        return None
    # 优先：歌手匹配且时长接近
    chosen = songs[0]
    artist_ok = [
        s for s in songs
        if artist and (artist in (s.get("singername") or "") or (s.get("singername") or "") in artist)
    ]
    if artist_ok:
        chosen = artist_ok[0]
        for s in artist_ok:
            if duration_s and abs(s.get("duration", 0) - duration_s) < 3:
                chosen = s
                break

    resp = requests.get(
        "http://krcs.kugou.com/search",
        params={"ver": 1, "man": "yes", "client": "mobi",
                "keyword": chosen.get("songname") or title,
                "duration": int((chosen.get("duration") or duration_s) * 1000),
                "hash": chosen["hash"]},
        timeout=5,
    )
    candidates = resp.json().get("candidates", [])
    if not candidates:
        return None
    cand = candidates[0]
    resp = requests.get(
        "http://lyrics.kugou.com/download",
        params={"ver": 1, "client": "pc", "id": cand["id"],
                "accesskey": cand["accesskey"], "fmt": "krc", "charset": "utf8"},
        timeout=5,
    )
    return _parse_krc(_decode_krc(resp.json().get("content", "")))


def _attach_karaoke(main_lines: LyricLines, krc_lines: list, tol_ms: int = 1000) -> dict:
    """把 KRC 逐字时间挂到主歌词行上（时间接近 + 去空白后文本一致才认）。"""
    def norm(s: str) -> str:
        return re.sub(r"\s+", "", s)

    karaoke = {}
    used = set()
    for ms, text, _ in main_lines:
        best = -1
        for i, (kms, ktext, _) in enumerate(krc_lines):
            if i in used or abs(kms - ms) > tol_ms or norm(ktext) != norm(text):
                continue
            if best < 0 or abs(kms - ms) < abs(krc_lines[best][0] - ms):
                best = i
        if best >= 0:
            used.add(best)
            karaoke[ms] = krc_lines[best][2]
    return karaoke


def current_line(lines: LyricLines, position_ms: int) -> tuple[int, str, str]:
    """返回当前进度对应的 (行索引, 原文, 译文)；还没到第一句时索引为 -1。"""
    index = -1
    original = translation = ""
    for i, (ms, text, trans) in enumerate(lines):
        if ms > position_ms:
            break
        index, original, translation = i, text, trans or ""
    return index, original, translation


if __name__ == "__main__":
    # 命令行验证：python lyrics.py "歌名" "歌手"
    import sys

    title = sys.argv[1]
    artist = sys.argv[2] if len(sys.argv) > 2 else ""
    found, karaoke, source = fetch_lyrics(title, artist)
    if found is None:
        print("没找到歌词")
    else:
        has_trans = sum(1 for _, _, t in found if t)
        for ms, text, trans in found[:8]:
            suffix = f"  /  {trans}" if trans else ""
            mark = " [逐字]" if ms in karaoke else ""
            print(f"{ms / 1000:8.2f}s  {text}{suffix}{mark}")
        print(f"... 共 {len(found)} 行，其中 {has_trans} 行带译文，{len(karaoke)} 行带逐字（来源 {source}）")
