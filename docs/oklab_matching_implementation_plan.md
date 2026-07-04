# OKLab マッチング距離 — 実装計画（承認済み・未着手）

状態: **計画承認済み・実装未着手**（2026-07-04 プランニングセッションの成果物）。
親ドキュメント: [`oklab_matching_distance_experiment_plan.md`](oklab_matching_distance_experiment_plan.md)（実験の背景・仮説・評価方針の正）。本書はその §3 実装方針を、実コード調査に基づく**ファイル単位の具体的実装手順**へ落としたもの。記載の行番号は 2026-07-04 時点（develop, 6078051）。

## Context

選択（どの画素を色替え対象とするか）は `ColorZone.Match.cs` の HSV+RGB ハイブリッド距離、再着色は OkLab と、2つの処理が別色空間で動いている非対称がある。実験計画書は「OKLab 距離を既存経路と並行する隔離実験経路として実装し、数値と視覚レビューで有効性を確認してから採否判断する」ことを規定済み。加えてユーザー要望として、**Unity のデバッグモード内トグルで新旧アルゴリズムを切替**できるようにする。

**スコープ（ユーザー確認済み）**: 実装＋初期計測まで。OKLab 式のチューニング（改善サイクル）・定数再較正・本番デフォルト切替は含めない（計測結果を見てユーザーが判断）。

**最重要原則**: 既定は必ず従来 HSV 経路。既定時は全出力**バイト単位で不変**（golden で証明）。

## 事前調査で確定した設計判断の根拠

- 本番の選択経路は `PixelProcessor.cs:298` の `zone.GetMatchScoresPrecomputedHSV(...)` ただ1箇所（`GetMatchStrength`/`ContainsPixel` 等は本番未使用、`ZoneAutoTuner.Verify.cs` の閉ループ検証のみが別途呼ぶ）。
- 距離式の実体は3箇所に存在: ①主経路 `ColorZone.Match.cs`、②緩和経路 `PixelProcessor.Selection.cs:404` `GetRelaxedMatchStrength`（手動複製）、③ `ZoneAutoTuner.Tolerance.cs:217` （tolerance 導出用インライン再実装）。v1 は①のみ OKLab 化するハイブリッド構成（実験計画書 §5 が許容）。
- 切替の配線は `DebugCaptureHooks.ParallelismOverride`（`DebugCaptureHooks.cs:56`）と完全同型の static フラグ方式が最小干渉。Harness CLI と Unity デバッグ UI の両方から同じフラグを設定できる。
- `Harness.csproj` は `ColorZone*.cs`/`PixelProcessor*.cs` を glob 列挙しているため、新規 partial 追加は csproj 変更不要。
- OKLab 変換ヘルパーは `PixelProcessor.ColorSpace.cs` に既存（float 版 `:97` / byte+LUT 版 `:104`、いずれも private → internal 昇格が必要。ColorZone と同一 asmdef）。
- **選択キャッシュ（`BuildSelectionKey`, `PixelProcessor.cs:63-92`）にフラグを含めないと、トグル切替時に旧経路の選択がキャッシュヒットで復元されるバグになる。** キー変更時は `Harness.cs:331-335` の selkey-audit（リフレクション Invoke の固定引数配列）も同一コミットで修正必須。

## 実装

### コミット1: `feat(core): OKLab マッチング距離の隔離実装を追加（未接続・出力不変）`

**新規 `Code/Core/ColorZone.MatchOklab.cs`**（partial）:

- `public void GetMatchScoresPrecomputedOklab(float pH, float pS, float pV, float pL, float pA, float pB, Color pixelColor, int x, int y, int texWidth, int texHeight, out float strength, out float highlightPot, out float matchConf)` — 既存 `GetMatchScoresPrecomputedHSV`（`ColorZone.Match.cs:124`）と同型。Rect 分岐も複製。HSV も受けるのは温存ヒューリスティック（彩度ガード・ハイライト回復）用。
- `GetColorMatchScoresOklab` — マルチサンプル和集合（`:157-178` と同型）。先頭でピクセル側の `pCn = sqrt(pA²+pB²) * InvOklabChromaNorm` と色相角 `pHueOk = atan2(pB,pA)/2π`（turns）を**1回だけ**計算して全サンプルへ渡す（マルチサンプル時の Atan2 重複回避）。
- `MatchOneSampleOklab` — 距離式は既存 hsvDist（`:334`）と**同型・同スケール**に構成し、tolerance / softRange / hardRange / `CalculateEdgeStrength` を無変更で流用:
  ```
  hueDistOk      = 環状距離(pHueOk, sc.sHueOk) ∈ [0, 0.5]   // HSV hDist と同スケール(turns)
  hueRelevanceOk = Clamp01(max(pCn, sc.sCn) / max(0.01, chromaThreshold))  // 既存 hueRelevance(:265)と同形
  effHueDist     = hueDistOk * hueRelevanceOk
  cRatio         = (sc.sCn > 0.01) ? Clamp01(pCn / sc.sCn) : 1
  distOklab      = effHueDist + |pCn - sc.sCn| * satDistWeight + |pL - sc.sL| * valueWeight * (1 - cRatio)
  ```
  - **グレー/有彩のハード分岐（`:205-206`）は撤廃**（単一連続式への統合が仮説の核心）。低 chroma では hueRelevanceOk→0 で `|ΔCn|+|ΔL|` 距離へ連続的に退化。
  - **RGB 距離との chromaConfidence Lerp（`:343`）は載せない**（Hue 特異点パッチそのものなので）。
  - 重み wL/wC/wH は新パラメータを作らず既存 `valueWeight`/`satDistWeight` を流用（プリセット互換・UI 追加ゼロ）。固定係数化の是非は再較正フェーズ（スコープ外）で判断。
- ヒューリスティック翻訳（v1 方針）:

  | 項目 | 方針 |
  |---|---|
  | 彩度ガード hard reject (`:197-200`) | HSV のまま流用（pS 判定を先頭に） |
  | グレー暗サンプル特殊処理 (`:219-223`) | 省略（L が知覚明度なので不要という仮説。near-black を計測の重点確認対象に） |
  | ChromaGate (`:232-238`) | C 基準に翻訳し連続化: `sc.sCn > ChromaGateActivateSat` 時に shortfall を距離加算、`(1 - chromaConfidenceOk)` を乗じ低 chroma サンプル限定を連続再現 |
  | AA 縁 soft ramp (`:244-246`) | 連続化: `aaSoftRange = max(softRange, tol * AchromaEdgeSoftness * (1 - chromaConfidenceOk))` |
  | 彩度ゲート satConfidence (`:259`) | C 基準: `cConf = Clamp01((pCn - satMinOk)/satRampOk)`、`gate = Lerp(1, cConf, chromaConfidenceOk)` |
  | シャドウ免除 (`:269-286`) | L 基準に同型翻訳（V→L、hue ゲートは effHueDist < ForgivenessHueGate、satFactor は pCn ベース） |
  | 明部免除 (`:293-306`, `:355-365`) | L 基準に同型翻訳。**暗部距離短縮の廃止＝非対称はそのまま踏襲**。`simDisableBrightForgiveness` も参照 |
  | ハイライト回復 (`:370`) | HSV のまま流用（`CalculateHueDistance(pH, sc.sH)` を計算して既存メソッドを呼ぶ）。highlightPot は HSV ベースの空間パス（PropagateHighlights/GrowHighlightBand）に繋がる別チャンネルのため |

- matchConf: `private const float CoreMatchDistanceOklab = 0.14f;`（同スケール構成なので HSV 版から開始。再導出は採用時の別作業）。`strength > 0` 時のみ `Clamp01(1 - distOklab / CoreMatchDistanceOklab)`。
- 新定数: `InvOklabChromaNorm`（sRGB 域内最大 chroma ≈ 0.323 の逆数。HSV の S [0,1] とスケールを揃える正規化）。
- 代替案としてコメントに残す: `ΔH_ab = sqrt(max(0, Δa²+Δb² - ΔC²))`（CIE 型・角度計算不要・chroma で自動減衰）。hueDistOk×relevance が低 chroma でまだ暴れる場合の差し替え候補。

**`Code/Core/ColorZone.cs`** — `SampleCache` struct（`:221-228`）に追加: `sL, sCn, sHueOk`（OKLab L / 正規化 chroma / 色相角 turns）、`satMinOk, satRampOk`、`chromaConfidenceOk`（`Clamp01((sCn - chromaThreshold)/0.10)`、Hue 角は暗部でも安定なので valueConf の min は取らない）。

**`Code/Core/ColorZone.Match.cs`** — `BuildSampleCache`（`:71-92`）末尾で `PixelProcessor.RgbToOklab(...)` を呼び新フィールドを**無条件充填**（キャッシュ無効化条件の変更が不要になり、既定時もバイト不変）。

**`Code/Core/PixelProcessor.ColorSpace.cs`** — `RgbToOklab` float 版（`:97`）と byte+LUT 版（`:104`）を `private`→`internal` 昇格。

この時点で新 API はどこからも呼ばれない。

### コミット2: `feat(core): マッチング距離の切替フラグを配線（既定 HSV・出力不変）`

**`Code/Core/DebugCaptureHooks.cs`** — `ParallelismOverride`（`:56`）の隣に追加:
```csharp
/// 【実験】選択(マッチング)距離を OKLab へ切替。false(既定)=従来 HSV/RGB ハイブリッド。
/// ProcessPixelsArray がジョブ開始時に 1 回読んでローカル化(ジョブ途中の変更は次回から)。
/// Harness は --matchDistance=oklab、Unity は Debug/PerfView が EditorPrefs 永続化で設定。
/// docs/oklab_matching_distance_experiment_plan.md 参照。
internal static bool MatchDistanceOklab;
```

**`Code/Core/PixelProcessor.cs`**:
- `ProcessPixelsArray`（CancellationToken 版, `:134`）先頭で `bool useOklab = DebugCaptureHooks.MatchDistanceOklab;` を**1回捕捉**（非同期プレビュージョブ中のトグル変更でもジョブ内は一貫。bool なので torn read もない）。
- `useOklab` 時のみ `pixOkL/pixOkA/pixOkB` を `s_floatPool.Rent`（`:178-181` の HSV 配列と同型）し、HSV 計算の Parallel.For（`:191-194`）内で byte LUT 版 `RgbToOklab` により充填。finally で返却。OFF 時はコスト完全ゼロ（Rent 自体をスキップ）。
  - メモリ: 4K で float[]×3 ≈ 201MB 追加だが `s_floatPool`（上限 2^24・maxArraysPerBucket 24, `:102-106`）の範囲内。
- `:298` の呼び出しを分岐: else 側に既存 `GetMatchScoresPrecomputedHSV` 呼び出しを**そのまま**残す（バイト不変の根拠）。
- `BuildSelectionKey`（`:63-92`）に `bool matchDistanceOklab` 引数を追加しキーへ `B(...)` を追記。呼び出し（`:250-251`）に `useOklab` を渡す。
- `GetRelaxedMatchStrength`（`PixelProcessor.Selection.cs:404`）冒頭に「OKLab 実験経路は当面未同期（HSV のまま）。採用時は要翻訳」のコメント追加。

**`scripts/headless-run/Harness.cs`**:
- 引数走査部（`:157-166` の並び）に `--matchDistance=oklab|hsv` 追加（`StartsWith` 走査、既定 hsv）→ `DebugCaptureHooks.MatchDistanceOklab` を設定。usage（`:151-152`）更新。`Console.Error.WriteLine("MATCH_DISTANCE ...")` を stderr へ（stdout の "OK" を汚さない、`--ffcheck` と同パターン）。
- `RunSelectionKeyAudit` の `KeyOf` 固定引数配列（`:331-335`）に `/*matchDistanceOklab*/false` を**同一コミットで**追加（リフレクション Invoke なので忘れると実行時エラー）。
- コメント注記: `--autotune` と `--matchDistance=oklab` の併用は「HSV で較正した tolerance を OKLab 距離に適用する」ため計測として無意味。

**ZoneAutoTuner 系は非対象**: `ZoneAutoTuner.Verify.cs`（`:66,133,140`）は `GetMatchScoresPrecomputedHSV` を直接呼ぶため、フラグと無関係に常に HSV＝自動調整は影響を受けない（意図どおり）。`ZoneAutoTuner.Tolerance.cs` に同期申し送りコメントのみ追加。

### コミット3: `feat(debug): デバッグモードに OKLab マッチング距離トグルを追加`

**`Code/Debug/PerfView.cs`**（Code/Debug/ は視覚レビューゲート・build-check 対象外）:
- `private const string PrefKeyMatchOklab = "Iroca.Debug.MatchDistanceOklab";`
- `Draw` の debug ブロック（`:55-61`）内 `DrawThreadControl` の後に `DrawMatchDistanceControl(host)` 追加。`DrawThreadControl`（`:84-122`）と同パターン:
  - `EditorGUILayout.ToggleLeft(new GUIContent("実験: OKLab マッチング距離", tooltip), ...)` — **ツールチップ必須**（プロジェクトルール）。文言に含める: 選択距離を HSV/RGB ハイブリッド→OKLab へ切替える実験機能である旨／プレビュー・適用・エクスポートの選択結果が変わる旨（発色計算は不変）／自動調整・穴埋め/境界回復は HSV のままで整合しない場合がある旨／通常は OFF（既定）推奨。
  - 変更時 `DebugCaptureHooks.MatchDistanceOklab = now; EditorPrefs.SetBool(...); host.MarkPreviewDirty();`（PerfView 内は BeginChangeCheck の外なので自動 dirty 化されない。明示呼び出し必須、`:78` が手本）。
  - ON 中は `EditorGUILayout.HelpBox("OKLab マッチング距離(実験)が有効です。エクスポート結果も変わります。", MessageType.Warning)` を常時表示（ON のまま忘れる対策）。
- `EnsurePrefsLoaded`（`:203-208`）に `DebugCaptureHooks.MatchDistanceOklab = EditorPrefs.GetBool(PrefKeyMatchOklab, false);` 追加（ドメインリロード復元。Debug asmdef 削除環境では復元コードごと消えて常に false＝本番安全）。

### コミット4（dev_safe 側・別リポジトリ）: fixtures / A/B 計測

dev_safe は入れ子の独立 private リポなので本体とは別にコミット:
- **`dev_safe/Tests/regression/fixtures.py`** — `run_harness`（`:250`）のコマンド組み立て（`:270-271`）に環境変数パススルー: `os.environ.get("IROCA_MATCH_DISTANCE")` が truthy なら `--matchDistance=<値>` を追記。既定（未設定）は引数リスト完全不変。全 pytest が env だけで OKLab 計測に切替可能になる。
- **`dev_safe/Tests/regression/_oklab_ab.py`**（新規、アンダースコア始まり=pytest 非収集。`_ff_ab.py` の前例に倣う）— SUBJECT_REGISTRY 全被写体（bandana / haolan-hair / haolan-costume / haolan-sneakers × 各5色）を `iter_subject_results` で HSV / OKLab の2周実行し、ケースごとの IoU / precision / recall（`intersection_pixel_count / gt_pixel_count` から導出。CaseResult に recall フィールドは無いが材料はある）と Δ、被写体別・全体平均を表出力。実験計画書 §3.3 手順3の主計器。

### コミット5: `test: visual_review にマッチング距離切替を追加`

**`tools/visual_review.py`** — `_parse_engine`（`:289`）と同型の `--match-distance` 解析を追加し、csharp 実行の dotnet 引数（`:118-119`）に条件付きで `--matchDistance=...` を追記。既定は付与しない。

## 検証

**各コミット共通（バイト不変ガード）**:
```
dotnet build scripts/build-check/IrocaEditor.csproj          # 型チェック（Unity不要）
python -m pytest scripts/golden/test_golden_csharp.py -q     # 出力ハッシュ固定=バイト不変の証明
python -m pytest scripts/golden/test_selection_key_audit.py -q  # 選択キー網羅監査（コミット2以降）
python -m pytest dev_safe/Tests/regression/ -q               # 全回帰（既定=HSV経路、十数分）
```
既定時出力不変のため、Code/ を触るコミットは `SKIP_VISUAL_REVIEW=1` で pre-commit を通す（出力不変変更に限り許可、golden green が根拠）。

**初期計測（実験計画書 §3.3 手順3-4、成果物=判断材料の報告）**:
```
python dev_safe/Tests/regression/_oklab_ab.py                # HSV vs OKLab 全被写体差分表
python tools/visual_review.py snapshot                        # 既定=HSV を before として保存
python tools/visual_review.py compare --engine csharp --match-distance oklab
# → 比較パネル PNG を Read で1枚ずつ確認（境界クロップ・全体サムネイル重点）
```
弱点重点確認: bandana 低彩度域、near-black 別素材（グレー暗サンプル特殊処理を省略した影響）、ハイライト境界、三角抽出（Feina）。

**Unity 実機確認**: デバッグモード ON → トグル切替でプレビューが再計算され選択が変わること、OFF に戻すと従来と一致すること。

**最終報告**: 計測結果（差分表＋視覚所見）をまとめ、チューニング継続/採否の判断材料として提示。**本番デフォルトは変更しない**。

## リスク・注意

- 選択キャッシュキーと selkey-audit の同時修正漏れが最大の落とし穴（コミット2内で不可分に扱う）。
- ChromaGate/彩度ゲートの C 翻訳は HSV 定数を流用するため正規化 chroma とのスケール差でずれる可能性 → v1 は流用で計測し、乖離はチューニングフェーズ（スコープ外）の課題としてメモ。
- OKLab ON のまま自動調整すると HSV 較正の tolerance が適用される → tooltip とコメントで明示。初期計測は固定 tolerance ケースのみ使用。
- 並行エージェント対策: コミットは明示パスで `git add`（`-A` 禁止）。

---

## 実装・初期計測結果（2026-07-04 実施）

状態: **実装完了・初期計測完了。採否＝ユーザー判断待ち。本番デフォルトは HSV のまま変更していない。**

### 実装（本体リポ・コミット済み）

| commit | 内容 |
|---|---|
| `d31afc8` feat(core) | OKLab 距離の隔離実装 `ColorZone.MatchOklab.cs`（未接続）。SampleCache に OKLab 派生値追加・RgbToOklab internal 化 |
| `4268d39` feat(core) | 切替フラグ配線（既定 HSV・golden バイト不変）。選択キャッシュキー＋selkey-audit を同一コミットで修正。Harness `--matchDistance` |
| `fc1dfb8` fix(core) | **L 項過選択の修正**（下記「計画式のバグ」） |
| `97c425f` feat(debug) | PerfView の OKLab トグル |
| `436c22e` test | visual_review の `--match-distance` |

dev_safe 側（`fixtures.py` の env パススルー ＋ `_oklab_ab.py` 計測スクリプト）は、`fixtures.py` に別作業由来の大きな未コミット変更（`require_harness` リファクタ）が同居していたため、それを巻き込まないようワーキングツリーに保持（並行エージェント注意ルール）。

### 実装中に判明した計画式のバグ（修正済み）

実装計画 §実装の距離式 `|ΔL| * valueWeight * (1 - cRatio)` は、**無彩サンプル（sCn≈0 → cRatio=1）で L 項が完全に消え、明度を無視して黒〜白の低 chroma 画素を全て拾う致命的過選択**になっていた（クリーム→青 tol0.32 で選択画素が 5.6k→715k＝126倍）。これは計画の「式」と「意図（低 chroma では |ΔCn|+|ΔL| 距離へ退化）」の内部矛盾。L 減衰を `chromaConfidenceOk` でゲート（`lWeight = 1 - cRatio * chromaConfidenceOk`）して修正。以降の計測はこの修正版で実施。

### A/B 計測（実 C# Harness・固定 tolerance・全被写体×各色）

`python dev_safe/Tests/regression/_oklab_ab.py`（HSV 既定 vs OKLab 実験の 2 周）。選択は sample 依存で target 非依存のため、各被写体で 5 色すべて同値＝実質 4 データ点。

| 被写体 | sample | IoU HSV | IoU OKLab | ΔIoU | recall HSV→OKLab | 所見 |
|---|---|---|---|---|---|---|
| bandana | クリーム | 0.998 | 0.995 | −0.003 | 1.000→0.997 | ほぼ同等 |
| haolan-hair | 青 | 0.993 | 0.992 | −0.001 | 0.993→0.992 | 実質同等 |
| **haolan-costume** | **青 S=1.0 V=1.0** | **0.937** | **0.558** | **−0.379** | **0.997→0.609** | **recall 崩壊** |
| haolan-sneakers | 青 | 0.991 | 0.984 | −0.007 | 0.999→1.000 | precision 微減（暗部わずかに過選択） |

**全体: 平均 ΔIoU = −0.097、改善 0 / 悪化 5 / 横ばい 15。OKLab は全被写体で HSV 以下。**

### 視覚レビュー（`compare` 相当パネル・Read で目視）

- **haolan-costume**: HSV は青パーツを全面変換、OKLab は明るい青だけ変換し**陰影の濃い青が未変換のまま残留**（miss overlay で陰影領域が大量に赤）。数値の recall 崩壊と一致。
- bandana / hair: HSV と OKLab は視覚的にほぼ区別不能。
- sneakers: ほぼ同等。暗い靴本体にごく僅かな過選択（数値の precision −0.007 と一致）。

### 根本原因（診断済み）

costume 崩壊は **satConfidence ゲート床の再較正問題**。ゲート床 `satMinOk = sCn × saturationStrictness(0.5) = 0.469` は OKLab の**知覚 chroma** を使うが、飽和材質でも**陰影では OKLab chroma が下がる**（青サンプル Cn=0.937 に対し、取りこぼした 720k 画素は 100% が Cn<床、median Cn=0.437・L=0.220＝暗い陰影）。HSV の S は陰影でも ≈1.0 を保つため、HSV 較正の `saturationStrictness=0.5` を OKLab chroma 単位に流用すると陰影の飽和材質を過剰にゲート除外する。shadow 免除は最暗部のみ救済し中間陰影の帯を取りこぼす。これは**リスク・注意に予告した「C 翻訳の HSV 定数流用によるスケール差」がそのまま顕在化**したもので、定数再較正フェーズ（スコープ外）の課題。

### 結論・申し送り

- **v1（HSV 定数流用）のままでは OKLab は採用不可。** 全被写体で同等以下、飽和×陰影材質で壊滅的。
- ただし失敗は距離式の構造ではなく**単一の再較正点（彩度ゲート床の chroma スケール）**に集約されており、仮説「ハード分岐を単一連続式へ統合」自体は否定されていない（bandana/hair/sneakers は同等＝連続式で破綻しない）。
- 次フェーズ（要ユーザー判断・スコープ外）の第一候補: `saturationStrictness` の OKLab chroma スケールへの再較正、または satConfidence ゲートを OKLab では chroma でなく別軸（例: 色相一致度）へ置換。あわせて `CoreMatchDistanceOklab` / ChromaGate の chroma スケール再導出。
- **本番デフォルトは HSV のまま。** 切替は Debug モードのトグル（実験）でのみ有効。
