# 精度向上ロードマップ 2026-07 — 色距離打ち止め後の3トラック計画

状態: **A 完了(c928abe) / B 完了(e89a1ed, noise 1.775→1.411) / C は Phase 0 完了・Phase 1 は no-go 確定(2026-07-05, 本文末尾の実施記録参照)**。以下の本文は着手前の計画を原文のまま残す。
前提: `docs/oklab_matching_conclusion.md` §5.1 で「選択の色距離は上限到達。次は空間・構造軸」と確定済み。残る精度課題は性質の異なる 3 系統であり、以下の優先順で実行する。

| Track | 内容 | 種別 | 依存 |
|---|---|---|---|
| **A** | GT拡充: feina服を回帰に編入し新しい実失敗面を開く | 評価基盤＋改善サイクル | なし（最優先） |
| **B** | feina三角 achroma ブロックノイズ 1.775→≤1.5 | 再着色段の実バグ修正 | なし（Aと並行可） |
| **C** | シード統合の再挑戦（seed-aware tolerance 再走） | 空間軸の新機構 | A の実被写体GT（Phase 0 前提） |

## 0. 全トラック共通の実行規範

- **測定は常に実C#ハーネス経由**。`python -m pytest dev_safe/Tests/regression/ -q`。Python再実装を作らない。
- **視覚レビュー必須**（CLAUDE.md 改善サイクル手順）: `python tools/visual_review.py snapshot` → 変更 → `python tools/visual_review.py compare --engine csharp` → 比較PNGを Read で1枚ずつ確認 → `python tools/visual_review.py approve`。pre-commit フックが Code/ 変更に approved.json を要求する。
- **dev_safe/ は入れ子の独立 private リポ**（本体からは追跡不可）。dev_safe 配下の変更は dev_safe 内で別途 commit する。
- コミットは Conventional Commits・1コミット=1論理変更・**明示パスで git add**（`-A` 禁止、並行エージェント対策）・テスト全パス時のみ。
- Code/ 編集後の型チェック: `dotnet build scripts/build-check/VACCEditor.csproj`。**Harness ビルドの成否を tail で見ない**（過去に stale バイナリ事故あり）。
- 特定キャラ名・座標・色値でのアルゴリズム分岐禁止（GT・fixtures の**データ定義**は例外）。
- valueBlend は原則 1.0 維持・edgeFeather は原則 0。どのトラックでも動かさない。
- 各トラックの停止条件に達したら、無理に進めず結果を報告して止まる。

## Track A: GT拡充 — feina服の回帰編入

### A-1 目的
現在の実被写体GTは 4 つ（bandana / haolan-hair / haolan-costume / haolan-sneakers）のみで、全て IoU 0.937〜0.998 の飽和域。「色の情報を取りきった」のは正確には**この4被写体上で**の話。新しい実被写体を評価に入れ、改善可能な実失敗を発見して改善サイクルに掛けることが精度向上の実弾になる。

### A-2 資産（存在確認済み）
- 元テクスチャ: `dev_safe/texture_sample/Feina/PNG/Clothes.png`（4096×4096）
- GTマスク30枚: `dev_safe/texture_sample/ground_truth/feina_clothes/mask_*.png`（Rチャンネル二値、テクスチャと同一座標系・反転なし）。Boots系10枚 / Goggles系7枚 / Tops系8枚 / Pants系3枚 / Bandana系3枚 ほか
- レイヤー台帳: `dev_safe/texture_sample/ground_truth/feina_clothes_fixtures.json`（21レイヤー分の `layer_path / mask_file / pixel_count / bbox_xyxy / sample_xy / sample_rgb_255`）
- PSD（GTの正）: `dev_safe/texture_sample/Feina/Feina_PSD_CLIP/Clothes.psd`（白レイヤー非表示の注意書きあり）
- **注意**: per-leaf マスクを再生成するスクリプトは現存しない（`analyze_feina_extend.py` はGT生成器ではなく halo 診断）。既存マスク30枚をそのまま使い、再生成が必要になったら新規スクリプトを起こす。

### A-3 被写体の選定（提案。PSDと突き合わせて最終確定）
被写体は「ユーザーがそのパーツの地色をクリックしたとき選択されるべき画素集合」を single_mask 形式で定義する。**同一素材のシェーディング（Shadow）はGTに含め、別素材のディテール（Metal/Lens 等）は含めない**。leaf の取捨は `feina_clothes_fixtures.json` の `sample_rgb_255` と PSD レイヤー構造を正として判断し、採用した合成規則を生成スクリプトのコメントに残す。

| 被写体ID（案） | GT合成（leaf の和集合、要PSD確認） | 狙い |
|---|---|---|
| `feina-pants` | Pants ∪ Pants_Shadow（Seam は色を見て判断） | 大面積の基本ケース |
| `feina-boots` | Boots_1 ∪ Boots_2 ∪ Shadow（Belt/Metal/Shoelace/Sole/Stitch は除外） | **隣接小パーツ密集 = precision の実地試験**（本命） |
| `feina-tops` | Tops_1 ∪ Tops2 ∪ Shadow ∪ Shadow_LongSleeve（Ethnic_Pattern/Ribbon/Seam は除外） | 模様保持と選択の両立 |
| （任意）`feina-goggles-belt` 等 | 小パーツ単体 | 小領域 recall |

まず 3 被写体で開始し、編入が軌道に乗ったら追加を検討する。

### A-4 手順
1. **GT合成スクリプト新設** `dev_safe/scripts/build_feina_gt.py`: 上記合成規則で leaf マスクを OR 合成し、`dev_safe/texture_sample/ground_truth/feina_<part>_mask.png`（Rチャンネル二値・4096×4096・反転なし）を出力。各被写体の採用leafと除外leafをスクリプト内に列挙。
2. **fixtures JSON 作成**: 既存被写体と同スキーマ（`{"sample_color_rgb":[r,g,b 0..255], "tests":[{"suffix","target_rgb"}...]}`）。sample_color は台帳の `sample_rgb_255`、テスト色は既存被写体の suffix 構成（blue/red/green 等 5 色）を踏襲。
3. **被写体登録**: `dev_safe/Tests/regression/fixtures.py` に `RecolorSubject`（`gt_strategy="single_mask"`, `mask_channel="R"`）を定数定義し `SUBJECT_REGISTRY` に追加。
4. **初回計測**: 登録被写体を実C#ハーネスで全ケース実行し、IoU/precision/recall を記録（これが「新失敗面」の一次データ）。
5. **テスト編入**（列挙は全てテスト内ハードコードなので各所へ追加）:
   - `test_false_positive.py`: `_SUBJECTS` と `PRECISION_FLOOR`（床=実測−0.02 の慣例）
   - `test_recolor_quality_gate.py`: `_SUBJECTS`（実測でしきい値超過するケースは `KNOWN_BROKEN` に原因コメント付き strict xfail として登録 — 既知failの台帳化。**テストを緩めて誤魔化さない**）
   - `quality_report.py`: `_SUBJECTS`
   - IoU回帰: `test_bandana_iou.py` のパターンで `test_feina_*_iou.py` を新設し、`dev_safe/Tests/run_baseline.py --subject feina-<part>` でベースライン凍結
6. **視覚較正**: `quality_report.py --dump-png` → パネルを Read で目視 → `--label good|bad feina-<part> <suffix>` → `--validate` が green であること。
7. **改善サイクル起動**（本丸）: 発見された最悪ケースに CLAUDE.md の自律改善サイクルを適用。採用条件は従来どおり「既存4被写体・gen2・golden の非退行 ＋ 新被写体の改善 ＋ 視覚レビューOK」。

### A-5 受け入れ基準
- 新 3 被写体が回帰スイートに編入され、床・ベースライン・ラベルが揃い全テスト green（xfail は台帳化されたもののみ）
- 初回計測サマリ（被写体×色ごとの IoU/precision/recall 表）が報告に含まれる
- 改善サイクルを回した場合: 変更ごとの数値差分と視覚レビュー結果

### A-6 停止条件
- 新被写体が全ケース 0.97+ で新失敗面が開かない → 編入（番兵化）のみで完了報告
- GT合成の leaf 取捨で PSD からも判断できない曖昧さ → ユーザーに確認
- 改善サイクルが既存被写体とのトレードオフしか生まない → 不採用・報告

## Track B: feina三角 achroma ブロックノイズ（1.775 → ≤1.5）

### B-1 現象
`test_recolor_quality_gate.py` の `KNOWN_BROKEN[("feina-triangle","black")]`（strict xfail）: cream(252,247,243)→黒、achroma+FormGain 経路で noise_amplification=**1.775** > `NOISE_AMP_CEIL=1.5`（`quality_thresholds.py`）。旧 Python proxy が隠蔽していた**実機のブロックノイズ**。clean 側最大は 1.18（hair purple）で 1.5 は較正済みの分離線。

指標定義: `dev_safe/Tests/regression/recolor_quality.py` の `noise_amplification()` = erode=2 の領域内部での「出力の3×3局所std平均 / 入力の同値」比。

### B-2 真因候補（コード読解済み・優先順）
1. **成分別基準Lの離散段差 × FormGain増幅**（最有力）: `Code/Core/PixelProcessor.Achroma.cs` の `BuildComponentMedianLMap()` が**連結成分ごとに単一の P80 基準L（regLmid）**を配布。三角は複数の小連結成分に割れており（既知: 5独立成分）、成分境界で regLmid が離散ジャンプ → `Code/Core/PixelProcessor.Recolor.cs` の achroma リマップ `rangeRemap = clamp(center + (oL−regLmid)×AchromaFormGain(2.5))` が段差を 2.5 倍に増幅 → 成分単位の階段状パッチ。
2. **256bin ヒストグラム量子化**: `PixelProcessor.cs` の `HistValueAtPercentile()` が L を 256bin に離散化して regLmid/regLlo/regLhi を返す。量子化誤差も FormGain で 2.5 倍。
3. `SolidifyAchromaInterior()` の内部 strength 二値化との相互作用。

### B-3 手順
1. **診断先行（コード変更前）**: `dev_safe/scripts/diag_triangle_noise.py` を新設。`fixtures.run_case` で feina-triangle/black を実行し、出力クロップ＋局所stdヒートマップ（`recolor_quality._local_std` 再利用）＋成分境界オーバーレイを PNG 出力。**ノイズの形状が成分境界と一致するかを視覚で確定**してから着手する（一致しなければ候補2/3へ）。※headless に段階別ダンプ機構は無い（`debug_dumps/` の既存PNGは Unity Editor Debug モジュール由来の陳腐化物）。必要なら Harness に dump フラグを足すのは可だが、まず上記スクリプトで足りるはず。
2. **改善サイクル**（仮説1つずつ・CLAUDE.md 手順厳守）。候補1が確定した場合の修正案（いずれか1つずつ試す）:
   - regLmid マップの成分間平滑化（成分境界の距離加重ブレンド、または小成分・近L成分の領域大域P80へのフォールバック）
   - `HistValueAtPercentile` の bin 内線形補間（量子化誤差の低減）
   - 増幅対象を低周波偏差に限定（`oL−regLmid` の高周波成分を FormGain から除外）
3. **非回帰条件（毎回全て確認）**: 三角の GOAL 群（form_brightness≥0.15 / value_bleed≤0.55 / edge_sharpen≥制限2.5 / recall≥0.85、`test_triangle_recolor_quality.py`）、`test_triangle_csharp_headless.py` 全検証（白背景保全・fringe 等）、品質ゲート全被写体、golden、回帰一式。三角経路は繊細な調整履歴（内部固め/AAソフトランプ/CleanAchromaFringe/中性リジェクト）を持つ — **既存挙動を壊す修正は即 revert**。
4. **達成時の後始末**: `test_recolor_quality_gate.py` と `test_csharp_quality_gate.py` の `KNOWN_BROKEN` から削除（strict xfail が xpass で fail になるので放置不可）、`quality_report.py --dump-png` → 目視 → `--label good feina-triangle black` → `--validate`。コミットは `fix(color): 三角achroma再着色の成分境界ブロックノイズを低減` 系。

### B-4 ストレッチ（任意・別コミット）
`test_synth_autotune.py` の xfail `lowsat_warm_gray`（WS-R=極端ターゲット増幅抑制・未実装）は三角と同根とされる。三角修正のメカニズムがそのまま効く場合のみ xfail 解除を試みる。効かない場合は着手しない（仕様判断待ち案件のまま）。

### B-5 停止条件
- 修正候補3案とも noise≤1.5 と三角GOAL維持を両立できない → 診断結果と試行ログを報告して停止
- 他被写体・goldenに悪化が出るトレードオフしか無い → 不採用・報告

## Track C: シード統合の再挑戦（seed-aware tolerance 再走）

### C-0 背景と教訓
navy 型（同色相・同彩度・明度違い別パーツ）は距離式 `d = hd + sd·satW + vd·valueW·(1−clamp(pS/sS))` で pS≈sS のとき明度項が消え **d≈0**。tolerance をどこに置いても分離不能なだけでなく、**navy 画素が距離分布の低側に混入して tolerance 導出（P95）自体を歪める**。ゆえにシード指定後は tolerance 再走が必須。過去の頓挫の教訓=**「tolerance 自動再走」と「非同期化」の両方が必要、片方欠けると壊れる**。本計画はこれを構造で担保する。

現状配線（確認済み）:
- シード: `Code/UI/PreviewView.Input.cs` の `HandleFloodFillSeedInput`（Shift+クリック）が `zone.seedUV` 設定→`previewDirty` のみ（**自動調整は再走しない**）。クリアは `IrocaWindow.ZoneList.cs` のボタン。消費は `PixelProcessor.cs` → `ApplyConnectedComponentMask`（`PixelProcessor.FloodFill.cs`。無効シードは自動フォールバック）。
- 自動調整: `ZoneAutoTuner.Analyze(pixels, w, h, zone, session, excluded, maskW, maskH)`。呼び出しは `IrocaWindow.AutoTune.cs` → `PreviewJob<TuneResult>` で**既に BG 実行・キャンセル・世代ガード・二重起動防止あり**。excluded（ユーザーマスク）は全導出関数・Verify に配管済み。
- 選択キャッシュキーに `useFloodFill`/`seedUV` は収載済み → 再走漏れ時も「古い選択の誤再利用」は構造的に起きず、劣化モードは現行出荷挙動（CC keep のみ）で止まる。
- ハーネス: `scripts/headless-run/Harness.cs` の zones JSON は `useFloodFill`/`seedUV` 既対応。`--seed` 新引数は不要。

### C-1 コア設計
新規抽象を作らず `Code/Core/ZoneAutoTuner.Seed.cs`（partial）を1枚追加:
1. 既存経路で暫定 T0 を導出（シード無しならここで return = 完全従来経路）
2. `BuildSimZone`（`ZoneAutoTuner.Verify.cs`、internal 化して共用）を探索 tolerance `Texplore = max(T0, SeedExploreTolFloor)` で構築し全画素 strength 算出
3. シードから BFS で自成分マスク `selfMask` を構築（述語 strength>0 ∧ α≥128・4連結 = `ApplyConnectedComponentMask` と同一。`PixelProcessor.FloodFill.cs` に `internal static TraceComponentFromSeed()` を新設して単一ソース化。シードが strength=0 上なら放棄して T0 を返す=UI の無効シード意味論と一致）
4. `excludedSeed = !selfMask ∪ ユーザーマスク除外` として既存導出関数群（`DeriveAutoTonalSamples` → `TryDeriveChromaticTolerance[Multi]` / `TryDeriveAchromaticTolerance`）を再実行 → T1
   - **重要: 成分外画素を foreign 距離キャップに使わない**。navy は d≈0 のためキャップに使うと tolerance が床まで潰れ自パーツ陰影が全滅する。navy の除去は既存 CC keep（seed 上書き）が担い、tolerance は「自成分を過不足なく覆う」ことに責務限定
5. 閉ループ検証（`VerifyBrightForgivenessOvershoot` と同形）: T1 で成分面積 growth 超過なら `TolShrinkSteps` 縮小＋証拠被覆率で下げ止め。既存 Verify 群も excludedSeed で再実行
6. `Analyze` へは省略可能引数 `seedAware=false, ct=default` のみ追加（既存呼び出し全て無変更）

スコープ宣言: 分離可能なのは**画素非連結**の navy（UVギャップ/アウトライン/透明で切れている — gen2 navy・実アトラスの大半）。完全画素連結の同色相隣接は原理的に不能（マスクのみが解）— gap=0 変種を追加して**測って明記**する。

### C-2 段階分割（各Phase独立コミット・独立revert可）
**Phase 0: 計測整備（製品コード変更ゼロ）**
- `dev_safe/measure_autotune.py` にシード付き実行（zones JSON へ `useFloodFill`/`seedUV` を書くだけ）。**座標系校正テストを最初に固定**（raw は上→下、UI seedUV は GetPixels32 準拠下→上。2パーツ合成でシード位置→再着色パーツの対応を確認）
- gen2 `same_hue_value_split` / `atlas_multipart` / `patterned_stripes` / `two_islands` にGT内部シードを与えた現行挙動（シード非考慮 autotune + CC keep）のベースライン凍結（`measure_gen2.py --seeded` 新設）— tolerance 再走の寄与を分離する対照群
- `synth_gen2.py` に `same_hue_value_split_touching`（gap=0、原理限界の計測用・受け入れ基準なし）追加
- 実被写体（Track A の新GT含む）のシード付き計測を追加
- 完了条件: 校正テスト green / ベースライン JSON 生成 / 既存 pytest 全パス

**Phase 1: コア機構（UI未接続・ハーネスで測る）**
- `TraceComponentFromSeed` 新設 / `ZoneAutoTuner.Seed.cs` 新設 / Harness に `--seed-autotune` フラグ＋AUTOTUNE 診断出力（T0/T1/成分px/SEEDTUNE_MS）
- **ビット不変性の機械証明**: `test_seed_autotune_bitinvariance.py` — シード無し zones でフラグ有無 2 回実行し出力 raw を**バイト比較**（完全一致必須）
- 受け入れ基準: navy シード付き **IoU≥0.95 / precision≥0.98 / recall≥0.95**（3クリック位置全て）/ atlas prec≥0.97・rec≥0.93 / stripes recall はシード無し比 −0.02 以内（明度ギャップ過剰反応の検出）/ two_islands は「クリック島のみ」GTで IoU≥0.95 / 実被写体シード付きで非退行 / 既存回帰・golden 全パス / SEEDTUNE_MS 2048²で追加≲1.5s / 視覚レビュー

**Phase 2: UI統合（非同期）**
- `IrocaConsts` に `EnableSeedAwareAutoTune`（**初期コミット false のダーク導入**）
- シード変更の**単一入口** `NotifySeedChanged(zone)`（Shift+クリック設定とクリアボタンの両方から必ず呼ぶ。設定/クリア対称に再走 →「tolerance は常に現在のシード状態で導出済み」を不変条件化）
- `ScheduleSeedRetune`/`ProcessPendingSeedRetune` は既存 `ScheduleAutoTune` の最小差分複製（同じ `PreviewJob`・0.4s デバウンス・実行中ジョブは Cancel して取り直し）。**`Analyze(seedAware:true)` を UI から同期呼び出しするコードパスを作らない**（レビュー基準）
- apply は **tolerance と extraSamples のみ**（他8項目は触らず手調整を破壊しない → 確認モーダル不要）。手動 Auto-Tune ボタンは `seedAware: EnableSeedAwareAutoTune` を渡すだけ
- 鮮度ガード（schedule 時の seedUV と apply 時の一致確認）＋ `Undo.undoRedoPerformed` フックで seedUV 変化時に再予約
- 新規UI要素を足す場合はツールチップ必須（general.md）
- 完了条件（目視チェックリスト）: 4096²で UI 無凍結の2段階更新（①即時CC反映 ②再走後tolerance反映）/ シード連打・走行中クリア/ゾーン削除/テクスチャ差し替えで事故なし / フラグOFF時の挙動完全不変 / Undo整合

**Phase 3: 既定ON化**（`EnableSeedAwareAutoTune=true` のみの独立コミット。ロールバック=この1定数 revert）

### C-3 リスクと停止条件
| リスク | 緩和 | 停止条件 |
|---|---|---|
| Texplore で AA 橋により navy へ連結 | growth verify + TolShrinkSteps | stripes/two_islands の recall 床と navy precision が両立不可なら中止し成分定義（bleed遮断）を別課題化 |
| 完全隣接 navy | gap=0 変種で限界を計測・明記（マスク解へ誘導） | 受け入れ基準に含めない |
| 追加コスト（全画素×2パス） | BG実行＋SEEDTUNE_MS監視、超過時 stride 検討 | 2048²で恒常>3s かつ削減不能 |
| 手調整の上書き | apply を tolerance+extraSamples に限定 | — |

## スコープ外（今回やらない）
- **マスク「含める」モード**（`docs/idea2.md` の3モード化）: 有力な将来候補だが今回は非選択。navy 型のユーザー解としては現行の Shift+クリックシード＋除外マスクが先に強化される
- **UVアイランド/メッシュ構造情報**: `docs/flood-fill-reactivation-plan.md` §6 の非目標のまま
- **色距離の再差し替え**（OKLab 等）: `docs/oklab_matching_conclusion.md` §5 の再訪条件を満たさない限り再検討しない

## 完了報告に含めるもの
- Track ごと: 実施した変更・数値の before/after 表（被写体×ケース）・視覚レビュー結果・採用/不採用の判断理由・コミット一覧（本体/dev_safe 別）
- 新たに開いた失敗面と、次の改善候補（あれば）

---

## Track C Phase 0 実施記録と Phase 1 no-go 判断（2026-07-05）

製品コード変更ゼロで Phase 0（計測整備）を完了し、その計測結果により **Phase 1
（seed-aware tolerance 再導出 = ZoneAutoTuner.Seed.cs）は実装不要（no-go）と確定**した。

### 整備した計測基盤（dev_safe、全て green）
- `test_seed_coords.py` — 座標系校正テスト。同色の上下 2 バーで「シード側だけ再着色」を
  assert し、`seedUV = (x/(w-1), y_top/(h-1))`（**flip なし**、raw=top-down）を機械固定。
- `synth_gen2.py` — `sample_pos_at_pct()`（クリック位置=シード位置の再現）と
  `same_hue_value_split_touching`（gap=0 の原理限界計測用）を追加。
- `measure_gen2.py --seeded [--baseline]` / `measure_autotune.py --seeded [--baseline]` —
  GT 内部シード付きの現行挙動（シード非考慮 autotune + CC keep）を計測・凍結。
  ベースライン: `synth_gen2_seeded_baseline.json` / `autotune_seeded_baseline.json`。
- `test_seed_gen2.py` — 下記到達水準を Phase 1 受け入れ基準そのままのハード床で守る
  受け入れゲート（15 テスト）。

### 発見1: gen2 navy は出荷済み機構だけで Phase 1 受け入れ基準を既に満たす
シード付き現行挙動（コード変更なし）の実測。基準は C-2 Phase 1 の受け入れ基準。

| ケース(+GT内部シード) | IoU | Prec | Rec | 基準 | シード無し baseline |
|---|---|---|---|---|---|
| same_hue_value_split (3位置全て) | **0.979** | **1.000** | **0.979** | ≥0.95/≥0.98/≥0.95 | 0.495/0.500/0.979 |
| atlas_multipart (3位置全て) | 1.000 | 1.000 | 1.000 | prec≥0.97/rec≥0.93 | 0.504/0.504/1.000 |
| patterned_stripes | 0.978 | 1.000 | 0.978 | recall 差 ≤0.02 | 差 0.000 |
| two_islands (クリック島GT) | 0.987–0.988 | — | — | ≥0.95 | — |
| same_hue_value_split_touching (gap=0) | 0.495 | 0.500 | 0.979 | 基準なし(原理限界) | 同値 |

C-0 が懸念した「navy 混入による tolerance 導出(P95)の歪み」は実害にならない:
navy(d≈0)の低側混入は P95 を **ChromaTolMin 床(0.08)** まで押し下げるが、床が自パーツの
分布を覆い、深い陰影はシャドウ免除が拾うため recall 0.979 を維持する。tolerance 再導出が
改善できる余地はゼロ（受け入れ基準は全て充足済み）。gap=0 変種は予想どおり AA 橋で
1 成分に融合しシード無効（IoU 0.495）= スコープ宣言（マスクのみが解）を数値で確認。

### 発見2（新しい失敗面）: 実被写体では単一シードが recall を壊滅させる
実被写体にGT内部シードを与えた現行挙動（`autotune_seeded_baseline.json`）:

| 被写体 | シード付き IoU / Prec / Rec | シード無し IoU (autotune baseline) |
|---|---|---|
| bandana | 0.719 / 0.997 / 0.721 | 0.993 |
| haolan-costume | 0.495 / 1.000 / 0.495 | 0.938 |
| haolan-sneakers | 0.292 / 0.999 / 0.292 | 0.965 |
| haolan-hair | **0.018** / 1.000 / 0.018 | 0.979 |
| feina-pants | 0.417 / **1.000** / 0.417 | 0.198 |
| feina-boots | 0.088 / 0.995 / 0.088 | 0.195 |
| feina-tops | 0.062 / 1.000 / 0.062 | 0.767 |

precision はほぼ 1.0 に跳ね上がる一方、**recall が壊滅**する。原因は tolerance ではなく
**CC keep の単一成分意味論**: 実被写体のパーツはテクスチャ上で多数の島（UV アイランド・
左右対称パーツ・小物）に分かれており、「シード成分だけ残す」が他の正当な島を全て落とす。
これは Phase 1（tolerance 再導出）では一切改善しない（再導出は選択画素を増やす機構では
あっても、keep 意味論には触れないため）。

### 判断
- **Phase 1 = no-go**（停止条件「指標がこれ以上有意に改善しない」に該当）。
  受け入れ基準は出荷済み機構で全充足であり、実装しても計測可能な利得がない。
  Phase 2（UI 統合）/ Phase 3（既定 ON）も前提を失い消滅。
- 到達水準は `test_seed_gen2.py` がハード床で恒久保証する（将来 FF/シード/導出の変更で
  navy 分離がこの水準を割れば fail する）。
- **次の改善候補は仕様判断待ち**: 実被写体でシードを実用にするには「単一成分 keep」を
  「シード成分と同素材の全成分 keep」（色類似成分の和集合）等へ拡張する仕様が必要。
  これは現行のシード UX（bleed 切り落とし用の上書き）と目的が異なるため、導入するか・
  どの UI 意味論にするかはユーザー判断（feina pants で prec 1.000 が出ている通り、
  bleed 切り落とし用途としては現行意味論が正しく機能している点に注意）。
