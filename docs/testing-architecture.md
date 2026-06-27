# テストアーキテクチャ — 製品 C# を直接測る

> 2026-06-27 更新。アーキテクチャレビュー §4.2(D)「C#↔Python 二重実装ドリフト」への恒久対応。

## 原則：C# が唯一の正、テストは実 C# を直接測る

このプロジェクトの再着色アルゴリズムの**唯一の正は製品 C#**（`Code/Core/PixelProcessor.cs` ほか）。
回帰テストは、Unity を起動せずに**実 C# をそのまま実行する headless ハーネス**経由で出力を得て、
GT（Ground Truth）と突き合わせる。**アルゴリズムの Python 再実装はもう存在しない**（`dev_safe/vacc_python`
は 2026-06-27 に撤去）。

> かつては「Python（`dev_safe/vacc_python`）で試作 → C# へ手動移植」運用のため、同一アルゴリズムが
> 2 言語に存在し、移植漏れで C# が黙って退行する**ドリフト**が繰り返し起きていた（例: `FillSmallHoles`
> 段が reset で消失 / OkLab ハイライト L キャップ未移植）。決定的だったのは、移行時に
> **HAOLAN スニーカーの旧 Python baseline が IoU 0.73、実機 C# は 0.99** と判明したこと。改善サイクルが
> 製品でなく、製品から 0.26 も乖離した Python プロキシを測っていた。これを断つため、テストの計測経路を
> 製品 C# 自身へ一本化した。

Python は**消えたわけではない**が、役割が変わった。残る Python は **テストの driver / 計測 math**
（pytest・numpy の IoU 計算・PNG/GT 読み込み・dotnet サブプロセス駆動・色空間変換）だけで、
**再着色の判断は一切しない**。「並行実装」ではなく「製品 C# を回して測る道具」である。

## 構成

| パス | 役割 | git |
|------|------|-----|
| `Code/Core/PixelProcessor.cs` ほか | 製品の唯一の正（再着色エンジン本体） | 追跡 |
| `scripts/headless-run/Harness.cs` | `Code/Core/*.cs` を直接コンパイル＋実 Unity DLL 参照で `ProcessPixelsArray` を `dotnet` 実行する CLI。`--zones` JSON / `--autotune` / `--ffcheck` 対応 = **本物の製品経路** | 追跡 |
| `scripts/build-check/` | Unity 抜きで Editor コードを型チェック（`dotnet build`） | 追跡 |
| `scripts/golden/` | C# 自己ゴールデン回帰（合成入力で出力ハッシュを固定。Python 非依存） | 追跡 |
| `dev_safe/Tests/regression/` | IoU / 品質ゲートのテスト本体。**計測経路は実 C# ハーネス** | gitignore（ローカル専用） |
| `dev_safe/Tests/Baselines/` | 凍結 baseline（C# 出力由来） | gitignore |

> `dev_safe/` は **gitignore**。テスト資産（GT マスク・baseline・テストコード）はローカル専用で、
> コミット対象になるのは `scripts/` 配下と `Code/` のみ。だからこそ本ドキュメント（追跡対象）に
> アーキテクチャを残す。

### テストの計測エンジン（要）

`dev_safe/Tests/regression/fixtures.py` の以下が全 IoU/品質テストの計測の中心：

- `run_harness(rgba, zones, settings, exclude=None, tag=...)` — 任意のゾーン/設定/除外マスクで
  実 C# ハーネスを走らせ出力 RGBA を返す汎用ランナー。
- `run_csharp(rgba, zone, settings, case)` — IoU 経路用の薄いラッパ（除外マスクなし＝全画素対象）。
- `run_case(...)` — 1 ケースを `run_csharp` で実行し IoU / Precision を計算。`iter_subject_results`
  経由で `test_*_iou.py` / `test_false_positive.py` / `quality_report.py` が共有する。

選択（どの画素を選んだか）の IoU は `recolored_mask`＝「入力から RGB が変化した画素」で定義する。
よって**ハーネスの出力 RGBA だけで IoU が成立**し、選択マスクを別途出力する必要はない。

### 計測 math と合成素材（アルゴリズムではない）

- `colorspace.py` — `rgb_to_hsv` / `hsv_to_rgb` / `_rgb_to_oklab` 等。出力を測るための色空間変換のみ。
  数値は Unity `Color.RGBToHSV` / OkLab 標準係数に一致（既存しきい値の較正を保つため変更しない）。
- `synth_parts.py` — `build_part` / `picks`。単一色相パーツの合成テクスチャ生成（テスト入力素材）。

## 実行方法

前提: `dotnet`（.NET 8 SDK）と Unity CoreModule DLL（既定 `C:\Program Files\Unity\Hub\Editor\2022.3.22f1\...`）。
無い環境ではハーネス系テストは **skip**。ハーネスのビルドは pytest 実行内で自動（モジュール 1 回）。

```bash
# IoU / 過検出（選択精度）
python -m pytest dev_safe/Tests/regression/ -q -k "iou or false_positive"

# 品質ゲート（実 C# / recolor_quality 指標）
python -m pytest dev_safe/Tests/regression/test_csharp_quality_gate.py dev_safe/Tests/regression/test_recolor_quality_gate.py -q

# 三角（タイトマスク=recolor_quality / ノーマスク最悪条件=csharp_headless）
python -m pytest dev_safe/Tests/regression/test_triangle_recolor_quality.py dev_safe/Tests/regression/test_triangle_csharp_headless.py -q

# 全回帰
python -m pytest dev_safe/Tests/regression/ -q

# C# 自己ゴールデン（合成入力・出力ハッシュ固定）
python -m pytest scripts/golden/test_golden_csharp.py -q
```

> 計測ごとに `dotnet` を起動するため Python in-process より遅い（全回帰で十数分規模）。
> 正確さ（実機経路）を優先した結果。必要ならゾーンのバッチ実行で高速化余地あり。

## baseline の再生成

IoU の凍結 baseline（`Baselines/refactor-pre/*.json`）は **C# 出力由来**。意図的にアルゴリズムを
変えた、または toolchain（Unity / dotnet）を更新したときだけ再生成する。

```bash
# 既存 baseline があると上書き拒否されるので、対象を削除してから再生成する
rm dev_safe/Tests/Baselines/refactor-pre/<subject>_baseline.json
python dev_safe/Tests/run_baseline.py --subject <bandana|haolan-costume|haolan-sneakers>
```

**flood-fill（連続領域モード）は実験中のため、baseline はすべて flood-fill OFF で生成する**
（`fixtures.ZoneSpec.use_flood_fill=False` 既定、ハーネス JSON で `useFloodFill: false` 明示）。
実験中の挙動を baseline に焼き付けないため。

## 撤去したもの（2026-06-27）

- `dev_safe/vacc_python/`（アルゴリズムの Python 再実装 2201 行）を**完全削除**。
- `test_dark_target_brightness.py` / `test_match_sample_normalization.py` を削除。理由＝これらが測っていた
  機能（OkLab ハイライト L キャップ `OKLAB_HIGHLIGHT_L_MULT` / スポイト正規化 `compute_match_sample`）は
  **製品 C# に存在しない**（`grep` 0 件）。Python 専用機能をテストして「緑」になっていた＝最も危険な
  ドリフト（実在しない挙動への偽の安心）。製品に無い以上、守るべき挙動が無い。
- 副産物のバグ修正: `test_triangle_csharp_headless.py` / `test_recolor_anchor_brightness.py` が
  旧アセンブリ名 `VACCHeadless.dll` を参照しており**サイレント skip していた**のを `IrocaHeadless.dll`
  へ修正。これで 16 個の実 C# テストが復活し全 pass。

### 将来 C# へ移植する候補（撤去機能のアイデアは保存）

機能そのものは製品 C# に無いが、ユーザー報告由来の正当な関心事。製品 C# に実装したら、その時に
**実機ハーネス経由のテストを新設**する（Python では復活させない）：

- **暗ターゲットの明部白暴走キャップ**（旧 `OKLAB_HIGHLIGHT_L_MULT`）。暗い色を指定したのに明部が
  白へ暴走する問題への対策。`PixelProcessor` の OkLab 2 区間 L リマップ上端キャップとして移植可能。
- **スポイト位置の正規化**（旧 `normalizeMatchSample` / `MatchSampleNormalizer`）。影/光沢どこを
  スポイトしても選択が同じになる機能。`autoRecolorAnchor`（出力側の位置非依存・実装済み）の選択側版。
