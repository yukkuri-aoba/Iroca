"""改善サイクル用視覚レビュースクリプト(出荷ゲート)。

使い方:
    python tools/visual_review.py snapshot                 # 変更前の出力を保存(既定で HEAD のコードから)
    python tools/visual_review.py snapshot --worktree      # 作業ツリーのコードから(変更前の作業ツリーを撮るとき)
    python tools/visual_review.py compare                  # 作業ツリーのコードで、固定設定 + ワンショット/追加操作後を比べる
    python tools/visual_review.py compare --index          # ステージした中身のコードで比べる(他人の未コミットを入れない)
    python tools/visual_review.py approve --note "<目視した範囲と根拠>"  # 承認(測った中身に結び付く)
    python tools/visual_review.py check [--index]          # 承認が有効か、今の中身をコミットしてもフックを通るか

  エンジンは csharp のみ(--engine csharp は書いても書かなくてもよい)。ワンショット/追加操作後には ONNX と凍結埋め込みも必要。
  固定設定の無マスク出力はワンショットではない。全条件の目視後に approve する。

承認は時刻ではなく「測った中身」に結び付く(2026-11-10、点検 B-1・B-4・B-7①)。compare は測るものを決めるファイル
(コード・測定器・dev_safe の基準。tools/review_state.py)の中身を測り始めと終わりに取り、食い違えばやり直させる。
pre-commit は、ステージした範囲のファイルが「HEAD と同じ」か「承認した比較で測った中身と同じ」なら通す(ファイル単位)。
snapshot は既定で HEAD のコードをビルドして撮る(「変更前」がいつも HEAD になり、共有の作業ツリーにある他人の未コミットも
入らない)。approve は、変更前と同じ中身を測った比較(変化 0 が何も示さない)を断る。

改善サイクルでの手順:
    1. 変更を加える前に(HEAD が変更前なら後でもよい) `snapshot` を実行。測定器(Python)や dev_safe の基準を変えるときは、
       変える前に撮る(snapshot は測定器を作業ツリーのまま動かすので、変えた後に撮ると変更前にならない)
    2. 変更を加えてテストを実行
    3. `compare` を実行 → 一覧に出た(変化があった)PNG を Read ツールで1枚ずつ確認
         - 元テクスチャにない質感・ノイズが追加されていないか
         - 変換対象エリアに変換漏れがないか
         - 関係ない部分が変わっていないか（全体サムネイル必須）
         - テクスチャを切り出して作業した場合も、全体サムネイルで切り出し外を確認
       `+masked` 付きのケースは除外マスクを与えて走らせたもので、compare が
       「守った画素が 1 つも変わっていないか」を機械で数える（目視では見えないため）。
    4. 問題がなければ `approve` を実行（マスク契約・α・選択の使い回しの食い違いがあると approve は失敗する）
    5. コミット（pre-commit フックが、ステージした中身と承認を突き合わせる）
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path

# numpy より前に、回帰(dev_safe/Tests/conftest.py)と同じメモリの設定をする(2026-11-09)。どれも確保の仕方だけで、
# 出力は変わらない。立てる前の compare は OpenBLAS が読み込むだけで 1GB のコミットを持ち(numpy と scipy が
# それぞれスレッド数ぶんの作業領域を取る。1 にすると 40MB)、出力のプロセス内メモも 12 件(4K で 1 件 64MB)まで
# 溜めていた。外から値を渡したときはそちらを使う。
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("IROCA_HARNESS_MEM_ENTRIES", "2")
os.environ.setdefault("DOTNET_gcConcurrent", "0")
os.environ.setdefault("DOTNET_GCConserveMemory", "7")

import numpy as np
from PIL import Image, ImageDraw

# --- sys.path 設定 ---
_ROOT = Path(__file__).resolve().parent.parent          # リポジトリルート

sys.path.insert(0, str(Path(__file__).resolve().parent))
import review_state as rs                                # noqa: E402
from workflow_contract import MASKED_REVIEW_CASES        # noqa: E402
_DEV_SAFE = _ROOT / "dev_safe"
_TESTS = _DEV_SAFE / "Tests"
for _p in [str(_DEV_SAFE), str(_TESTS)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

from regression.fixtures import (
    SUBJECT_REGISTRY,
    _zone_to_harness,
    load_cases,
    load_subject_inputs,
    make_zone,
    default_settings,
)

# --- 出力ディレクトリ ---
REVIEW_DIR = _TESTS / "visual_review"
SNAPSHOT_BEFORE_DIR = REVIEW_DIR / "snapshot_before"
COMPARE_DIR = REVIEW_DIR / "compare"
APPROVED_JSON = REVIEW_DIR / "approved.json"
CSHARP_WORK = REVIEW_DIR / "_csharp_work"
# snapshot で PNG を並べて保存するスレッドの数(4K の出力 1 枚 64MB。待ちはこの 2 倍まで)。
SNAPSHOT_SAVE_WORKERS = 4

THUMB_HEIGHT = 512  # 比較パネルの列高さ（px）

# 除外マスク付きレビューケース（subject_id -> 使うケース数）。
#
# 「ユーザーが手で守った画素は絶対に変わらない」は「プレビュー＝出力」と並ぶ中核保証だが、
# 2026-08-07 まで視覚レビューの全ケースが np.zeros(= マスク無し)でハーネスを呼んでおり、
# **出荷ゲートにマスク経路が 1 件も無かった**。実際 N-1(CleanAchromaFringe が除外マスクを
# 参照せず保護画素を上書きする契約違反)はこのゲートを素通りし、修正後も全ケースがビット一致で
# 「何も検証できていない」状態だった(レビュー 2026-08-06 の N-12)。
#
# 被写体は経路の違いで選ぶ: 有彩サンプルの通常マッチ経路と、暗い無彩サンプル(グレーモード +
# 無彩フチ消し = N-1 の現場)を 1 被写体ずつ。具体名は dev_safe/Tests/subjects.json。
# 色数は実行コスト(1 ケース = dotnet 1 プロセス × フル解像度)とのバランスで先頭 2 色に絞る。
MASKED_SUBJECT_CASES = MASKED_REVIEW_CASES

# マスク付きケースの case_id 接尾辞。compare 側の契約検査もこれで判定する。
MASKED_SUFFIX = "+masked"

# マスク契約の検査結果（compare が書き、approve が読む）。
MASK_CONTRACT_JSON = REVIEW_DIR / "mask_contract.json"

# 変化画素の内訳（compare が書く。目視の当たりを付けるための数値）。
CHANGE_SUMMARY_JSON = REVIEW_DIR / "change_summary.json"


# ---------------------------------------------------------------------------
# ユーティリティ
# ---------------------------------------------------------------------------
def _run_all_cases() -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """(引退) Python 再実装は削除済み。csharp エンジンを使うこと。"""
    raise RuntimeError(
        "Python エンジン(vacc_python)は完全引退しました。"
        "実 C# 出力で確認してください: python tools/visual_review.py <cmd> --engine csharp")


def _build_review_mask(gt_mask: np.ndarray) -> np.ndarray:
    """GT 領域の左半分だけを守る除外マスク(uint8, 1=除外)を作る。

    マスクは「変換対象になるはずの領域の一部」を守る形でないと検出力が無い
    (対象外を守っても出力は何も変わらないので契約違反が現れない)。
    分割位置は **GT 自身の bbox 中央** から決めるので、被写体ごとの座標を焼き込まずに
    「守った側」と「変換される側」が必ず両方存在する。
    """
    xs = np.where(gt_mask.any(axis=0))[0]
    if xs.size < 2:
        raise RuntimeError("GT マスクが空か 1 列しかなく、マスクケースを作れません")
    mid = int((int(xs[0]) + int(xs[-1])) // 2)
    excl = gt_mask.copy()
    excl[:, mid:] = False
    if not excl.any():
        raise RuntimeError("生成した除外マスクが空です")
    return excl.astype(np.uint8)


def _iter_cases_csharp():
    """全ケースを **実 C# Harness** で実行し、(case_id, input_rgba, output_rgba, exclude_or_None) を返す。

    視覚レビューが Python ではなく実際に出荷される C# 出力を見られるようにする
    (project_visual_review_washoff_blind の「視覚レビューが C# に盲目」を解消)。
    dotnet/Unity DLL が無ければ RuntimeError で中断する。

    実行は回帰テストと同じ `fixtures.run_harness_many`(実行キャッシュ + --batch)に一本化する
    (2026-09-14)。ゾーン JSON・設定・除外マスクの直列化が IoU 回帰(`run_csharp`)と同一なので、
    直前に回帰スイートを回していれば同じ DLL の出力がキャッシュから返り、79 ケースを 1 件ずつ
    dotnet 起動していた約 15 分がほぼ消える。キャッシュキーに DLL のダイジェストが入るので、
    Code/ を変えて再ビルドした時点で古い出力が使われることはない(回帰テストと同じ保証)。
    --batch の出力が単発実行と byte 一致することは dev_safe/scripts/verify_harness_batch.py と
    test_harness_batch_parity.py が検証している。

    末尾で MASKED_SUBJECT_CASES の被写体について除外マスク付きの変種も回す
    (case_id は `<元の case_id>+masked`)。exclude が None でないケースは、compare が
    「マスク画素が 1 つも変わっていないか」を機械で検査する。
    """
    from regression import fixtures as fx

    try:
        fx.ensure_harness()
    except fx.HarnessUnavailable as e:
        raise RuntimeError(f"dotnet / Unity DLL が利用できません(--engine csharp は使えません): {e}")
    except fx.HarnessBuildFailed as e:
        raise RuntimeError(f"Harness ビルド失敗:\n{e}")

    # 設定は fixtures._settings_to_harness(default_settings()) — IoU 回帰と同一の直列化。
    # ゾーンは fixtures._zone_to_harness で **全フィールド明示** する(IoU 回帰と同一の
    # 直列化)。以前はケース固有 7 フィールドだけ書き、残りを zones JSON 既定
    # (ZonesJsonDefaults)へフォールバックさせていたが、highlightRecovery の JSON 既定は
    # false で **UI の ColorZone 既定(true)と意図的に異なる**(2026-08-07 の GT 実測で
    # MCP/バッチ向けに false を採った。ZonesJsonDefaults.cs 参照)。その結果、
    #   UI(true) / IoU 回帰(true 明示) / このパネル(false)
    # の三者不一致になり、出荷ゲートの目視が製品既定とも回帰テストとも違う設定の絵を
    # 見ていた(2026-08-21 テストと実操作の乖離調査)。全フィールド明示に揃えることで
    # 「このパネルと回帰テストの IoU は同一設定を指す」を再び真にする。
    # ZoneSpec と製品 ColorZone 既定の一致は test_zones_schema_parity.py が機械検査する。
    settings_cfg = fx._settings_to_harness(default_settings())

    def _batch(rgba, cases, suffix: str, exclude):
        """選択を使い回さずに測り(書き出しと同じ経路。点検 D-3)、回帰が使い回して測った出力がキャッシュにあれば
        突き合わせる。返すのは (出力, 使い回した側と違う画素数 or None(キャッシュに無い)) の並び。"""
        specs = [([_zone_to_harness(make_zone(c), c)], settings_cfg, f"{c.case_id}{suffix}")
                 for c in cases]
        outs = fx.run_harness_many(rgba, specs, exclude=exclude, share_selection=False)
        shared = fx.cached_outputs(rgba, specs, exclude=exclude, share_selection=True)
        return [(o, None if s is None else int(np.any(o != s, axis=-1).sum())) for o, s in zip(outs, shared)]

    def _subject_items(subject) -> list:
        """1 被写体ぶんの (case_id, 入力, 出力, 除外, 差) を順に並べて返す(背景のスレッドで呼ぶ)。"""
        rgba, gt_mask = load_subject_inputs(subject)
        cases = load_cases(subject)
        items = [(case.case_id, rgba, out, None, shared_diff)
                 for case, (out, shared_diff) in zip(cases, _batch(rgba, cases, "", None))]   # exclude=None → 全画素処理

        # ── 除外マスク付きの変種(N-12: ゲートにマスク経路を通す) ──
        n_masked = MASKED_SUBJECT_CASES.get(subject.subject_id, 0)
        if n_masked <= 0:
            return items
        if gt_mask is None:
            raise RuntimeError(
                f"{subject.subject_id}: マスクケースには single_mask 戦略の GT が要ります")
        exclude = _build_review_mask(gt_mask)
        exclude_bool = exclude.astype(bool)   # 呼び出し側は読むだけ
        items += [(f"{case.case_id}{MASKED_SUFFIX}", rgba, out, exclude_bool, shared_diff)
                  for case, (out, shared_diff) in zip(cases[:n_masked],
                                                      _batch(rgba, cases[:n_masked], MASKED_SUFFIX, exclude))]
        return items

    # 被写体ごとに実行して返す(呼び出し側が使い終えた出力は次の被写体の前に手放せる)。
    # 呼び出し側が 1 つの被写体の縮小・パネル・PNG の保存をしている間に、次の被写体のハーネスを背景のスレッドで
    # 走らせる(直列・メインスレッドの洗い出しの 試-1・試-2。以前は交互で、片方が動く間もう片方が止まっていた)。
    # 先に求めるのは 1 被写体だけ(同時に持つ出力は、渡している被写体と計算中の被写体の 2 つぶんまで。
    # 全ケースを抱えて 10GB を超えた事故の対策を崩さない)。ハーネスを呼ぶのはこのスレッド 1 本だけで、
    # 渡す順・中身は直列のときと同じ。
    subjects = list(SUBJECT_REGISTRY.values())
    with ThreadPoolExecutor(max_workers=1) as pool:
        ahead = pool.submit(_subject_items, subjects[0]) if subjects else None
        for i in range(len(subjects)):
            items = ahead.result()
            ahead = pool.submit(_subject_items, subjects[i + 1]) if i + 1 < len(subjects) else None
            yield from items
            del items


def _iter_engine(engine: str):
    """(case_id, input_rgba, output_rgba, exclude_or_None, 選択を使い回した出力との差 or None) を順に返す。
    C# は被写体ごとに実行する。"""
    if engine == "csharp":
        yield from _iter_cases_csharp()
        return
    for cid, (i, o, e) in sorted(_run_all_cases().items()):
        yield cid, i, o, e, None


# ---------------------------------------------------------------------------
# 変化の切り分け（N-11: 512px サムネイルだけでは 4K の変化を判定できない）
#
# 「元テクスチャにないノイズが出ていないか」を 512px 3 列パネルで見るのは、2K/4K では
# 物理的に無理がある。実際 2026-08-06〜07 の 2 回のレビューとも、判定のために
# 「変化画素の分類」と「等倍クロップ」を別途スクリプトで書き起こす必要があった。
# ここに組み込んで、数値で当たりを付けてから目視できるようにする。
# ---------------------------------------------------------------------------
CROP_SIZE = 384  # 等倍クロップの一辺（px）


def _changed_px(before: np.ndarray, after: np.ndarray) -> np.ndarray:
    """変更前と RGBA のどれかが違う画素(α だけの変化も変化として数える。点検 B-7⑤)。"""
    return np.any(before != after, axis=-1)


def _classify_change(orig: np.ndarray, before: np.ndarray, after: np.ndarray) -> dict:
    """変化画素を「元へ復帰 / 元から離れた」に分類する。

    元テクスチャからの L1 距離が縮んだ画素は **誤爆着色の除去**、伸びた画素は
    **新規/強化された着色**。総変化画素数だけでは、この 2 つが打ち消し合って
    「大きく変わった」以上のことが分からない。変化は RGBA の 4 チャンネルで数え、α が変わった画素は
    alpha_changed にも数える(2026-11-10 まで RGB だけを見ていて、α だけの変化は「ビット一致」になった)。
    変更前とビット一致なら(ほとんどのケース)、int16 の写しを作らずに 0 を返す(結果は同じ。性能の候補 T1)。
    """
    if before.shape == after.shape and np.array_equal(before, after):
        return {"changed": 0, "alpha_changed": 0, "toward_original": 0, "away_from_original": 0, "max_delta": 0}
    o = orig.astype(np.int16)
    b = before.astype(np.int16)
    a = after.astype(np.int16)
    ch = _changed_px(before, after)
    n = int(ch.sum())
    if n == 0:
        return {"changed": 0, "alpha_changed": 0, "toward_original": 0, "away_from_original": 0, "max_delta": 0}
    db = np.abs(b - o).sum(-1)[ch]
    da = np.abs(a - o).sum(-1)[ch]
    return {
        "changed": n,
        "alpha_changed": int((before[..., 3] != after[..., 3]).sum()),
        "toward_original": int((da < db).sum()),
        "away_from_original": int((da > db).sum()),
        "max_delta": int(np.abs(b - a).max()),
    }


def alpha_violations(orig: np.ndarray, after: np.ndarray) -> int:
    """出力の α が入力の α と違う画素の数。再着色は色だけを変える不変条件(golden の test_alpha_is_preserved が
    合成入力で見ている)なので、実物の被写体でもマスク契約と同じく機械で数え、1 画素でもあれば承認しない。"""
    return int((orig[..., 3] != after[..., 3]).sum())


def _densest_change_window(changed: np.ndarray, size: int) -> tuple[int, int]:
    """変化画素が最も密な size×size 窓の左上座標。積分画像 + 粗いステップで探す。"""
    h, w = changed.shape
    size = min(size, h, w)
    ii = np.pad(np.cumsum(np.cumsum(changed.astype(np.int64), axis=0), axis=1),
                ((1, 0), (1, 0)))
    step = max(1, size // 4)
    best, by, bx = -1, 0, 0
    for y in range(0, h - size + 1, step):
        for x in range(0, w - size + 1, step):
            s = int(ii[y + size, x + size] - ii[y, x + size]
                    - ii[y + size, x] + ii[y, x])
            if s > best:
                best, by, bx = s, y, x
    return by, bx


def phantom_change(orig: np.ndarray, before: np.ndarray, after: np.ndarray) -> dict:
    """出力にだけある線(元では同じ色の隣が出力で分かれた所。dev_safe の regression/phantom_edges.py)の変化。

    変化画素の数では、塗る範囲が広がった・狭まったのと、元に無い線が増えたのとを見分けられない。
    線の組の数を変更前後で数え、変更後にだけある線の画素(new)を返す。等倍クロップはそこを中心にする。
    """
    from regression import phantom_edges as pe
    pb = pe.analyze(orig, before, want_marks=True)
    pa = pe.analyze(orig, after, want_marks=True)
    new = pa["marks"] & ~pb["marks"]
    return {"before": pb["pairs"], "after": pa["pairs"],
            "same_before": pb["same_color_split"], "same_after": pa["same_color_split"],
            "new_px": int(new.sum()), "_new": new, "_marks_after": pa["marks"]}


def _mark_lines(rgba: np.ndarray, marks: np.ndarray, new: np.ndarray) -> np.ndarray:
    """線の画素に印を付けた複製(変更後にだけある線 = 赤紫、前からある線 = 黄)。"""
    out = rgba.copy()
    out[marks & ~new, :3] = (255, 220, 0)
    out[new, :3] = (255, 0, 200)
    return out


def phantom_crop_row(orig: np.ndarray, before: np.ndarray, after: np.ndarray, ph: dict) -> np.ndarray | None:
    """変更後にだけある線がいちばん密な所の等倍クロップ(元・変更前・変更後・印つき)。無ければ None。"""
    from regression import phantom_edges as pe
    if ph["new_px"] == 0:
        return None
    cy, cx = pe.densest_window(ph["_new"], CROP_SIZE)
    marked = _mark_lines(after, ph["_marks_after"], ph["_new"])
    return _hstack_with_sep([
        _add_label(_crop(orig, cy, cx, CROP_SIZE), f"等倍 元 @({cx},{cy}) 元に無い線が増えた所", (120, 40, 120)),
        _add_label(_crop(before, cy, cx, CROP_SIZE), "等倍 変更前", (160, 80, 30)),
        _add_label(_crop(after, cy, cx, CROP_SIZE), "等倍 変更後", (30, 140, 60)),
        _add_label(_crop(marked, cy, cx, CROP_SIZE), "印: 赤紫=増えた線 黄=前からの線", (120, 40, 120)),
    ])


def _crop(rgba: np.ndarray, y: int, x: int, size: int) -> np.ndarray:
    size = min(size, rgba.shape[0], rgba.shape[1])
    return rgba[y:y + size, x:x + size]


def _vstack_with_sep(rows: list[np.ndarray], sep_px: int = 4) -> np.ndarray:
    """行を縦に結合。幅が異なる場合は最大幅に合わせて右側を黒で埋める。"""
    max_w = max(r.shape[1] for r in rows)
    out: list[np.ndarray] = []
    sep = np.full((sep_px, max_w, 4), 200, dtype=np.uint8)
    for i, row in enumerate(rows):
        if i > 0:
            out.append(sep)
        if row.shape[1] < max_w:
            pad = np.zeros((row.shape[0], max_w - row.shape[1], 4), dtype=np.uint8)
            row = np.hstack([row, pad])
        out.append(row)
    return np.vstack(out)


def _make_thumb(rgba: np.ndarray, height: int = THUMB_HEIGHT) -> np.ndarray:
    """RGBA (H,W,4) を指定高さにリサイズ。アスペクト比維持。"""
    h, w = rgba.shape[:2]
    new_w = max(1, int(w * height / h))
    img = Image.fromarray(rgba).resize((new_w, height), Image.LANCZOS)
    return np.array(img)


_LABEL_FONT: object | None = None
_LABEL_FONT_LOADED = False


def _label_font():
    """ラベル用の日本語フォント。見つからなければ PIL 既定（英数のみ）へフォールバック。

    PIL の既定ビットマップフォントは CJK グリフを持たず、ラベルが全部豆腐（□）になる。
    ラベルはこのパネルで「どの列が何か」「違反何 px か」を伝える唯一の手段なので、
    OS 同梱の日本語フォントを探して使う。
    """
    global _LABEL_FONT, _LABEL_FONT_LOADED
    if _LABEL_FONT_LOADED:
        return _LABEL_FONT
    _LABEL_FONT_LOADED = True
    from PIL import ImageFont
    candidates = [
        (r"C:\Windows\Fonts\meiryo.ttc", 0),
        (r"C:\Windows\Fonts\YuGothM.ttc", 0),
        (r"C:\Windows\Fonts\msgothic.ttc", 0),
        ("/System/Library/Fonts/ttf/HiraginoSans-W4.ttc", 0),
        ("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", 0),
    ]
    for path, index in candidates:
        try:
            _LABEL_FONT = ImageFont.truetype(path, 15, index=index)
            return _LABEL_FONT
        except OSError:
            continue
    print("[compare] 警告: 日本語フォントが見つからず、ラベルが豆腐表示になります。")
    return None


LABEL_H = 24  # ラベルバーの高さ（px）


def _add_label(arr: np.ndarray, text: str, bg_rgb: tuple[int, int, int]) -> np.ndarray:
    """上部にカラーバー＋白テキストラベルを追加。"""
    img = Image.fromarray(arr)
    draw = ImageDraw.Draw(img)
    draw.rectangle([0, 0, img.width - 1, LABEL_H - 1], fill=(*bg_rgb, 220))
    draw.text((6, 3), text, fill=(255, 255, 255, 255), font=_label_font())
    return np.array(img)


def _hstack_with_sep(cols: list[np.ndarray], sep_px: int = 4) -> np.ndarray:
    """列を横並びに結合。高さが異なる場合は最大高さに揃える。"""
    max_h = max(c.shape[0] for c in cols)
    padded: list[np.ndarray] = []
    sep = np.full((max_h, sep_px, 4), 200, dtype=np.uint8)
    for i, col in enumerate(cols):
        if i > 0:
            padded.append(sep)
        if col.shape[0] < max_h:
            pad = np.zeros((max_h - col.shape[0], col.shape[1], 4), dtype=np.uint8)
            col = np.vstack([col, pad])
        padded.append(col)
    return np.hstack(padded)


# ---------------------------------------------------------------------------
# 測る DLL と中身(snapshot・compare 共通。2026-11-10、点検 B-1・B-4・B-5・B-7①)
# ---------------------------------------------------------------------------
SNAPSHOT_META = SNAPSHOT_BEFORE_DIR / "meta.json"


def _prepare_harness(source: str, allow_external: bool, cmd: str):
    """source(worktree / index / rev:<rev>)のコードでハーネスをビルドして写し、このプロセスで測る DLL に決める。

    返すのは来歴(tools/harness_build.BuildInfo)。IROCA_HARNESS_DLL(来歴の無い外の DLL)は、allow_external の
    ときだけ使い(None を返す)、その結果は承認できない。
    """
    from regression import fixtures as fx
    if fx.external_dll():
        if not allow_external:
            print(f"[{cmd}] IROCA_HARNESS_DLL={fx.external_dll()} が立っています。出荷ゲートは来歴の分かる DLL"
                  "(このツールがビルドしたもの)で測ります。外して回すか、見るだけなら --allow-external-dll を付けてください"
                  "(その結果は承認できません)。")
            sys.exit(1)
        fx.ensure_harness()
        return None
    try:
        info = fx.build_harness_info(source)
    except fx.HarnessUnavailable as e:
        raise RuntimeError(f"dotnet / Unity DLL が利用できません(--engine csharp は使えません): {e}")
    except fx.HarnessBuildFailed as e:
        raise RuntimeError(f"Harness ビルド失敗:\n{e}")
    fx.pin_harness(info)
    print(f"[{cmd}] ハーネス {info.dll_sha256[:16]} を測ります(コードは {source} から)")
    return info


def _measured_state(source: str, info) -> dict:
    """測り始めの中身。ビルドした DLL の来歴と食い違えば(ビルドの後に編集された)止める(点検 B-7①)。"""
    st = rs.state(source)
    if info is not None:
        bad = sorted(k for k, v in info.sources.items() if st.get(k) != v)
        if bad:
            raise RuntimeError(f"ビルドした後にソースが変わりました: {', '.join(bad[:6])}。やり直してください")
    return st


def _git_head() -> str:
    import subprocess
    r = subprocess.run(["git", "rev-parse", "HEAD"], cwd=_ROOT, capture_output=True, text=True)
    return r.stdout.strip()


# ---------------------------------------------------------------------------
# snapshot
# ---------------------------------------------------------------------------
def cmd_snapshot(engine: str = "csharp", source: str = "rev:HEAD", allow_external: bool = False) -> None:
    """変更前の出力を全ケース分 snapshot_before(と workflow_review/snapshot)に保存する。

    既定は HEAD のコードをビルドして撮る(点検 B-4。「変更前」がいつも HEAD になり、共有の作業ツリーにある他人の
    未コミットの変更も入らない。作業ツリーと HEAD のコードが同じなら作業ツリーの DLL を使うので、実行キャッシュに当たる)。
    meta.json に、どのコード・測定器・基準から撮ったか(state)を残し、compare と approve が「変更前と同じ中身を
    測っていないか」を確かめる。
    """
    info = _prepare_harness(source, allow_external, "snapshot")
    st0 = _measured_state(source, info) if info is not None else None
    SNAPSHOT_BEFORE_DIR.mkdir(parents=True, exist_ok=True)
    # 途中で止まった snapshot を、来歴つきの完全なものと取り違えないように、先に古い meta を消す
    SNAPSHOT_META.unlink(missing_ok=True)
    import workflow_review
    (workflow_review.REVIEW / "snapshot" / "meta.json").unlink(missing_ok=True)
    print(f"[snapshot] 全ケースをスナップショット中 (engine={engine}, コード={source}) …")
    # 1 件ずつ保存して手放す(全ケースの出力を同時に抱えるとメモリが尽きる。compare と同じ)。
    # 4K の PNG の圧縮(1 枚 0.5 秒前後)はスレッドで並べる(圧縮の間は GIL を放す)。待ちの数に上限を付けて、
    # 保存を待つ出力を持ちすぎない。中身は 1 本で保存したときと同じバイト列。
    saved: list[str] = []
    pending: list = []
    with ThreadPoolExecutor(max_workers=SNAPSHOT_SAVE_WORKERS) as pool:
        for case_id, _input, output_rgba, _exclude, _shared_diff in _iter_engine(engine):
            pending.append(pool.submit(Image.fromarray(output_rgba).save, SNAPSHOT_BEFORE_DIR / f"{case_id}.png"))
            saved.append(case_id)
            print(f"  保存: {case_id}.png")
            while len(pending) > 2 * SNAPSHOT_SAVE_WORKERS:
                pending.pop(0).result()
        for f in pending:
            f.result()
    print(f"[snapshot] {len(saved)} ケース完了 → {SNAPSHOT_BEFORE_DIR}")
    workflow_review.generate(snapshot=True, measured={"source": source, "state": st0, "run_id": None})
    if st0 is not None:
        changed = rs.diff(rs.state(source), st0)
        if changed:
            raise RuntimeError(f"snapshot の途中で中身が変わりました: {', '.join(changed[:6])}。撮り直してください")
    meta = {
        "timestamp": datetime.now().isoformat(),
        "cases": sorted(saved),
        # 来歴(点検 B-4)。state が無い(外の DLL・2026-11-10 より前の)snapshot は、何から撮ったか分からない
        "source": source,
        "state": st0,
        "state_digest": rs.digest(st0) if st0 is not None else None,
        "head": _git_head(),
        "dirty": (rs.code_state("worktree") != rs.code_state(source)) if st0 is not None else None,
        "dll_sha256": info.dll_sha256 if info is not None else None,
    }
    text = json.dumps(meta, indent=2, ensure_ascii=False)
    SNAPSHOT_META.write_text(text, encoding="utf-8")
    (workflow_review.REVIEW / "snapshot" / "meta.json").write_text(text, encoding="utf-8")
    if meta["dirty"]:
        print("[snapshot] 注意: 作業ツリーのコードは、撮ったコードと違います(compare で、その違いが変化として出ます)。")


def snapshot_provenance(st: dict) -> dict:
    """今の snapshot が、測る中身 st に対してどんな「変更前」か(compare が review.json に残し、approve が見る)。"""
    if not SNAPSHOT_META.exists():
        return {"present": False}
    meta = json.loads(SNAPSHOT_META.read_text(encoding="utf-8"))
    out = {"present": True, "timestamp": meta.get("timestamp"), "source": meta.get("source"),
           "head": meta.get("head"), "state_digest": meta.get("state_digest")}
    snap_state = meta.get("state")
    out["legacy"] = snap_state is None
    out["same_as_compare"] = snap_state == st if snap_state is not None else None
    out["code_same_as_compare"] = (rs.code_part(snap_state) == rs.code_part(st)) if snap_state is not None else None
    return out


def snapshot_rule(doc: dict, committed: dict) -> str | None:
    """approve してよいか(点検 B-4)。断るときは理由を返す。

    - 比較で測った中身がコミット済みの中身(本体と dev_safe の HEAD)と同じなら、HEAD を確かめただけなので通す。
    - snapshot が無いなら、全パネルが「変更前なし」として目視に回っているので通す。
    - 来歴の無い(古い形の)snapshot は、コードが HEAD と同じ比較だけ通す(コードを変えたのに変更前の中身が分からない)。
    - 変更前と同じ中身を測った比較は、変化 0 が何も示さないので断る。
    """
    st = doc["state"]
    if st == committed:
        return None
    snap = doc.get("snapshot") or {}
    if not snap.get("present"):
        return None
    if snap.get("legacy"):
        if rs.code_part(st) == rs.code_part(committed):
            return None
        return ("変更前(snapshot)がどのコードから撮られたか分かりません(古い形)。コードを変えた比較の承認には、"
                "来歴のある snapshot が要ります: python tools/visual_review.py snapshot(既定で HEAD から)→ compare")
    if snap.get("same_as_compare"):
        return ("変更前(snapshot)と同じ中身を測っています。変化 0 は何も示しません。HEAD から撮り直すか"
                "(python tools/visual_review.py snapshot)、測定器・基準を変えたときは変える前に撮った snapshot と比べてください")
    return None


# ---------------------------------------------------------------------------
# compare
# ---------------------------------------------------------------------------
def cmd_compare(engine: str = "csharp", source: str = "worktree", allow_external: bool = False) -> None:
    """3列比較パネル（元テクスチャ | 変更前 | 変更後）を全ケース分生成する。

    engine="csharp" のとき「変更後」を実 C# Harness の出力で描画する(出荷物を忠実に確認)。
    source="index" ならステージした中身のコードをビルドして測る(共有の作業ツリーにある他人の未コミットを入れない)。
    測り始めと終わりの中身(state)、比較の実行の id(run_id)、変更前の来歴を review.json に残す。approve と pre-commit は
    これを見る(時刻は見ない)。
    """
    info = _prepare_harness(source, allow_external, "compare")
    st0 = _measured_state(source, info) if info is not None else rs.state(source)
    run_id = uuid.uuid4().hex
    snap = snapshot_provenance(st0)
    if snap.get("legacy"):
        print("[compare] 注意: 変更前(snapshot)の来歴がありません(古い形か外の DLL)。何のコードから撮ったか分からないので、"
              "コードを変えた比較は承認できません(python tools/visual_review.py snapshot で HEAD から撮り直す)。")
    elif snap.get("same_as_compare"):
        print("[compare] 注意: 変更前(snapshot)と同じ中身を測っています。変化 0 は何も示しません"
              "(HEAD を確かめるだけなら承認できます)。")
    elif snap.get("present") and snap.get("code_same_as_compare") is False and snap.get("source") == "worktree":
        print("[compare] 注意: 変更前は作業ツリーから撮っています。共有の作業ツリーでは他人の未コミットが入りえます。")
    COMPARE_DIR.mkdir(parents=True, exist_ok=True)

    # snapshot_before は、そのケースを描くときに 1 枚ずつ読む。4K の全ケース(1 件 64MB × 100 超)と
    # 変更後の全出力を同時に抱えると 10GB を超え、Unity などと並べるとメモリが尽きた(2026-10-04)。
    before_paths: dict[str, Path] = {}
    meta_path = SNAPSHOT_BEFORE_DIR / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        for cid in meta["cases"]:
            p = SNAPSHOT_BEFORE_DIR / f"{cid}.png"
            if p.exists():
                before_paths[cid] = p
        print(f"[compare] 変更前スナップショット: {len(before_paths)} ケース")
    else:
        print("[compare] 警告: snapshot_before が見つかりません（変更前との比較なし）。")

    # 現在のアルゴリズムで全ケース実行(被写体ごとに受け取り、描いたら手放す)
    print(f"[compare] 現在のアルゴリズムで全ケースを実行中 (engine={engine}) …")

    generated: list[Path] = []
    to_review: list[Path] = []             # 目視が要るパネル（変化あり、または変更前なし）
    contract: dict[str, int] = {}          # マスク付きケース -> 契約違反画素数
    change_summary: dict[str, dict] = {}   # ケース -> 変化画素の内訳
    share_checked: list[str] = []          # 回帰が選択を使い回して測った出力と突き合わせたケース
    share_leaks: dict[str, int] = {}       # そのうち出力が違ったケース -> 違う画素数(選択キャッシュの鍵の漏れ)
    alpha_contract: dict[str, int] = {}    # α が入力と違う画素があったケース -> 画素数(B-7⑤)
    orig_thumb_src: np.ndarray | None = None   # 元テクスチャの縮小を作った入力(同じ被写体のケースで使い回す)
    orig_thumb_raw: np.ndarray | None = None
    for case_id, input_rgba, after_rgba, exclude, shared_diff in _iter_engine(engine):
        if shared_diff is not None:
            share_checked.append(case_id)
            if shared_diff:
                share_leaks[case_id] = shared_diff
        n_alpha = alpha_violations(input_rgba, after_rgba)
        if n_alpha:
            alpha_contract[case_id] = n_alpha
        bp = before_paths.get(case_id)
        before_rgba: np.ndarray | None = np.array(Image.open(bp).convert("RGBA")) if bp else None

        after_label = "固定設定 / 変更後 (自動調整なし)"
        if exclude is not None:
            # マスク契約: 除外指定した画素は RGBA が 1 ステップも変わってはならない。
            # 目視(512px サムネイル)では絶対に検出できないので、ここで機械的に数える。
            violated = int(np.any(input_rgba[exclude] != after_rgba[exclude], axis=-1).sum())
            contract[case_id] = violated
            after_label = (f"固定設定 + 模擬除外 (守った画素 {int(exclude.sum()):,} / "
                           f"違反 {violated:,})")

        # 縮小(4K から LANCZOS で 1 枚約 0.13 秒)は使い回す(性能の候補 T1。パネルの画素は同じ):
        # 元テクスチャは同じ被写体のケースで同じ配列なので被写体ごとに 1 回、変更前が変更後とビット一致なら
        # 変更後の縮小をそのまま使う。ラベルは写しに描く(使い回す縮小を書き換えない)。
        if input_rgba is not orig_thumb_src:
            orig_thumb_src, orig_thumb_raw = input_rgba, _make_thumb(input_rgba)
        orig_thumb = _add_label(
            orig_thumb_raw.copy(),
            "元テクスチャ (Original)",
            (30, 80, 160),
        )
        after_thumb_raw = _make_thumb(after_rgba)
        after_thumb = _add_label(
            after_thumb_raw.copy(),
            after_label,
            (30, 140, 60) if exclude is None or not contract[case_id] else (190, 30, 30),
        )

        if before_rgba is not None:
            same_as_before = before_rgba.shape == after_rgba.shape and np.array_equal(before_rgba, after_rgba)
            before_thumb = _add_label(
                (after_thumb_raw if same_as_before else _make_thumb(before_rgba)).copy(),
                "変更前 (Before / Old)",
                (160, 80, 30),
            )
            panel = _hstack_with_sep([orig_thumb, before_thumb, after_thumb])
        else:
            placeholder = np.full_like(orig_thumb, 64)
            placeholder = _add_label(placeholder, "変更前 (snapshot 未実行)", (80, 80, 80))
            panel = _hstack_with_sep([orig_thumb, placeholder, after_thumb])

        # ── 等倍クロップ行(N-11): 変化が最も密な領域を 1:1 で並べる ──
        # サムネイルは 4096px を 512px へ 1/8 に縮小するため、細い縁のノイズや
        # 質感の潰れは原理的に見えない。変化があるケースだけ 2 段目を足す。
        if before_rgba is not None:
            stats = _classify_change(input_rgba, before_rgba, after_rgba)
            change_summary[case_id] = stats
            if stats["changed"] > 0:
                changed_mask = _changed_px(before_rgba, after_rgba)
                cy, cx = _densest_change_window(changed_mask, CROP_SIZE)
                crop_row = _hstack_with_sep([
                    _add_label(_crop(input_rgba, cy, cx, CROP_SIZE),
                               f"等倍 元 @({cx},{cy})", (30, 80, 160)),
                    _add_label(_crop(before_rgba, cy, cx, CROP_SIZE),
                               "等倍 変更前", (160, 80, 30)),
                    _add_label(_crop(after_rgba, cy, cx, CROP_SIZE),
                               f"等倍 変更後 (最大差 {stats['max_delta']})", (30, 140, 60)),
                ])
                rows = [panel, crop_row]
                # 3 段目: 元に無い線が増えた所(変化の密な所とは別の場所のことが多い)
                ph = phantom_change(input_rgba, before_rgba, after_rgba)
                stats["phantom"] = {k: v for k, v in ph.items() if not k.startswith("_")}
                ph_row = phantom_crop_row(input_rgba, before_rgba, after_rgba, ph)
                if ph_row is not None:
                    rows.append(ph_row)
                del ph
                panel = _vstack_with_sep(rows)

        out_path = COMPARE_DIR / f"{case_id}_comparison.png"
        Image.fromarray(panel).save(out_path)
        generated.append(out_path)
        if before_rgba is None or change_summary[case_id]["changed"] > 0:
            to_review.append(out_path)
        print(f"  → {out_path.name}")

    # 古いファイルを掃除（今回生成していないもの）
    for old in COMPARE_DIR.glob("*_comparison.png"):
        if old not in generated:
            old.unlink()

    # マスク契約の検査結果を残す（approve が読んで違反時にブロックする）。
    MASK_CONTRACT_JSON.write_text(
        json.dumps({"checked_at": datetime.now().isoformat(), "run_id": run_id, "cases": contract},
                   indent=2, ensure_ascii=False),
        encoding="utf-8")

    CHANGE_SUMMARY_JSON.write_text(
        json.dumps({"generated_at": datetime.now().isoformat(), "run_id": run_id, "cases": change_summary,
                    # 選択を使い回さずに測った出力と、回帰が使い回して測った出力(キャッシュにあるもの)の突き合わせ
                    "selection_share_check": {"checked": len(share_checked), "leaks": share_leaks},
                    # 出力の α が入力と違う画素(全ケースを数える。載っているのは違反のあったケースだけ)
                    "alpha_contract": {"checked": len(generated), "violations": alpha_contract}},
                   indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"[compare] {len(generated)} ケース完了")
    print(f"\n[compare] {len(generated)} 枚の比較パネルを生成しました。")
    print(f"保存先: {COMPARE_DIR}\n")

    # 数値で当たりを付けてから目視する（N-11）。変化の大きい順に並べる。
    changed_cases = {k: v for k, v in change_summary.items() if v["changed"]}
    if change_summary:
        total = sum(v["changed"] for v in change_summary.values())
        print(f"[変化サマリ] 変化ケース {len(changed_cases)}/{len(change_summary)} / "
              f"変化画素 合計 {total:,}")
        if changed_cases:
            print(f"  {'case':32}{'変化':>12}{'うちα':>8}{'元へ復帰':>12}{'元から離れた':>14}{'最大差':>7}")
            for cid, v in sorted(changed_cases.items(),
                                 key=lambda kv: -kv[1]["changed"]):
                print(f"  {cid:32}{v['changed']:>12,}{v.get('alpha_changed', 0):>8,}{v['toward_original']:>12,}"
                      f"{v['away_from_original']:>14,}{v['max_delta']:>7}")
            print("  ※ 変化の大きいケースから等倍クロップ(パネル 2 段目)を確認すること。")
            _print_phantom_table(changed_cases)
        else:
            print("  すべてのケースが変更前とビット一致（出力に影響しない変更）。")
        print()
    print(f"[選択の使い回し] 使い回さずに測った出力を、回帰が使い回して測った出力(キャッシュにある "
          f"{len(share_checked)}/{len(generated)} ケース)と突き合わせ: 食い違い {len(share_leaks)} ケース")
    for cid, n in sorted(share_leaks.items()):
        print(f"  NG  {cid}: {n:,} px(選択キャッシュの鍵に漏れがある。approve はブロックされます)")
    print(f"[α] 出力の α が入力と違うケース {len(alpha_contract)}/{len(generated)}")
    for cid, n in sorted(alpha_contract.items()):
        print(f"  NG  {cid}: {n:,} px(再着色は色だけを変える。approve はブロックされます)")
    print()
    if contract:
        total = sum(contract.values())
        print(f"[マスク契約] {len(contract)} ケースを検査 / 違反 合計 {total:,} px")
        for cid, v in sorted(contract.items()):
            print(f"  {'NG' if v else 'ok'}  {cid}: 違反 {v:,} px")
        if total:
            print("  → 除外マスクで守った画素が書き換わっています。approve はブロックされます。")
        print()
    # 変更前とビット一致のケースは目視しなくてよい（2026-10-18 ユーザー決定）。
    # 変更前が無いケースは変化の有無が分からないので目視に回す。
    # 元に無い線が増えた順(次に変化の大きい順)に並べる。線の増えは IoU にも変化画素の数にも出ない。
    def _review_order(p: Path):
        st = change_summary.get(p.name[:-len("_comparison.png")], {})
        ph = st.get("phantom") or {}
        return (-(ph.get("after", 0) - ph.get("before", 0)), -st.get("changed", 0))
    to_review.sort(key=_review_order)
    print("=" * 60)
    print(f"【エージェントへ】変化があったケース {len(to_review)}/{len(generated)} 枚を "
          "Read ツールで 1 枚ずつ確認してください（変更前とビット一致のケースは読まなくてよい）。"
          "元に無い線が増えた順に並べています。3 段目があるパネルは、増えた線の所を必ず見ること。")
    print("=" * 60)
    for p in to_review:
        print(f"  {p}")
    print()
    # Windows コンソール(cp932)で例外にならないよう ASCII 記号を使う。
    print("各画像で以下を確認:")
    print("  [v] 元テクスチャにない質感・ノイズが追加されていないか")
    print("  [v] 変換対象エリアに変換漏れがないか")
    print("  [v] 関係ない部分（全体サムネイルで確認）が変わっていないか")
    print("  [v] 全体サムネイルで切り出し範囲外のエリアも確認したか")
    print()
    print("問題がなければ:")
    print('  python tools/visual_review.py approve --note "<目視した範囲と根拠>"')
    import workflow_review
    workflow_review.generate(measured={"source": source, "state": st0, "run_id": run_id, "snapshot": snap})


def _print_phantom_table(changed_cases: dict) -> None:
    """変化したケースの、出力にだけある線の組の増減(増えた順)。"""
    rows = [(cid, v["phantom"]) for cid, v in changed_cases.items() if v.get("phantom")]
    if not rows:
        return
    rows.sort(key=lambda kv: -(kv[1]["after"] - kv[1]["before"]))
    up = sum(1 for _, p in rows if p["after"] > p["before"])
    tb, ta = sum(p["before"] for _, p in rows), sum(p["after"] for _, p in rows)
    print(f"[元に無い線] 変化ケースの線の組 {tb:,} → {ta:,}(増えた {up} / {len(rows)} ケース)")
    print(f"  {'case':32}{'変更前':>10}{'変更後':>10}{'増えた線の画素':>14}")
    for cid, p in rows[:20]:
        print(f"  {cid:32}{p['before']:>10,}{p['after']:>10,}{p['new_px']:>14,}")
    print("  ※ 増えたケースはパネル 3 段目(赤紫の印)を確認すること。")


# ---------------------------------------------------------------------------
# approve
# ---------------------------------------------------------------------------
def _load_run_json(path: Path, run_id: str, what: str) -> dict:
    """compare が書いた検査結果(mask_contract.json・change_summary.json)を、同じ比較の実行のものとして読む。"""
    if not path.exists():
        print(f"[approve] エラー: {what}の結果がありません。compare を実行してください:")
        print("  python tools/visual_review.py compare")
        sys.exit(1)
    doc = json.loads(path.read_text(encoding="utf-8"))
    if doc.get("run_id") != run_id:
        print(f"[approve] エラー: {what}の結果が、ワークフローと同じ比較(compare の 1 回)のものではありません。"
              "compare をやり直してください。")
        sys.exit(1)
    return doc


def cmd_approve(note: str | None = None) -> None:
    """視覚レビューの承認を、比較で測った中身(state)と比較の実行(run_id)に結び付けて書く。

    比較の後に測るものが変わっていれば断る(時刻ではなく中身で見る。点検 B-1・B-7①)。マスク契約・α・選択の
    使い回しの食い違いは目視では分からないので、compare が数えた結果を承認の前提にする。変更前と同じ中身を
    測った比較(変化 0 が何も示さない)は断る(点検 B-4。snapshot_rule)。
    """
    import workflow_review
    # 固定設定だけの承認では自動調整経路を検証できない。別条件の比較も必須。測った中身が今と同じことも確かめる。
    doc = workflow_review.validate_review()
    if not note or not note.strip():
        raise ValueError("--note に固定設定とワークフロー全ケースの目視範囲を記録してください")
    run_id = doc["run_id"]
    panels = sorted(COMPARE_DIR.glob("*_comparison.png"))
    if not panels:
        print("[approve] エラー: 比較パネルがありません。先に compare を実行してください:")
        print("  python tools/visual_review.py compare")
        sys.exit(1)

    # マスク契約(除外指定した画素は絶対に変わらない)は目視では検出できないので、
    # compare が数えた違反を承認の前提条件にする。人間の注意力に委ねない項目。
    contract = _load_run_json(MASK_CONTRACT_JSON, run_id, "マスク契約の検査")
    cases = contract.get("cases") or {}
    if not cases:
        print("[approve] エラー: マスク付きケースが 1 件も検査されていません"
              "(MASKED_SUBJECT_CASES が空 or compare が古い)。")
        sys.exit(1)
    violations = {k: v for k, v in cases.items() if v}
    if violations:
        print("[approve] エラー: 除外マスクで守った画素が書き換わっています(マスク契約違反):")
        for cid, v in sorted(violations.items()):
            print(f"    {cid}: {v:,} px")
        print("  これは目視では見えない退行です。承認せず原因を直してください。")
        sys.exit(1)
    # 選択の使い回し(回帰とプレビュー)と使い回さない経路(書き出し)の食い違いも、目視では分からない(点検 D-3)。
    summary = _load_run_json(CHANGE_SUMMARY_JSON, run_id, "変化の内訳")
    share = summary.get("selection_share_check")
    if share is None:
        print("[approve] エラー: 選択の使い回しの突き合わせの結果がありません。compare を実行してください。")
        sys.exit(1)
    if share["leaks"]:
        print("[approve] エラー: 選択を使い回した出力と使い回さない出力が食い違うケースがあります(選択キャッシュの鍵の漏れ):")
        for cid, v in sorted(share["leaks"].items()):
            print(f"    {cid}: {v:,} px")
        sys.exit(1)
    alpha = summary.get("alpha_contract")
    if alpha is None:
        print("[approve] エラー: α の検査結果がありません。compare を実行してください。")
        sys.exit(1)
    if alpha["violations"]:
        print("[approve] エラー: 出力の α が入力の α と違うケースがあります(再着色は色だけを変える):")
        for cid, v in sorted(alpha["violations"].items()):
            print(f"    {cid}: {v:,} px")
        sys.exit(1)
    # 変更前が何から撮られたか(点検 B-4)
    refuse = snapshot_rule(doc, rs.committed_state())
    if refuse:
        print(f"[approve] エラー: {refuse}")
        sys.exit(1)
    snap = doc.get("snapshot") or {}
    if snap.get("legacy"):
        print("[approve] 注意: 変更前(snapshot)の来歴がありません。コードは HEAD と同じなので承認しますが、"
              "測定器・基準の変更は、変える前に撮った snapshot との比較でしか確かめられません。")

    REVIEW_DIR.mkdir(parents=True, exist_ok=True)
    # 既定の注記は「何をどこまで見たか」を書かない定型文だった。ツールが検証できない主張
    # （全パネル確認済み）を勝手に記録すると、承認記録そのものが信用できなくなるので、
    # 実際に見た範囲と根拠を --note で残せるようにしてある。
    marker = {
        "approved_at": datetime.now().isoformat(),
        "note": note,
        # 承認は時刻でなく、この比較の実行で測った中身に結び付く(pre-commit はこれとステージした中身を突き合わせる)
        "run_id": run_id,
        "state_source": doc["state_source"],
        "state": doc["state"],
        "panels": len(panels),
        "mask_contract_cases": len(cases),
        "mask_contract_violations": 0,
        "selection_share_checked": share["checked"],
        "alpha_checked": alpha["checked"],
    }
    APPROVED_JSON.write_text(
        json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    workflow_review.approve(note)
    print(f"[approve] マーカーを書き込みました: {APPROVED_JSON} (panels={len(panels)}, 測り元={doc['state_source']})")
    print("  コミットできるのは、範囲のファイルが「HEAD と同じ」か「この比較で測った中身と同じ」ものだけです"
          "(確かめるなら python tools/visual_review.py check --index)。")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=("snapshot", "compare", "approve", "check"))
    ap.add_argument("--engine", default="csharp", choices=("python", "csharp"),
                    help="csharp のみ(Python 再実装は引退済み)")
    src = ap.add_mutually_exclusive_group()
    src.add_argument("--index", action="store_true",
                     help="compare / check: 作業ツリーでなく、ステージした中身のコードで測る・確かめる")
    src.add_argument("--worktree", action="store_true", help="snapshot: HEAD でなく作業ツリーのコードから撮る")
    src.add_argument("--rev", default=None, help="snapshot: このコミットのコードから撮る(既定 HEAD)")
    ap.add_argument("--allow-external-dll", action="store_true",
                    help="IROCA_HARNESS_DLL の DLL で測る(見るだけ。来歴が無いので承認できない)")
    ap.add_argument("--note", default=None, help="approve: 実際に目視した範囲と根拠")
    args = ap.parse_args()
    if args.engine == "python":
        _run_all_cases()
    if args.command == "snapshot":
        if args.index:
            ap.error("snapshot に --index は使えません(--worktree か --rev)")
        source = "worktree" if args.worktree else f"rev:{args.rev or 'HEAD'}"
        cmd_snapshot(args.engine, source, args.allow_external_dll)
    elif args.command == "compare":
        if args.worktree or args.rev:
            ap.error("compare は作業ツリー(既定)か --index で測ります")
        cmd_compare(args.engine, "index" if args.index else "worktree", args.allow_external_dll)
    elif args.command == "approve":
        cmd_approve(args.note)
    else:
        import workflow_review
        problems = workflow_review.check(index=args.index)
        if problems:
            print("[check] 承認と中身が合いません(compare → 目視 → approve をやり直す):")
            for p in problems:
                print(f"  {p}")
            sys.exit(1)
        print(f"[check] 承認は有効で、{'ステージした中身' if args.index else '作業ツリー'}をコミットしてもフックを通ります。")


if __name__ == "__main__":
    main()
