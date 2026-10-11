"""GitHub Pages に置くサイト（VPM listing とオンラインマニュアル）を組み立てる。

listing（docs/index.json）には、配信のたびに NDMF の公式 listing から、いろかが対応する
範囲の安定版を写して足す。VCC / ALCOM で、いろかのリポジトリだけを追加した人でも
NDMF を選べるようにするため。写す項目は公式とまったく同じ（URL・zipSHA256 とも）なので、
NDMF の公式リポジトリも追加している環境でもぶつからない。

対応する範囲は Code/NdmfIntegration/Iroca.NdmfIntegration.asmdef の versionDefines
（nadena.dev.ndmf の expression）から読む。範囲を広げれば、写す範囲も一緒に変わる。

いろかのリリースは頻繁ではないので、写した項目はリポジトリの docs/index.json へは
書き戻さず、.github/workflows/pages.yml が毎日この組み立てと配信をやり直す。
公式 listing を読めなかったときは失敗で終わる（配信されず、前回の配信がそのまま残る）。

    python scripts/pages/build_site.py --out _site
    python scripts/pages/build_site.py --out _site --ndmf-listing path/to/vpm.json   # 手元の写しで試す
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import re
import shutil
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LISTING = ROOT / "docs" / "index.json"
MANUAL_BUILD = ROOT / "docs" / "manual" / "build_manual.py"
ASMDEF = ROOT / "Code" / "NdmfIntegration" / "Iroca.NdmfIntegration.asmdef"

NDMF_ID = "nadena.dev.ndmf"
NDMF_LISTING_URL = "https://vpm.nadena.dev/vpm.json"

SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")
SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")


def parse_version(text: str) -> tuple[tuple[int, int, int], str | None]:
    m = SEMVER.match(text.strip())
    if not m:
        raise ValueError(f"バージョンとして読めません: {text!r}")
    return (int(m[1]), int(m[2]), int(m[3])), m[4]


def parse_range(expr: str):
    """Unity の versionDefines の式（"[1.8.0,2.0.0)" や "1.8.0" など）を (下限, 下限を含む, 上限, 上限を含む) にする。"""
    expr = expr.strip()
    if expr and expr[0] in "[(":
        if expr[-1] not in ")]" or "," not in expr:
            raise ValueError(f"範囲の式として読めません: {expr!r}")
        lo_text, hi_text = (s.strip() for s in expr[1:-1].split(",", 1))
        lo = parse_version(lo_text)[0] if lo_text else None
        hi = parse_version(hi_text)[0] if hi_text else None
        return lo, expr[0] == "[", hi, expr[-1] == "]"
    # 範囲記号の無い式は「その版以上」
    return parse_version(expr)[0], True, None, False


def in_range(ver: tuple[int, int, int], rng) -> bool:
    lo, lo_inc, hi, hi_inc = rng
    if lo is not None and (ver < lo or (ver == lo and not lo_inc)):
        return False
    if hi is not None and (ver > hi or (ver == hi and not hi_inc)):
        return False
    return True


def ndmf_range_from_asmdef() -> tuple[str, tuple]:
    data = json.loads(ASMDEF.read_text(encoding="utf-8-sig"))
    for vd in data.get("versionDefines", []):
        if vd.get("name") == NDMF_ID:
            return vd["expression"], parse_range(vd["expression"])
    raise SystemExit(f"{ASMDEF.name} の versionDefines に {NDMF_ID} がありません")


def load_ndmf_listing(path: str | None) -> dict:
    if path:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    req = urllib.request.Request(NDMF_LISTING_URL, headers={"User-Agent": "Iroca-pages-build"})
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.loads(res.read().decode("utf-8-sig"))


def pick_ndmf_versions(listing: dict, rng) -> dict:
    versions = listing.get("packages", {}).get(NDMF_ID, {}).get("versions", {})
    picked = {}
    for key, entry in versions.items():
        try:
            ver, pre = parse_version(key)
        except ValueError:
            continue
        if pre is not None or not in_range(ver, rng):
            continue  # プレリリースと範囲外は載せない
        if entry.get("name") != NDMF_ID or entry.get("version") != key:
            raise SystemExit(f"NDMF {key} の項目の name / version が食い違っています")
        if not str(entry.get("url", "")).startswith("https://"):
            raise SystemExit(f"NDMF {key} の url が https ではありません")
        if not SHA256.match(str(entry.get("zipSHA256", ""))):
            raise SystemExit(f"NDMF {key} に zipSHA256 がありません")
        picked[key] = copy.deepcopy(entry)
    if not picked:
        raise SystemExit("載せる NDMF の版が 1 つもありません（公式 listing の形が変わった可能性）")
    return dict(sorted(picked.items(), key=lambda kv: parse_version(kv[0])[0]))


def build_manual() -> bytes:
    """docs/manual/build_manual.py で 1 枚の HTML を作る（標準ライブラリだけで動く）。"""
    spec = importlib.util.spec_from_file_location("build_manual", MANUAL_BUILD)
    mod = importlib.util.module_from_spec(spec)
    sys.modules["build_manual"] = mod
    spec.loader.exec_module(mod)
    return mod.build()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, default=ROOT / "_site", help="出力先（中身は作り直す）")
    ap.add_argument("--ndmf-listing", help="NDMF の listing を URL でなく手元のファイルから読む（試験用）")
    a = ap.parse_args()

    expr, rng = ndmf_range_from_asmdef()
    listing = json.loads(LISTING.read_text(encoding="utf-8-sig"))
    try:
        ndmf = pick_ndmf_versions(load_ndmf_listing(a.ndmf_listing), rng)
    except (OSError, ValueError) as e:
        raise SystemExit(f"NDMF の listing を読めませんでした: {e}")
    listing.setdefault("packages", {})[NDMF_ID] = {"versions": ndmf}

    out: Path = a.out
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    (out / "index.json").write_text(json.dumps(listing, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # マニュアルは原稿から作った 1 枚の HTML だけを載せる（パッケージには入れず、ここだけで配る）
    (out / "manual").mkdir()
    (out / "manual" / "index.html").write_bytes(build_manual())

    keys = list(ndmf)
    print(f"NDMF {expr}: {len(keys)} 版を載せました（{keys[0]} 〜 {keys[-1]}）")
    print(f"出力: {out}")


if __name__ == "__main__":
    sys.exit(main())
