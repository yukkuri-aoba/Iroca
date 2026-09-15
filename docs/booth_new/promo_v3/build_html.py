# -*- coding: utf-8 -*-
"""BOOTH 販促画像 v3（BOOTH ネイティブ × 実物主義）のコンプ HTML を生成する。

v2（../promo/）との違い:
  - 全スライド同型の「中央揃え見出し + 灰色サブ + 灰色フッター」をやめ、1 枚 1 主張・左寄せ・
    枚ごとに構図を変える（写真主役 / 実 UI / 平置きテクスチャ / 2 連比較）。
  - 文字だけのスライドを廃止。使い方は実際の いろか ウィンドウ、仕様は書き出し PNG の実物と一緒に。
  - BOOTH の慣習（「無料」タグ、赤丸注釈、手書き文字の書き込み、キャラ主役）を取り込む。
  - 数字・名前は実物のもの（プリセット名・許容範囲・テクスチャ解像度）だけを書く。

生成物は公開リポに置かない: dev_safe/promo/booth_new_v3/booth-promo-v3.html
書体: Noto Sans JP（OS インストール済み VF）/ 注釈は Yomogi・Klee One（OFL、fonts/ に同梱・無ければ取得）
"""
import base64
import math
import os
import random
import urllib.request

D = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(D, "img")
REPO = os.path.dirname(os.path.dirname(os.path.dirname(D)))
OUT_DIR = os.path.join(REPO, "dev_safe", "promo", "booth_new_v3")
FONT_DIR = os.path.join(OUT_DIR, "fonts")
os.makedirs(FONT_DIR, exist_ok=True)

FONTS = {
    "Yomogi-Regular.ttf": "https://github.com/google/fonts/raw/main/ofl/yomogi/Yomogi-Regular.ttf",
    "KleeOne-SemiBold.ttf": "https://github.com/google/fonts/raw/main/ofl/kleeone/KleeOne-SemiBold.ttf",
}
for name, url in FONTS.items():
    p = os.path.join(FONT_DIR, name)
    if not os.path.exists(p):
        print("fetch", name)
        urllib.request.urlretrieve(url, p)


def b64(name):
    ext = name.rsplit(".", 1)[1].lower()
    mime = {"jpg": "image/jpeg", "jpeg": "image/jpeg", "png": "image/png"}[ext]
    with open(os.path.join(IMG, name), "rb") as f:
        return f"data:{mime};base64," + base64.b64encode(f.read()).decode()


# ---------- 手描き風 SVG（乱数は seed 固定で再現可能） ----------
ACCENT = "#e10912"   # macro_after.jpg（赤スニーカー）の中央値から採った実色
BLUE = "#3118cd"     # macro_before.jpg（元の青）の中央値


def _poly(pts):
    return "M " + " L ".join(f"{x:.1f} {y:.1f}" for x, y in pts)


def circle(cx, cy, rx, ry, seed, color=ACCENT, width=6, turns=1.12, rot=0):
    """マーカーで囲んだような、少し閉じすぎる楕円。"""
    rnd = random.Random(seed)
    n = 56
    pts = []
    start = rnd.uniform(0, 2 * math.pi)
    for i in range(int(n * turns) + 1):
        t = start + i / n * 2 * math.pi
        r1 = 1 + rnd.uniform(-0.035, 0.035)
        r2 = 1 + rnd.uniform(-0.035, 0.035)
        drift = i / (n * turns) * 6  # 書き終わりが少し内側へ流れる
        pts.append((cx + (rx * r1 - drift) * math.cos(t), cy + (ry * r2 - drift) * math.sin(t)))
    return (f'<path d="{_poly(pts)}" fill="none" stroke="{color}" stroke-width="{width}" '
            f'stroke-linecap="round" stroke-linejoin="round" transform="rotate({rot} {cx} {cy})"/>')


def arrow(x1, y1, x2, y2, seed, color=ACCENT, width=6, head=22):
    rnd = random.Random(seed)
    n = 14
    pts = []
    for i in range(n + 1):
        t = i / n
        nx, ny = -(y2 - y1), (x2 - x1)
        ln = math.hypot(nx, ny) or 1
        bow = math.sin(t * math.pi) * rnd.uniform(-4, 4)
        pts.append((x1 + (x2 - x1) * t + nx / ln * bow, y1 + (y2 - y1) * t + ny / ln * bow))
    ang = math.atan2(y2 - y1, x2 - x1)
    h1 = (x2 - head * math.cos(ang - 0.5), y2 - head * math.sin(ang - 0.5))
    h2 = (x2 - head * math.cos(ang + 0.5), y2 - head * math.sin(ang + 0.5))
    return (f'<path d="{_poly(pts)}" fill="none" stroke="{color}" stroke-width="{width}" stroke-linecap="round" stroke-linejoin="round"/>'
            f'<path d="{_poly([h1, (x2, y2), h2])}" fill="none" stroke="{color}" stroke-width="{width}" stroke-linecap="round" stroke-linejoin="round"/>')


def underline(x1, x2, y, seed, color="#ffd83d", width=18):
    """蛍光マーカー風の下線。"""
    rnd = random.Random(seed)
    n = 10
    pts = [(x1 + (x2 - x1) * i / n, y + rnd.uniform(-2.5, 2.5)) for i in range(n + 1)]
    return (f'<path d="{_poly(pts)}" fill="none" stroke="{color}" stroke-width="{width}" '
            f'stroke-linecap="round" stroke-linejoin="round" opacity="0.85"/>')


def svg(w, h, body, x=0, y=0, cls=""):
    return (f'<svg class="ov {cls}" style="left:{x}px;top:{y}px" width="{w}" height="{h}" '
            f'viewBox="0 0 {w} {h}" xmlns="http://www.w3.org/2000/svg">{body}</svg>')


# ---------- CSS ----------
CSS = """
@font-face{ font-family:"Yomogi"; src:url("fonts/Yomogi-Regular.ttf"); }
@font-face{ font-family:"Klee One"; src:url("fonts/KleeOne-SemiBold.ttf"); font-weight:600; }
:root{ --bg:#f7f6f4; --ink:#1d1d1f; --muted:#7d7a75; --hairline:#e4e1dc; }
@media (prefers-color-scheme: dark){ :root{ --bg:#111114; --ink:#f0efec; --muted:#96938e; --hairline:#28282c; } }
:root[data-theme="dark"]{ --bg:#111114; --ink:#f0efec; --muted:#96938e; --hairline:#28282c; }
:root[data-theme="light"]{ --bg:#f7f6f4; --ink:#1d1d1f; --muted:#7d7a75; --hairline:#e4e1dc; }
html,body{ background:var(--bg); color:var(--ink); }
body{ font-family:"Noto Sans JP","Hiragino Sans","Yu Gothic",Meiryo,sans-serif; font-feature-settings:"palt"; line-height:1.6; }
.wrap{ max-width:1080px; margin:0 auto; padding:56px 24px 96px; }
h1{ font-size:clamp(28px,4.5vw,44px); font-weight:900; line-height:1.25; margin:10px 0 14px; }
.lede{ color:var(--muted); max-width:40em; font-size:15px; }
.slide-head{ margin:72px 0 14px; display:flex; align-items:baseline; gap:14px; flex-wrap:wrap; }
.slide-head .no{ font-size:13px; font-weight:800; letter-spacing:.14em; }
.slide-head .use{ font-size:13px; color:var(--muted); }
.note{ font-size:13px; color:var(--muted); margin-top:10px; max-width:46em; }
.note b{ color:var(--ink); font-weight:600; }
.stage{ position:relative; width:100%; overflow:hidden; border-radius:4px; outline:1px solid var(--hairline); }

/* ---- board 共通 ---- */
.board{ position:absolute; top:0; left:0; width:1280px; height:1280px; transform-origin:top left; overflow:hidden;
  background:#ffffff; color:#1a1a1a; font-family:"Noto Sans JP",sans-serif; font-feature-settings:"palt"; line-height:1.3; }
.board *{ box-sizing:border-box; }
.board .abs{ position:absolute; }
.board .ov{ position:absolute; pointer-events:none; }
.board .kicker{ position:absolute; font-family:"Klee One"; font-weight:600; font-size:30px; color:#5b5752; letter-spacing:.02em; }
.board h2{ position:absolute; margin:0; font-weight:900; line-height:1.22; letter-spacing:.005em; }
.board .sub{ position:absolute; font-size:34px; font-weight:500; color:#4a4744; line-height:1.55; }
.board .card{ position:absolute; background:#f7f6f4; border:2px solid #e6e0d9; border-radius:26px; overflow:hidden; }
.board .hand{ font-family:"Yomogi"; }
.board .klee{ font-family:"Klee One"; font-weight:600; }
.board .lbl{ position:absolute; font-family:"Klee One"; font-weight:600; font-size:30px; color:#3a3733; }
.board .red{ color:#e10912; }
.board .foot{ position:absolute; left:64px; right:64px; bottom:44px; display:flex; align-items:baseline; justify-content:space-between; }
.board .brand{ font-family:"Noto Serif JP",serif; font-weight:900; font-size:40px; letter-spacing:.06em; }
.board .brand small{ font-family:"Noto Sans JP"; font-weight:500; font-size:22px; color:#8a8781; letter-spacing:.08em; margin-left:14px; }
.board .credit{ font-size:21px; color:#8a8781; letter-spacing:.02em; }
.board .pageno{ font-family:"Yomogi"; font-size:30px; color:#8a8781; }
.board .sticker{ position:absolute; width:196px; height:196px; border-radius:50%; background:#e10912; color:#fff;
  display:flex; flex-direction:column; align-items:center; justify-content:center; transform:rotate(-11deg);
  box-shadow:0 8px 0 rgba(0,0,0,.08); }
.board .sticker b{ font-size:78px; font-weight:900; line-height:1; letter-spacing:.02em; }
.board .sticker span{ font-family:"Klee One"; font-size:22px; margin-top:8px; opacity:.95; }
.board .hl{ background:linear-gradient(transparent 58%, #ffd83d 58%, #ffd83d 94%, transparent 94%); }
.board .pill{ position:absolute; font-family:"Klee One"; font-weight:600; font-size:27px; color:#1a1a1a;
  border:3px solid #1a1a1a; border-radius:999px; padding:8px 26px; background:#fff; }
.board .pill.ok{ border-color:#e10912; color:#e10912; }
.board .pill.no{ border-color:#8a8781; color:#8a8781; }
.board .win{ position:absolute; border:1px solid #3c3c3c; box-shadow:0 18px 40px rgba(0,0,0,.22); image-rendering:auto; }
.board .step{ position:absolute; width:520px; }
.board .step .n{ position:absolute; left:0; top:-6px; width:60px; height:60px; border-radius:50%; border:4px solid #e10912;
  color:#e10912; font-family:"Klee One"; font-weight:600; font-size:34px; display:flex; align-items:center; justify-content:center; }
.board .step .t{ margin-left:84px; font-size:34px; font-weight:700; line-height:1.4; }
.board .step .d{ margin-left:84px; font-family:"Klee One"; font-weight:600; font-size:26px; color:#5b5752; margin-top:6px; line-height:1.45; }
.board .spec{ position:absolute; display:flex; gap:18px; flex-wrap:wrap; }
.board .spec div{ font-size:26px; font-weight:700; border:3px solid #1a1a1a; border-radius:14px; padding:10px 22px; background:#fff; }
.board .spec div small{ display:block; font-family:"Klee One"; font-weight:600; font-size:19px; color:#8a8781; margin-top:-2px; }
.board .cap{ position:absolute; font-family:"Klee One"; font-weight:600; font-size:26px; color:#5b5752; line-height:1.5; }
.alts{ margin-top:88px; padding-top:28px; border-top:1px solid var(--hairline); font-size:14px; color:var(--muted); }
.alts b{ color:var(--ink); }
.alts ul{ margin:10px 0 0 1.2em; display:grid; gap:6px; }
"""


def foot(page, credit="モデル: HAOLAN（かなリぁ）"):
    return (f'<div class="foot"><div class="brand">いろか<small>Iroca</small></div>'
            f'<div class="credit">{credit}</div><div class="pageno">{page} / 6</div></div>')


# ---------- SLIDE 1 表紙 ----------
S1 = f"""
<div class="board s1">
  <div class="kicker" style="left:88px;top:92px">VRChat アバターのテクスチャを色替えする Unity ツール</div>
  <h2 style="left:84px;top:150px;font-size:118px">PSDがなくても、<br><span class="hl">色ちがい</span>。</h2>
  <div class="sub" style="left:88px;top:470px;width:900px">スポイトで色を取って、変更先を選ぶ。<br>陰影はそのまま、色だけが変わる。</div>
  <div class="sticker" style="left:1010px;top:70px"><b>無料</b><span>全機能つかえる</span></div>
  <div class="card" style="left:60px;top:600px;width:1160px;height:560px">
    <img src="{b64('hero_pair.jpg')}" style="position:absolute;left:0;top:-92px;width:1160px" alt="左が元の青、右がいろかで赤にした衣装">
    <div class="pill" style="left:22px;top:18px;font-size:26px;padding:4px 20px">元のテクスチャ</div>
    <div class="pill ok" style="left:934px;top:18px;font-size:26px;padding:4px 20px">いろかで赤に</div>
  </div>
  {foot(1)}
</div>
"""

# ---------- SLIDE 2 ラインナップ ----------
S2 = f"""
<div class="board s2">
  <h2 style="left:64px;top:92px;font-size:92px">1枚のテクスチャから、<span class="hl">5色</span>。</h2>
  <div class="sub" style="left:68px;top:232px;width:1100px">衣装・髪・スニーカーをそれぞれ色替え。同じポーズ、同じ光で撮影。</div>
  <div class="card" style="left:60px;top:340px;width:1160px;height:700px">
    <img src="{b64('lineup_wide.jpg')}" style="position:absolute;left:0;top:26px;width:1160px" alt="左端が元、右に5色">
    <div class="lbl" style="left:112px;top:36px;font-size:34px">元</div>
    {svg(140, 100, circle(70, 50, 44, 34, seed=21, width=6), x=80, y=10)}
    <div class="lbl red" style="left:520px;top:36px;font-size:34px">いろかで色替え（5色）</div>
    <div class="abs" style="left:262px;top:80px;width:2px;height:560px;background:#d6d1ca"></div>
  </div>
  <div class="cap" style="left:68px;top:1064px;width:1140px">プリセットは 1 つ。変更先の色だけ差し替えて 5 回書き出した。<br>写真は Unity 上で撮った実物で、レタッチなし。</div>
  {foot(2)}
</div>
"""

# ---------- SLIDE 3 マクロ ----------
S3 = f"""
<div class="board s3">
  <h2 style="left:64px;top:92px;font-size:92px">紐の艶も、宝石の光も、<br>そのまま<span class="hl">残る</span>。</h2>
  <div class="card" style="left:60px;top:400px;width:540px;height:540px;border-radius:22px">
    <img src="{b64('macro_before.jpg')}" style="position:absolute;left:0;top:0;width:540px;height:540px" alt="元の青のスニーカー">
  </div>
  <div class="card" style="left:680px;top:400px;width:540px;height:540px;border-radius:22px">
    <img src="{b64('macro_after.jpg')}" style="position:absolute;left:0;top:0;width:540px;height:540px" alt="赤に色替えしたスニーカー">
    {svg(540, 540, circle(415, 230, 80, 38, seed=31, width=6, rot=-4) + circle(275, 398, 214, 38, seed=32, width=6, rot=1))}
  </div>
  {svg(120, 80, arrow(10, 40, 100, 40, seed=33, width=7), x=600, y=640)}
  <div class="lbl" style="left:60px;top:960px">元のテクスチャ</div>
  <div class="lbl red" style="left:680px;top:960px">いろかで色替え</div>
  <div class="cap red" style="left:860px;top:344px;width:360px;text-align:right">組紐の結び目の陰影</div>
  {svg(200, 260, arrow(150, 10, 120, 232, seed=34, width=5), x=980, y=386)}
  <div class="cap red" style="left:760px;top:1000px;width:460px;text-align:right">↑ 宝石のハイライトまで赤</div>
  <div class="cap" style="left:64px;top:1064px;width:1140px">プリセット HAOLAN_Sneakers_Red（許容範囲 0.25）をそのまま適用。同じカメラ、同じ照明。</div>
  {foot(3)}
</div>
"""

# ---------- SLIDE 4 使い方（実 UI） ----------
# ui_panel.png: いろか ウィンドウ左列（①元テクスチャ〜加工設定）を 400×517 で切り出したもの。1.45 倍で置く。
PX, PY, PS = 640, 380, 1.45
def p(x, y):  # panel 座標 → board 座標
    return PX + x * PS, PY + y * PS
sp = p(320, 214)      # スポイトボタン中心（panel 内 263-380, 204-224）
tc = p(286, 242)      # 変更先カラー欄中心（192-380, 232-252）
tl = p(286, 320)      # 許容範囲スライダー（192-380, 312-327）
S4 = f"""
<div class="board s4">
  <h2 style="left:64px;top:92px;font-size:84px">スポイトでクリック。<br>あとは、変更先の色を選ぶ。</h2>
  <div class="kicker" style="left:{PX}px;top:{PY-46}px;font-size:26px">実際の画面（Unity 2022.3 / いろか v0.2）</div>
  <img class="win" src="{b64('ui_panel.png')}" style="left:{PX}px;top:{PY}px;width:{400*PS:.0f}px;height:{477*PS:.0f}px" alt="いろかのウィンドウ左列">
  {svg(1280, 1280, circle(sp[0], sp[1], 108, 30, seed=41, width=5)
                 + circle(tc[0], tc[1], 156, 30, seed=42, width=5)
                 + circle(tl[0], tl[1], 156, 28, seed=43, width=5)
                 + arrow(560, 470, sp[0]-118, sp[1], seed=44, width=5)
                 + arrow(560, 640, tc[0]-166, tc[1], seed=45, width=5)
                 + arrow(560, 842, tl[0]-166, tl[1]+4, seed=46, width=5))}
  <div class="step" style="left:64px;top:410px"><div class="n">1</div><div class="t">「スポイト」を押して、<br>変えたい色をクリック</div><div class="d">プレビュー上の画素から直接色を取る</div></div>
  <div class="step" style="left:64px;top:590px"><div class="n">2</div><div class="t">「変更先カラー」で<br>好きな色を選ぶ</div><div class="d">プレビューにすぐ反映される</div></div>
  <div class="step" style="left:64px;top:780px"><div class="n">3</div><div class="t">広すぎ・狭すぎは<br>「許容範囲」で直す</div><div class="d">「自動調整」に任せてもいい</div></div>
  <div class="step" style="left:64px;top:970px"><div class="n">4</div><div class="t">エクスポートで PNG に保存</div><div class="d">元のファイルは上書きしない</div></div>
  {foot(4, credit="")}
</div>
"""

# ---------- SLIDE 5 書き出し ----------
S5 = f"""
<div class="board s5">
  <h2 style="left:64px;top:92px;font-size:92px">書き出されるのは、<br>ふつうの <span class="hl">PNG</span>。</h2>
  <div class="card" style="left:60px;top:400px;width:520px;height:520px;border-radius:22px;background:#fff">
    <img src="{b64('atlas_before.jpg')}" style="position:absolute;left:0;top:0;width:520px;height:520px" alt="元のテクスチャ">
  </div>
  <div class="card" style="left:700px;top:400px;width:520px;height:520px;border-radius:22px;background:#fff">
    <img src="{b64('atlas_after.jpg')}" style="position:absolute;left:0;top:0;width:520px;height:520px" alt="いろかで書き出したテクスチャ">
  </div>
  {svg(120, 80, arrow(10, 40, 100, 40, seed=51, width=7), x=580, y=620)}
  <div class="lbl" style="left:60px;top:940px">スニーカーの元テクスチャ <span style="font-size:24px;color:#8a8781">4096×4096</span></div>
  <div class="lbl red" style="left:700px;top:940px">いろかで書き出し <span style="font-size:24px;color:#8a8781">別ファイル</span></div>
  <div class="spec" style="left:64px;top:1040px;width:1160px">
    <div>Unity 2022.3<small>Editor 拡張</small></div>
    <div>Windows 10 / 11<small>動作確認済み</small></div>
    <div>PNG / JPG<small>読み込み</small></div>
    <div>ネット接続 不要<small>ローカルで処理</small></div>
    <div>アトラス OK<small>まとめテクスチャも</small></div>
  </div>
  {foot(5, credit="")}
</div>
"""

# ---------- SLIDE 6 エンジン比較 ----------
S6 = f"""
<div class="board s6">
  <h2 style="left:64px;top:92px;font-size:92px">色相を回すだけだと、<br>こうなる。</h2>
  <div class="card" style="left:60px;top:400px;width:540px;height:540px;border-radius:22px">
    <img src="{b64('compare-hsv.png')}" style="position:absolute;left:0;top:0;width:540px;height:540px" alt="色相だけ回したスニーカー">
    {svg(540, 540, circle(300, 452, 190, 40, seed=61, color='#8a8781', width=5))}
  </div>
  <div class="card" style="left:680px;top:400px;width:540px;height:540px;border-radius:22px">
    <img src="{b64('compare-iroca.png')}" style="position:absolute;left:0;top:0;width:540px;height:540px" alt="いろかで山吹にしたスニーカー">
    {svg(540, 540, circle(300, 452, 190, 40, seed=62, width=5))}
  </div>
  <div class="pill no" style="left:60px;top:960px">✕ 色相を回しただけ</div>
  <div class="pill ok" style="left:680px;top:960px">○ いろか</div>
  <div class="cap" style="left:60px;top:1030px;width:540px">明るい所が赤桃に濁って、宝石が飛ぶ。</div>
  <div class="cap" style="left:680px;top:1030px;width:540px">光点まで山吹のまま。影も残る。</div>
  <div class="cap" style="left:64px;top:1120px;width:1160px;color:#8a8781;font-size:23px">同じ画素、同じ目標色。いろかは OkLab（知覚色空間）で明るさを別に扱うので、色相を回しても明暗が崩れない。</div>
  {foot(6, credit="")}
</div>
"""

HTML = f"""<meta charset="utf-8">
<title>いろか — BOOTH販促画像 v3 コンプ</title>
<style>{CSS}</style>
<div class="wrap">
  <header>
    <h1>いろか BOOTH 販促画像 v3（BOOTH ネイティブ × 実物主義）</h1>
    <p class="lede">v2 の「Apple 調・全面中央揃え・文字だけのスライド」を、BOOTH の一覧で浮かない文法へ寄せた版。写真は v2 と同じ実物（HAOLAN を実 C# エンジンで色替えし Unity で撮影）。使い方は実際の いろか ウィンドウ、仕様は書き出した PNG そのものと一緒に見せる。</p>
  </header>

  <div class="slide-head"><span class="no">SLIDE 1</span><span class="use">表紙・1280×1280</span></div>
  <div class="stage">{S1}</div>
  <p class="note"><b>狙い：</b>「PSD がない」という具体的な困りごとを見出しに。無料タグ・手描き矢印・キャラ主役は BOOTH の無料ツール（lilToon / TexTransTool 型）の文法。</p>

  <div class="slide-head"><span class="no">SLIDE 2</span><span class="use">カラーラインナップ・1280×1280</span></div>
  <div class="stage">{S2}</div>
  <p class="note"><b>狙い：</b>見出しに数字（1 枚 → 5 色）。注記は作った本人の言葉で、真実だけ（プリセット 1 つの変更先差し替え）。</p>

  <div class="slide-head"><span class="no">SLIDE 3</span><span class="use">品質の証明（マクロ）・1280×1280</span></div>
  <div class="stage">{S3}</div>
  <p class="note"><b>狙い：</b>抽象語（陰影・質感）ではなく、見れば分かる部位（組紐・宝石）を赤丸で名指し。</p>

  <div class="slide-head"><span class="no">SLIDE 4</span><span class="use">使い方（実 UI）・1280×1280</span></div>
  <div class="stage">{S4}</div>
  <p class="note"><b>素材：</b>img/ui_panel.png は起動中の いろか ウィンドウ左列（①元テクスチャ〜加工設定）を切り出したもの。プレビュー領域は含めていない（他アバターのテクスチャを写さないため）。HAOLAN を読み込んだ全体ショットに差し替える場合は README の再撮影手順。</p>

  <div class="slide-head"><span class="no">SLIDE 5</span><span class="use">書き出し・仕様・1280×1280</span></div>
  <div class="stage">{S5}</div>
  <p class="note"><b>狙い：</b>v2 の文字だけの仕様スライドを、実際に書き出される PNG（元 4096×4096 のスニーカーテクスチャと赤の出力）と一緒に。仕様はタグで最小限。</p>

  <div class="slide-head"><span class="no">SLIDE 6</span><span class="use">エンジン比較・1280×1280（掲載順は SLIDE 3 の直後を推奨）</span></div>
  <div class="stage">{S6}</div>
  <p class="note"><b>狙い：</b>「安っぽくならない」という評価語をやめ、失敗を名指し（色相を回すだけだと〜）。✕／○ は日本の定番記号。</p>

  <section class="alts">
    <b>v2 からの変更点（AI っぽさ対策）</b>
    <ul>
      <li>全スライド同型の「中央揃え大見出し＋灰色サブ＋灰色フッター」→ 左寄せ・1 枚 1 主張・枚ごとに構図を変える。</li>
      <li>文字だけの 3 カラム（使い方・仕様）→ 実 UI スクショ＋赤丸注釈、書き出し PNG の実物＋仕様タグ。</li>
      <li>トラッキングを広げた英字 eyebrow、「→」文字、文字グラデ → 手描き矢印・赤丸・蛍光マーカー・無料タグ。</li>
      <li>評価語（安っぽく・質感）→ 具体名（組紐・宝石・許容範囲 0.25・4096×4096・プリセット名）。</li>
      <li>「〜だけ。」の連発 → 見出しごとに文型を変える（困りごと／数字／部位／手順／実物／失敗の名指し）。</li>
    </ul>
  </section>
</div>

<script>
  function fit(){{
    document.querySelectorAll('.stage').forEach(function(s){{
      var b = s.firstElementChild;
      var sc = Math.min(1, s.clientWidth / 1280);
      b.style.transform = 'scale(' + sc + ')';
      s.style.height = (1280 * sc) + 'px';
    }});
  }}
  var exportN = parseInt(new URLSearchParams(location.search).get('board') || '', 10);
  if (exportN) {{
    var b = document.querySelectorAll('.board')[exportN - 1];
    document.body.replaceChildren(b);
    document.body.style.margin = '0';
    b.style.position = 'fixed'; b.style.top = '0'; b.style.left = '0'; b.style.transform = 'none';
  }} else {{
    addEventListener('resize', fit);
    fit();
  }}
</script>
"""

out = os.path.join(OUT_DIR, "booth-promo-v3.html")
with open(out, "w", encoding="utf-8") as f:
    f.write(HTML)
print("written", out, len(HTML))
