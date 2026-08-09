"""改善サイクル用視覚レビュースクリプト。

使い方:
    python tools/visual_review.py snapshot    # 変更前スナップショット保存
    python tools/visual_review.py compare     # 比較パネル生成(Python 出力)
    python tools/visual_review.py compare --engine csharp  # 実 C# Harness 出力で比較
    python tools/visual_review.py approve     # 確認完了マーカー書き込み

  --engine csharp は出荷される実 C# 出力を描画する(Python 経路と乖離する gray mode 等を
  人間レビューでも見られるようにする)。dotnet/Unity DLL が必要。

改善サイクルでの手順:
    1. 変更を加える前に `snapshot` を実行（旧アルゴリズム出力を保存）
    2. 変更を加えてテストを実行
    3. `compare` を実行 → 生成された PNG を Read ツールで1枚ずつ確認
         - 元テクスチャにない質感・ノイズが追加されていないか
         - 変換対象エリアに変換漏れがないか
         - 関係ない部分が変わっていないか（全体サムネイル必須）
         - テクスチャを切り出して作業した場合も、全体サムネイルで切り出し外を確認
       `+masked` 付きのケースは除外マスクを与えて走らせたもので、compare が
       「守った画素が 1 つも変わっていないか」を機械で数える（目視では見えないため）。
    4. 問題がなければ `approve` を実行（マスク契約違反があると approve は失敗する）
    5. コミット（pre-commit フックが approve を確認する）
"""
from __future__ import annotations

import json
import sys
from datetime import datetime
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

# --- sys.path 設定 ---
_ROOT = Path(__file__).resolve().parent.parent          # リポジトリルート

sys.path.insert(0, str(Path(__file__).resolve().parent))
from harness_scope import harness_source_files          # noqa: E402
_DEV_SAFE = _ROOT / "dev_safe"
_TESTS = _DEV_SAFE / "Tests"
for _p in [str(_DEV_SAFE), str(_TESTS)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

from regression.fixtures import (
    SUBJECT_REGISTRY,
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

THUMB_HEIGHT = 512  # 比較パネルの列高さ（px）

# 除外マスク付きレビューケース（subject_id -> 使うケース数）。
#
# 「ユーザーが手で守った画素は絶対に変わらない」は「プレビュー＝出力」と並ぶ中核保証だが、
# 2026-08-07 まで視覚レビューの全ケースが np.zeros(= マスク無し)でハーネスを呼んでおり、
# **出荷ゲートにマスク経路が 1 件も無かった**。実際 N-1(CleanAchromaFringe が除外マスクを
# 参照せず保護画素を上書きする契約違反)はこのゲートを素通りし、修正後も全ケースがビット一致で
# 「何も検証できていない」状態だった(レビュー 2026-08-06 の N-12)。
#
# 被写体は経路の違いで選ぶ:
#   bandana       — 有彩サンプルの通常マッチ経路
#   feina-goggles — 暗い無彩サンプル = グレーモード + 無彩フチ消し(N-1 の現場)
# 色数は実行コスト(1 ケース = dotnet 1 プロセス × フル解像度)とのバランスで先頭 2 色に絞る。
MASKED_SUBJECT_CASES = {
    "bandana": 2,
    "feina-goggles": 2,
}

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


def _run_all_cases_csharp() -> dict[str, tuple[np.ndarray, np.ndarray, np.ndarray | None]]:
    """全ケースを **実 C# Harness** で実行。{case_id: (input_rgba, output_rgba, exclude_or_None)}

    視覚レビューが Python ではなく実際に出荷される C# 出力を見られるようにする
    (project_visual_review_washoff_blind の「視覚レビューが C# に盲目」を解消)。
    dotnet/Unity DLL が無ければ RuntimeError で中断する。

    末尾で MASKED_SUBJECT_CASES の被写体について除外マスク付きの変種も回す
    (case_id は `<元の case_id>+masked`)。exclude が None でないケースは、compare が
    「マスク画素が 1 つも変わっていないか」を機械で検査する。
    """
    from regression import headless_io as hio

    csproj = _ROOT / "scripts" / "headless-run" / "Harness.csproj"
    dll = _ROOT / "scripts" / "headless-run" / "bin" / "Release" / "IrocaHeadless.dll"
    if hio.run(["dotnet", "--version"]).returncode != 0:
        raise RuntimeError("dotnet が利用できません(--engine csharp は使えません)")
    build = hio.run(["dotnet", "build", str(csproj), "-c", "Release", "-nologo"])
    if build.returncode != 0 or not dll.exists():
        raise RuntimeError(f"Harness ビルド失敗(Unity DLL 不在?):\n{build.stdout[-800:]}")
    CSHARP_WORK.mkdir(parents=True, exist_ok=True)

    settings = default_settings()
    settings_cfg = {
        "edgeFeather": settings.edge_feather,
        "antiAliasCleanup": settings.anti_alias_cleanup,
        "holeFillPasses": settings.hole_fill_passes,
        "holeFillMinNeighbors": settings.hole_fill_min_neighbors,
        "relaxedSatMin": settings.relaxed_sat_min,
        "relaxedSatRamp": settings.relaxed_sat_ramp,
        "useDecontamination": settings.use_decontamination,
        "decontaminationRadius": settings.decontamination_radius,
    }
    def _run(case, tag: str, in_raw: Path, mask_raw: Path) -> np.ndarray:
        zone = make_zone(case)
        zones_json = CSHARP_WORK / f"{tag}_zones.json"
        # ここで書かないフィールド(useFloodFill / autoRecolorAnchor / highlightRecovery /
        # shadowDesaturation ほか)は **意図的に省略** して製品既定へフォールバックさせる。
        # 出荷ゲートの目視は「出荷される設定の出力」を見るべきなので、ケース固有の選択
        # パラメータ(下の 7 つ)だけをケースから取り、残りは製品既定に任せる。
        #
        # この省略が安全なのは、zones JSON の既定値が Code/Core/ZonesJsonDefaults.cs に
        # 単一ソース化され、ハーネス DTO と製品 DTO の一致を
        # dev_safe/Tests/regression/test_zones_schema_parity.py が機械検査しているから。
        # 2026-08-06 以前はハーネス既定が製品と乖離しており(useFloodFill 製品 true /
        # ハーネス false)、このパネルは製品でも回帰テスト設定でもない混成を映していた。
        #
        # 2026-08-07 以降、fixtures.ZoneSpec の選択挙動も製品既定へ揃えた(N-10)ため、
        # **このパネルと回帰テストの IoU は同一設定を指す**(以前は別物だった)。
        hio.write_zones_json(zones_json, [{
            "name": zone.name,
            "sample": list(case.sample_rgb),
            "target": list(case.target_rgb),
            "tolerance": zone.tolerance,
            "valueBlend": zone.value_blend,
            "edgeSoftness": zone.edge_softness,
            "saturationStrictness": zone.saturation_strictness,
        }], settings_cfg)
        out_raw = CSHARP_WORK / f"{tag}_out.raw"
        r = hio.run(["dotnet", str(dll), str(in_raw), str(mask_raw),
                     str(out_raw), "--zones", str(zones_json)])
        if r.returncode != 0:
            raise RuntimeError(f"Harness 実行失敗 {tag}: {r.stderr}\n{r.stdout}")
        return hio.read_raw_rgba(out_raw)

    results: dict[str, tuple[np.ndarray, np.ndarray, np.ndarray | None]] = {}
    for subject in SUBJECT_REGISTRY.values():
        rgba, gt_mask = load_subject_inputs(subject)
        in_raw = CSHARP_WORK / f"{subject.subject_id}_in.raw"
        mask_raw = CSHARP_WORK / f"{subject.subject_id}_mask.raw"
        hio.write_raw(in_raw, rgba)
        hio.write_raw(mask_raw, np.zeros(rgba.shape[:2], np.uint8))  # 全画素処理
        cases = load_cases(subject)
        for case in cases:
            results[case.case_id] = (rgba, _run(case, case.case_id, in_raw, mask_raw), None)

        # ── 除外マスク付きの変種(N-12: ゲートにマスク経路を通す) ──
        n_masked = MASKED_SUBJECT_CASES.get(subject.subject_id, 0)
        if n_masked <= 0:
            continue
        if gt_mask is None:
            raise RuntimeError(
                f"{subject.subject_id}: マスクケースには single_mask 戦略の GT が要ります")
        exclude = _build_review_mask(gt_mask)
        excl_raw = CSHARP_WORK / f"{subject.subject_id}_excl.raw"
        hio.write_raw(excl_raw, np.ascontiguousarray(exclude))
        for case in cases[:n_masked]:
            tag = f"{case.case_id}{MASKED_SUFFIX}"
            results[tag] = (rgba, _run(case, tag, in_raw, excl_raw), exclude.astype(bool))
    return results


def _run_engine(engine: str) -> dict[str, tuple[np.ndarray, np.ndarray, np.ndarray | None]]:
    if engine == "csharp":
        return _run_all_cases_csharp()
    return _run_all_cases()


# ---------------------------------------------------------------------------
# 変化の切り分け（N-11: 512px サムネイルだけでは 4K の変化を判定できない）
#
# 「元テクスチャにないノイズが出ていないか」を 512px 3 列パネルで見るのは、2K/4K では
# 物理的に無理がある。実際 2026-08-06〜07 の 2 回のレビューとも、判定のために
# 「変化画素の分類」と「等倍クロップ」を別途スクリプトで書き起こす必要があった。
# ここに組み込んで、数値で当たりを付けてから目視できるようにする。
# ---------------------------------------------------------------------------
CROP_SIZE = 384  # 等倍クロップの一辺（px）


def _classify_change(orig: np.ndarray, before: np.ndarray, after: np.ndarray) -> dict:
    """変化画素を「元へ復帰 / 元から離れた」に分類する。

    元テクスチャからの L1 距離が縮んだ画素は **誤爆着色の除去**、伸びた画素は
    **新規/強化された着色**。総変化画素数だけでは、この 2 つが打ち消し合って
    「大きく変わった」以上のことが分からない。
    """
    o = orig[..., :3].astype(np.int16)
    b = before[..., :3].astype(np.int16)
    a = after[..., :3].astype(np.int16)
    ch = np.any(b != a, axis=-1)
    n = int(ch.sum())
    if n == 0:
        return {"changed": 0, "toward_original": 0, "away_from_original": 0, "max_delta": 0}
    db = np.abs(b - o).sum(-1)[ch]
    da = np.abs(a - o).sum(-1)[ch]
    return {
        "changed": n,
        "toward_original": int((da < db).sum()),
        "away_from_original": int((da > db).sum()),
        "max_delta": int(np.abs(b - a).max()),
    }


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
# snapshot
# ---------------------------------------------------------------------------
def cmd_snapshot(engine: str = "python") -> None:
    """現在のアルゴリズム出力を全ケース分 snapshot_before に保存する。"""
    SNAPSHOT_BEFORE_DIR.mkdir(parents=True, exist_ok=True)
    print(f"[snapshot] 全ケースをスナップショット中 (engine={engine}) …")
    cases = _run_engine(engine)
    for case_id, (_, output_rgba, _exclude) in cases.items():
        Image.fromarray(output_rgba).save(SNAPSHOT_BEFORE_DIR / f"{case_id}.png")
        print(f"  保存: {case_id}.png")
    meta = {
        "timestamp": datetime.now().isoformat(),
        "cases": sorted(cases.keys()),
    }
    (SNAPSHOT_BEFORE_DIR / "meta.json").write_text(
        json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print(f"[snapshot] {len(cases)} ケース完了 → {SNAPSHOT_BEFORE_DIR}")


# ---------------------------------------------------------------------------
# compare
# ---------------------------------------------------------------------------
def cmd_compare(engine: str = "python") -> None:
    """3列比較パネル（元テクスチャ | 変更前 | 変更後）を全ケース分生成する。

    engine="csharp" のとき「変更後」を実 C# Harness の出力で描画する(出荷物を忠実に確認)。
    """
    COMPARE_DIR.mkdir(parents=True, exist_ok=True)

    # snapshot_before を読み込む
    before_images: dict[str, np.ndarray] = {}
    meta_path = SNAPSHOT_BEFORE_DIR / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        for cid in meta["cases"]:
            p = SNAPSHOT_BEFORE_DIR / f"{cid}.png"
            if p.exists():
                before_images[cid] = np.array(Image.open(p).convert("RGBA"))
        print(f"[compare] 変更前スナップショット: {len(before_images)} ケース読み込み完了")
    else:
        print("[compare] 警告: snapshot_before が見つかりません（変更前との比較なし）。")

    # 現在のアルゴリズムで全ケース実行
    print(f"[compare] 現在のアルゴリズムで全ケースを実行中 (engine={engine}) …")
    current = _run_engine(engine)
    print(f"[compare] {len(current)} ケース完了")

    generated: list[Path] = []
    contract: dict[str, int] = {}          # マスク付きケース -> 契約違反画素数
    change_summary: dict[str, dict] = {}   # ケース -> 変化画素の内訳
    for case_id, (input_rgba, after_rgba, exclude) in sorted(current.items()):
        before_rgba: np.ndarray | None = before_images.get(case_id)

        after_label = "変更後 (After / New)"
        if exclude is not None:
            # マスク契約: 除外指定した画素は RGBA が 1 ステップも変わってはならない。
            # 目視(512px サムネイル)では絶対に検出できないので、ここで機械的に数える。
            violated = int(np.any(input_rgba[exclude] != after_rgba[exclude], axis=-1).sum())
            contract[case_id] = violated
            after_label = (f"変更後 + 除外マスク (守った画素 {int(exclude.sum()):,} / "
                           f"違反 {violated:,})")

        orig_thumb = _add_label(
            _make_thumb(input_rgba),
            "元テクスチャ (Original)",
            (30, 80, 160),
        )
        after_thumb = _add_label(
            _make_thumb(after_rgba),
            after_label,
            (30, 140, 60) if exclude is None or not contract[case_id] else (190, 30, 30),
        )

        if before_rgba is not None:
            before_thumb = _add_label(
                _make_thumb(before_rgba),
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
                changed_mask = np.any(
                    before_rgba[..., :3].astype(np.int16)
                    != after_rgba[..., :3].astype(np.int16), axis=-1)
                cy, cx = _densest_change_window(changed_mask, CROP_SIZE)
                crop_row = _hstack_with_sep([
                    _add_label(_crop(input_rgba, cy, cx, CROP_SIZE),
                               f"等倍 元 @({cx},{cy})", (30, 80, 160)),
                    _add_label(_crop(before_rgba, cy, cx, CROP_SIZE),
                               "等倍 変更前", (160, 80, 30)),
                    _add_label(_crop(after_rgba, cy, cx, CROP_SIZE),
                               f"等倍 変更後 (最大差 {stats['max_delta']})", (30, 140, 60)),
                ])
                panel = _vstack_with_sep([panel, crop_row])

        out_path = COMPARE_DIR / f"{case_id}_comparison.png"
        Image.fromarray(panel).save(out_path)
        generated.append(out_path)
        print(f"  → {out_path.name}")

    # 古いファイルを掃除（今回生成していないもの）
    for old in COMPARE_DIR.glob("*_comparison.png"):
        if old not in generated:
            old.unlink()

    # マスク契約の検査結果を残す（approve が読んで違反時にブロックする）。
    MASK_CONTRACT_JSON.write_text(
        json.dumps({"checked_at": datetime.now().isoformat(), "cases": contract},
                   indent=2, ensure_ascii=False),
        encoding="utf-8")

    CHANGE_SUMMARY_JSON.write_text(
        json.dumps({"generated_at": datetime.now().isoformat(), "cases": change_summary},
                   indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"\n[compare] {len(generated)} 枚の比較パネルを生成しました。")
    print(f"保存先: {COMPARE_DIR}\n")

    # 数値で当たりを付けてから目視する（N-11）。変化の大きい順に並べる。
    changed_cases = {k: v for k, v in change_summary.items() if v["changed"]}
    if change_summary:
        total = sum(v["changed"] for v in change_summary.values())
        print(f"[変化サマリ] 変化ケース {len(changed_cases)}/{len(change_summary)} / "
              f"変化画素 合計 {total:,}")
        if changed_cases:
            print(f"  {'case':32}{'変化':>12}{'元へ復帰':>12}{'元から離れた':>14}{'最大差':>7}")
            for cid, v in sorted(changed_cases.items(),
                                 key=lambda kv: -kv[1]["changed"]):
                print(f"  {cid:32}{v['changed']:>12,}{v['toward_original']:>12,}"
                      f"{v['away_from_original']:>14,}{v['max_delta']:>7}")
            print("  ※ 変化の大きいケースから等倍クロップ(パネル 2 段目)を確認すること。")
        else:
            print("  すべてのケースが変更前とビット一致（出力に影響しない変更）。")
        print()
    if contract:
        total = sum(contract.values())
        print(f"[マスク契約] {len(contract)} ケースを検査 / 違反 合計 {total:,} px")
        for cid, v in sorted(contract.items()):
            print(f"  {'NG' if v else 'ok'}  {cid}: 違反 {v:,} px")
        if total:
            print("  → 除外マスクで守った画素が書き換わっています。approve はブロックされます。")
        print()
    print("=" * 60)
    print("【エージェントへ】以下の PNG を Read ツールで 1 枚ずつ確認してください。")
    print("=" * 60)
    for p in generated:
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
    print("  python tools/visual_review.py approve")


# ---------------------------------------------------------------------------
# approve
# ---------------------------------------------------------------------------
def _newest_code_mtime() -> float:
    """ハーネスがコンパイルする製品ソースの最新 mtime。

    pre-commit フック(tools/check_visual_review.py)が approve を要求する範囲と同じ
    定義(tools/harness_scope.py)を共有する。ここがズレると「フックは通るのに
    approve が陳腐と言う」のような食い違いが起きる。
    """
    newest = 0.0
    for p in harness_source_files():
        newest = max(newest, p.stat().st_mtime)
    return newest


def cmd_approve(note: str | None = None) -> None:
    """視覚レビュー確認完了マーカーを書き込む。

    空承認ガード(監査 4-1): compare パネルが存在し、かつ最新の Code/ ソース変更より
    新しいことを検証する。パネルより後にコードを変えた場合は compare からやり直し。
    """
    panels = sorted(COMPARE_DIR.glob("*_comparison.png"))
    if not panels:
        print("[approve] エラー: 比較パネルがありません。先に compare を実行してください:")
        print("  python tools/visual_review.py compare --engine csharp")
        sys.exit(1)
    oldest_panel = min(p.stat().st_mtime for p in panels)
    code_mtime = _newest_code_mtime()
    if code_mtime > oldest_panel:
        print("[approve] エラー: 比較パネルより新しい Code/ 変更があります(パネルが陳腐)。")
        print("  compare を再実行してから approve してください。")
        sys.exit(1)

    # マスク契約(除外指定した画素は絶対に変わらない)は目視では検出できないので、
    # compare が数えた違反を承認の前提条件にする。人間の注意力に委ねない唯一の項目。
    if not MASK_CONTRACT_JSON.exists():
        print("[approve] エラー: マスク契約の検査結果がありません。compare を実行してください:")
        print("  python tools/visual_review.py compare --engine csharp")
        sys.exit(1)
    contract = json.loads(MASK_CONTRACT_JSON.read_text(encoding="utf-8"))
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
    REVIEW_DIR.mkdir(parents=True, exist_ok=True)
    # 既定の注記は「何をどこまで見たか」を書かない定型文だった。ツールが検証できない主張
    # （全パネル確認済み）を勝手に記録すると、承認記録そのものが信用できなくなるので、
    # 実際に見た範囲と根拠を --note で残せるようにしてある。
    marker = {
        "approved_at": datetime.now().isoformat(),
        "note": note or "(注記なし: --note で確認範囲と根拠を記録すること)",
        "panels": len(panels),
        "mask_contract_cases": len(cases),
        "mask_contract_violations": 0,
    }
    APPROVED_JSON.write_text(
        json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print(f"[approve] マーカーを書き込みました: {APPROVED_JSON} (panels={len(panels)})")
    print("  コミット可能です。")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def _parse_engine(argv: list[str]) -> str:
    """argv から --engine python|csharp を取り出す(既定 csharp = 出荷される実 C#)。

    Python 再実装(vacc_python)は引退済みのため、python 指定は明示エラーになる。
    """
    for i, a in enumerate(argv):
        if a == "--engine" and i + 1 < len(argv):
            eng = argv[i + 1]
            if eng not in ("python", "csharp"):
                print(f"不明な engine: {eng!r} (python|csharp)")
                sys.exit(1)
            return eng
        if a.startswith("--engine="):
            eng = a.split("=", 1)[1]
            if eng not in ("python", "csharp"):
                print(f"不明な engine: {eng!r} (python|csharp)")
                sys.exit(1)
            return eng
    return "csharp"


def _parse_note(argv: list[str]) -> str | None:
    """argv から --note <text> / --note=<text> を取り出す(approve 用)。"""
    for i, a in enumerate(argv):
        if a == "--note" and i + 1 < len(argv):
            return argv[i + 1]
        if a.startswith("--note="):
            return a.split("=", 1)[1]
    return None


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    cmd = sys.argv[1]
    engine = _parse_engine(sys.argv[2:])
    if cmd == "snapshot":
        cmd_snapshot(engine)
    elif cmd == "compare":
        cmd_compare(engine)
    elif cmd == "approve":
        cmd_approve(_parse_note(sys.argv[2:]))
    else:
        print(f"不明なコマンド: {cmd!r}")
        print("使い方: python tools/visual_review.py [snapshot|compare|approve] [--engine python|csharp]")
        sys.exit(1)


if __name__ == "__main__":
    main()
