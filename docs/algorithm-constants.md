# アルゴリズムの定数の台帳

選択・再着色・自動調整・AI 提案の後処理で使う、名前の付いた数値定数を、種類と根拠つきで並べた台帳。
`scripts/source_checks/test_algorithm_constants.py` がソースと突き合わせ、次のどれでも落ちる。

- 台帳に無い定数を足した（→ 行を足す）
- 値を変えた（→ 値と根拠を直す。測り直したならその記録を根拠に）
- 定数を消した・改名した（→ 行を消す・直す）
- 名前の無い小数のリテラルが増えた・減った（→ 増えたなら名前を付けて台帳へ、減ったなら数を下げる）

対象は出力（選択・再着色・自動調整の結果・AI 提案のマスク）に効くファイルだけ: `Code/Core` の `ColorZone*`・
`PixelProcessor*`・`ZoneAutoTuner*`・`HighlightSampleCorrector`・`UvChartMap` と `Code/MaskSuggest/Ops`。UI の寸法・
メモリ管理・保存形式と、利用者が変える設定の既定値（`RecolorSettings`・`ZonesJsonDefaults`・`ColorZone` のフィールド）は外す。

## なぜ台帳にするか

再着色の選択・自動調整には、手元の被写体で試して選んだ閾値が多い。1 つずつは理由があっても、どれが何の根拠で
入ったかが散らばると、減らすことも、新しい被写体で外れたときに疑う先を絞ることもできない（2026-10-24 の外部
レビュー「実測で調整した定数が数百個あり、被写体を増やすたびに再調整の圧力がかかる」）。

- 「実測」の定数は、手元の被写体の塗り方に合わせた仮定。新しい被写体で外れたときに最初に疑う先になる。
- 減らす候補の表にもなる。段ごとに外して測った結果（効いていない段）と組み合わせて使う。
- 足すときに、この表のどの種類になるかを決めさせる。実測の定数を足す前に、既存の定数やテクスチャの
  統計量（ヒストグラム・彩度の分布・領域の大きさの比など）から導けないかを考える（`.claude/instructions/improvement-cycle.md`
  の「汎化能力を最優先する」）。

## 種類

| 種類 | 意味 | 新しい被写体で外れうるか |
|---|---|---|
| 定義 | 数学・色空間・モデルの入力仕様から決まる（1/√3、OkLab の係数、SAM の入力 1024 など） | 外れない |
| 量子化 | 8bit の 1 階調・ゼロ割り防止の ε・浮動小数の誤差の余裕など、数値の性質から決まる | ほぼ外れない |
| 計算 | 速さ・メモリだけに効き、出力を変えない（キャッシュの数・並列の下限・予算など） | 外れない |
| 近似 | 出力に効くが、計算の手間との兼ね合いで選んだ（ビン数・反復の打ち切り・走査の間引きなど） | まれ |
| 設計 | 被写体で測らずに理屈で選んだ（安全側の上限・他の定数と揃える値・仕様の境目など） | ありうる |
| 実測 | 手元の被写体・ケースで試して選んだ | いちばん疑う |
| 不明 | 値の由来がコメントにもコミットにも残っていない（初版からある・機能の効果だけ測った、など） | 実測と同じく疑う。測り直すか、理屈で置き直す |

2026-10-24 の分類は、コメントと各定数を入れたコミットのメッセージ（`git log -S`）を読んで付けた。値そのものを
比べた・観察から置いた記録があるものだけを「実測」、記録が無いものを「不明」にしている。

## 段

「段」の欄は、その定数が選択・自動調整の段の 1 つの中だけで使われるときの記号。段ごとに外して測った結果と
突き合わせるために書く。複数の段や段の外（再着色など）でも使うものは「—」。

| 記号 | 段 | 記号 | 段 |
|---|---|---|---|
| BF | 明部の免除 | EV | 証拠（AI 提案）を使う導出全体 |
| SF | 暗部の免除 | NM | スポイト色の地色への正規化 |
| MS | 複数サンプルの和集合 | AS | 自動トーン抽出 |
| DG | 暗い無彩サンプルの明るさ盲 | FC | 色相の近い隣接部品で tolerance を打ち切る |
| CG | 微 tint の無彩サンプルの彩度整合ゲート | HE | 証拠のツヤ署名でハイライト補助を点ける |
| QS | 量子化のならし | EG | 証拠ドメインからのゲート |
| CC | 彩度天井ゲート | XS | 証拠による補完 |
| EN | 閉領域の中性ツヤ復帰 | TB | tolerance の上限（証拠を覆える最小値の倍） |
| HP | ハイライトの空間伝播 | CV | 閉ループ検証 |
| HB | ハイライト帯の成長 | FF | 連結成分アンカリング全体 |
| EH | 閉領域ハイライト復帰 | CR | 連結成分のコア規則 |
| SG | 包絡ゲート: 陰影の明度下限 | PS | 包絡ゲート: 別部品の彩度上限 |
| PH | 包絡ゲート: 別部品の色相幅 | DT | 離れた半透明を外す |
| FH | 小さな穴埋め | BE | 境界の回復 |
| RN | 無彩の塗り先で中性を外す | SA | 無彩の塗り先で内部を固める |

## 足すとき・変えるとき

- 行は `| 定数 | 値 | 種類 | 段 | 根拠 |`。値はソースの初期化式をそのまま（空白は詰めてよい）バッククォートで囲む。
- 根拠には「何の値で、なぜその値か」を短く書く。実測なら何で測ったか（物差し・件数）も。**被写体名・アバター名は
  書かない**（公開リポジトリ）。被写体つきの記録は dev_safe に置き、ここからは素材や性質で書く。
- 計測の段の番号のように意味の無い列挙は、`Ph*` のようなまとめ行にしてよい（種類は 計算 か 定義、値は「N 個」）。

## 集計（2026-10-24 時点）

| 種類 | 数 |
|---|---|
| 実測 | 41 |
| 不明 | 107 |
| 設計 | 62 |
| 近似 | 23 |
| 量子化 | 14 |
| 計算 | 27 |
| 定義 | 14 |

計測の段の番号（`Ph*` 11 個・`Sp*` 30 個）はまとめ行なので数に入れていない。
この表はテストで突き合わせない（行を足したら手で直す）。

## 名前の無い小数の数（増やさない）

定数の宣言の外にある小数のリテラルのうち、0・1・2・3・4・0.5・255・256 と 1e-4 以下の ε を除いた数。
テストはこの数と一致することを確かめる。調整した値を直書きで足すと増えて落ちる（名前を付けて上の台帳へ）。
整数（`Mathf.Max(50, …)` のような画素数の床）は添字やループと見分けられないので数えていない。

多いのは 2 つ。`PixelProcessor.ColorSpace.cs` の 43 は sRGB・OkLab の変換係数（定義）。`ZoneAutoTuner.cs` の 40 は
ほとんどが `MergeAnalyzed` の段階関数（彩度の厳しさ・グレー判定のしきい値・模様保持・エッジの柔らかさ・影の彩度下限・
ハイライト補助の初期値などを、サンプルの統計から階段状に決める）で、由来の記録が無く、そのままゾーンへ出荷される。
名前の付いた定数より、こちらが「由来の分からない調整値」としては大きい（2026-10-24 の分類で確認）。

| ファイル | 数 |
|---|---|
| `Code/Core/ColorZone.cs` | 6 |
| `Code/Core/ColorZone.Match.cs` | 6 |
| `Code/Core/HighlightSampleCorrector.cs` | 0 |
| `Code/Core/PixelProcessor.Achroma.cs` | 0 |
| `Code/Core/PixelProcessor.ColorSpace.cs` | 43 |
| `Code/Core/PixelProcessor.cs` | 6 |
| `Code/Core/PixelProcessor.Decontam.cs` | 1 |
| `Code/Core/PixelProcessor.FloodFill.cs` | 1 |
| `Code/Core/PixelProcessor.Highlight.cs` | 6 |
| `Code/Core/PixelProcessor.Hsv.cs` | 0 |
| `Code/Core/PixelProcessor.MixtureBand.cs` | 0 |
| `Code/Core/PixelProcessor.Palette.cs` | 2 |
| `Code/Core/PixelProcessor.Recolor.cs` | 6 |
| `Code/Core/PixelProcessor.Selection.cs` | 2 |
| `Code/Core/UvChartMap.cs` | 0 |
| `Code/Core/ZoneAutoTuner.cs` | 40 |
| `Code/Core/ZoneAutoTuner.Evidence.cs` | 7 |
| `Code/Core/ZoneAutoTuner.Normalize.cs` | 0 |
| `Code/Core/ZoneAutoTuner.Tolerance.cs` | 5 |
| `Code/Core/ZoneAutoTuner.Verify.cs` | 0 |
| `Code/MaskSuggest/Ops/MaskSuggestGranularity.cs` | 0 |
| `Code/MaskSuggest/Ops/SamCoordMapper.cs` | 0 |
| `Code/MaskSuggest/Ops/SamImageOps.cs` | 0 |
| `Code/MaskSuggest/Ops/SamMaskPostprocess.cs` | 0 |
| `Code/MaskSuggest/Ops/SamMaskRefine.cs` | 3 |
| `Code/MaskSuggest/Ops/SamZoomOps.cs` | 0 |

## `Code/Core/ColorZone.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `HighlightValueMin` | `0.80f` | 不明 | — | ハイライト候補の明度下限。導入時の記録は「高明度 V>0.80」だけで、計測なし |
| `HighlightSaturationMax` | `0.20f` | 不明 | — | ハイライト候補の彩度上限の絶対床。導入時に計測記録なし |
| `HighlightSaturationMaxSampleFrac` | `0.5f` | 実測 | — | 高彩度の青い靴のツヤが S0.2〜0.5 に分布した観察から、サンプル彩度の半分を上限にした |
| `HighlightRelaxedSatMin` | `0.02f` | 不明 | — | ハイライト補助の彩度ランプの始点。通常マッチの彩度床 0.02 を写した値 |
| `HighlightRelaxedSatRamp` | `0.08f` | 不明 | — | 同じランプの幅。通常マッチの satRamp 床 0.08 を写した値 |
| `MatchStrengthFloor` | `0.05f` | 不明 | — | これ以下を未選択とみなす strength 床。局所定数 4 個を一本化したもので、由来の記録なし |
| `SaturationGuardActiveSourceSat` | `0.40f` | 不明 | — | 彩度ガードが働く源色彩度の下限。ガード量は感度解析したが、この値は未掃引 |
| `SaturationGuardFractionScale` | `0.30f` | 不明 | — | ガードの床＝源色 S×この値×ガード量。値そのものの計測記録なし |
| `ChromaGateActivateSat` | `0.02f` | 実測 | — | 微 tint とみなす彩度の境界。圧縮ノイズは S≈0.01 台、見える tint は S≥0.02 という観察 |
| `ChromaGateFloorFrac` | `0.5f` | 不明 | — | 中性すぎとみなす相対床（サンプル彩度の半分）。効果は測ったが値の根拠なし |
| `ChromaGateFloorCap` | `0.02f` | 実測 | — | 床の絶対上限。白い布の地 S0.02〜0.05、純白背景 S≲0.012 の観察から |
| `ChromaGatePenalty` | `1.0f` | 設計 | — | 加算距離を tolerance 1 個分にし、pS=0 なら確実に選択の外へ落とす |
| `ChromaCeilSampleFrac` | `3.0f` | 不明 | — | 無彩サンプルの彩度天井の倍率。値の根拠も掃引の記録もなし |
| `ChromaCeilAbs` | `0.04f` | 不明 | — | 彩度天井の絶対床（真の無彩サンプル用）。値の根拠の記録なし |
| `ChromaCeilPenalty` | `1.0f` | 設計 | — | 緩和マッチ側の加算距離の上限。彩度整合ゲートと同じ tolerance 1 個分 |
| `AchromaEdgeSoftness` | `0.7f` | 不明 | — | グレーモードの AA 縁ソフトランプ幅（tolerance 比）。効果は測ったが値の根拠なし |
| `CoreMatchDistance` | `0.14f` | 実測 | CR | 連結成分の確信コアの固定半径。複数被写体の掃引で確定と記録あり |
| `InvSqrt3` | `0.57735027f` | 定義 | — | RGB 単位立方体の対角 √3 で距離を [0,1] に正規化する 1/√3 |
| `ForgivenessHueGate` | `0.15f` | 不明 | — | 陰影・ツヤ免除で同色相とみなす色相距離の上限。初期実装からあり、計測記録なし |
| `ShadowValueThresholdFrac` | `0.75f` | 不明 | SF | 影とみなす明度（サンプル V 比）。初期実装からあり、計測記録なし |
| `HighlightValueHeadroomFrac` | `0.25f` | 設計 | — | 明部免除の始まり。影側 0.75 の鏡像（1−0.75）として決めた |
| `HighlightHueCapFloor` | `0.05f` | 不明 | — | ハイライト色相帯の下限。淡色の隣接布への誤マッチ対策として導入、計測なし |
| `HighlightHueCapTolFrac` | `0.3f` | 不明 | — | ハイライト色相帯の tolerance 比。上と同時に導入、計測記録なし |
| `ChromaConfidenceSpan` | `0.10f` | 不明 | — | 色相の確信度が 0→1 に立ち上がる彩度の幅。由来の記録なし |
| `HueValueConfLo` | `0.05f` | 不明 | — | 暗い色の色相を信用し始めるサンプルの明度。初期実装からあり、記録なし |
| `HueValueConfSpan` | `0.15f` | 不明 | — | 上から色相の確信度が 1 になるまでの明度の幅（V 0.05〜0.20）。初期実装からあり、記録なし |
| `ForgivenessRangeFrac` | `0.6f` | 不明 | — | 陰影・ツヤ免除ランプの幅（使えるレンジに対する比）。初期実装からあり、計測記録なし |
| `HueReliableSatFrac` | `0.35f` | 不明 | — | 色相を信用する彩度の下限比。自動トーン抽出の彩度帯から流用、値の掃引記録なし |
| `BrightDistanceForgivenessMin` | `0.3f` | 不明 | BF | 明部の距離短縮の下限（最大 70%）。廃止した暗部側 0.3 と対称に置いた |
| `GrayModeBaseChromaThreshold` | `0.30f` | 不明 | — | 真っ暗なサンプルでグレーモードに入る彩度上限。初期実装からあり、記録なし |
| `GrayModeChromaConfidenceRamp` | `0.20f` | 不明 | — | グレーモードしきい値が通常値へ戻るサンプル明度。初期実装からあり、記録なし |
| `GrayModeDarkSampleValue` | `0.3f` | 不明 | — | 暗いサンプルとみなす明度の上限。初期実装からあり、計測記録なし |
| `GrayHighlightHeadroom` | `0.55f` | 不明 | — | 暗い無彩サンプルで罰しない明るさの余白。合成グラデで確かめただけ |
| `GrayLumExcessWeight` | `1.2f` | 設計 | — | (1−0.55)×重み>0.40（無彩 tol 上限）で白を弾けるよう、式から決めた |

## `Code/Core/HighlightSampleCorrector.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `MinSampleSat` | `0.20f` | 不明 | — | 灰色寄りの源色では補正しない彩度下限。帯成長の 0.20 と同じ趣旨、記録なし |
| `BodySatFrac` | `0.50f` | 不明 | — | 地色とみなす彩度下限（源色比）。既定 OFF の機能で、計測記録なし |
| `HueBand` | `0.15f` | 不明 | — | 地色とみなす色相帯。免除の色相ゲート 0.15 と同じ値、計測記録なし |
| `HighlightGatePct` | `0.75f` | 不明 | — | スポイトの V が地色の P75 より上のときだけ補正。既定 OFF、計測記録なし |
| `TargetPct` | `0.60f` | 不明 | — | 補正後の wash しきい＝地色 V の P60。既定 OFF、計測記録なし |
| `MinBodyPixels` | `100` | 設計 | — | 地色統計に要る最小画素数。再着色アンカーの AnchorMinPixels とそろえた |

## `Code/Core/PixelProcessor.Achroma.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `AchromaSampleC` | `0.06f` | 不明 | — | サンプルを無彩とみなす OkLab 彩度の境。値を選んだ記録なし |
| `AchromaTargetC` | `0.06f` | 不明 | — | 塗り先を無彩とみなす OkLab 彩度の境。サンプル側と同値。選んだ記録なし |
| `AchromaFormGain` | `1.5f` | 実測 | — | 無彩パスの陰影の増幅率。ノイズ増幅が 2.5 で 1.78 倍・1.5 で 1.41 倍、形は保つと測って下げた |
| `AchromaFormOffset` | `0.16f` | 実測 | — | 黒側の余白・白側の余白の上限。基準を P80 にした際、明部の明るさを測って 0.13→0.16 |
| `AchromaRefPercentile` | `0.80f` | 実測 | — | 成分の地色基準の L 分位。中央値では暗い縁が混ざり模様ごとにばらつき、P80 で std 0.074→0.024 |
| `AchromaHeadroomPercentile` | `0.90f` | 実測 | — | 白側の余白を測る明部の分位。P95 では細い文字の縁が入り余白が残ったと実測 |
| `AchromaRegionCoreThr` | `0.5f` | 不明 | — | 領域の L 中央値に使う strength の下限。選んだ記録なし |
| `AchromaNeutralRejectWeightMin` | `0.5f` | 設計 | RN | 中性の除去を動かす無彩重みの下限（重みの半分）。呼び出し側の発動条件だけで使う |
| `NeutralRejectActiveSourceSat` | `0.40f` | 設計 | RN | 有彩サンプルとみなす HSV 彩度。照合の彩度ガードの作動境 0.40 を流用 |
| `NeutralRejectFloorFrac` | `0.07f` | 実測 | RN | 中性とみなす相対彩度の床。模様上の淡い色が残るのを見て 0.30→0.10→0.07 と下げた |
| `NeutralRejectRampFrac` | `0.5f` | 設計 | RN | 床の手前のフェード開始（床の半分）。二値の斑を消すための連続化 |
| `NeutralRejectProtectRadius` | `2` | 不明 | RN | 高彩度コアの周りを保護する距離（AA 縁の幅）。選んだ記録なし |
| `matchThr` | `ColorZone.MatchStrengthFloor` | 設計 | RN | 選択済みとみなす strength の床 0.05 の共有定数の別名 |
| `matchThr` | `ColorZone.MatchStrengthFloor` | 設計 | SA | 同上（内部固め側の別名） |
| `ClearChunk` | `1 << 18` | 計算 | — | 地図のゼロ埋めを並列に分ける塊。出力に効かない |

## `Code/Core/PixelProcessor.ColorSpace.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `LinToSrgbLutSize` | `4096` | 近似 | — | 線形→sRGB の表の分割数。Pow 版と 98304 色の往復でバイト差 0 を確認 |
| `s_srgbToLinearLut` | `BuildSrgbToLinearLut()` | 定義 | — | sRGB の 256 段 → 線形の表（sRGB の変換式から作る） |
| `s_linearToSrgbLut` | `BuildLinearToSrgbLut()` | 定義 | — | 線形 → sRGB の表（sRGB の変換式から作る） |
| `GamutEps` | `1e-4f` | 量子化 | — | 線形 RGB の丸め誤差を範囲内とみなす幅 |
| `GamutBisectSteps` | `14` | 近似 | — | 彩度を縮める率の二分探索の回数。分解能 1/16384 で 8bit 出力には十分 |

## `Code/Core/PixelProcessor.Decontam.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `AchromaFringeMatchRadius` | `2` | 実測 | — | フチ消しをマッチ境界の外 2px に限る。tolerance の直外 1〜2px に残る観察から |
| `AchromaFringeMinAlpha` | `0.05f` | 設計 | — | 地色の残りがこれ未満なら触らない。残る色みは最大 13 階調ほどで見えない |
| `AchromaFringeMaxAlpha` | `0.70f` | 不明 | — | これを超える地色寄りの画素は除外して白拒否を保つ。0.70 の記録なし |
| `AchromaFringeBgRadius` | `4` | 不明 | — | 背景色を推定する窓の半径。最初から 4 で、選んだ記録なし |
| `AchromaFringeExclusionMargin` | `AchromaFringeMatchRadius + AchromaFringeBgRadius` | 設計 | — | 無彩フチ消しが除外マスクを読む最大の余白。書く範囲と BG を集める範囲の和 |
| `DecontamInteriorBgFrac` | `0.08f` | 実測 | — | 背景密度がこれ未満なら内部として固める。観察した偽ドナー約 2% と 1px 隙間の 14% の間 |
| `DecontamDegenEps` | `1f` | 量子化 | — | sample と背景の差の 2 乗の下限（約 1 階調）。これ未満では α が定まらない |
| `DecontamLineDistSqMax` | `3000f` | 不明 | — | 混色の線分からの距離² の上限（各 ch 約 31 階調）。由来の記録なし |
| `DecontamDirectCostRatioFull` | `32` | 計算 | — | 直接足す経路への切り替えの目安。速さの損益分岐 45 の手前。出力は同じ |
| `DecontamDirectCostRatioSolid` | `8` | 計算 | — | 密度だけの経路の切り替えの目安。損益分岐 13 の手前。出力は同じ |
| `FringeBgRadius` | `AchromaFringeBgRadius` | 設計 | — | AchromaFringeBgRadius のローカルな別名 |
| `Band` | `64` | 計算 | — | 累積和の列方向を並列に分ける帯の幅。整数の加算で出力に効かない |
| `MixSegment` | `32` | 計算 | — | 早い打ち切りをまとめて判定する行の区間。出力はビット単位で同じ |

## `Code/Core/PixelProcessor.FloodFill.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `CoreAbsoluteFloor` | `0.6f` | 不明 | CR | matchConf が無いときのコア閾値の床。製品経路では matchConf が常にあり、ここに来ない |
| `ShadowFloorKeepFrac` | `0.5f` | 実測 | — | 包絡の内側が過半なら成分を残す。57 件で 1/4〜0.75 を試し、崩れる 0.75 から離した |
| `PartHueKeepFrac` | `0.25f` | 実測 | PH | 色相帯は 1/4 で残す。過半にすると茶髪の房が落ちた（再現率 1.000→0.965） |

## `Code/Core/PixelProcessor.Highlight.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `HlPropTile` | `64` | 計算 | HP | ハイライト伝播を並列にするタイルの一辺。出力はビット不変 |
| `HlBandCoreThreshold` | `0.90f` | 不明 | HB | 帯成長の出発点（本体）とみなす strength の下限。導入時に記録なし |
| `HlBandAxisEps` | `0.10f` | 不明 | — | sample→白 軸からの許容残差（RGB）。導入時に記録なし |
| `HlBandMinSampleSat` | `0.20f` | 不明 | — | 帯成長と閉領域復帰を無効にする源色彩度の下限。記録なし |
| `HlBandMinSatFrac` | `0.15f` | 不明 | — | 帯候補の彩度下限（源色比）。「白素材への流入防止」とだけ記録 |
| `EnclosedHlMinPixels` | `64` | 設計 | EH | 8×8 未満は穴埋めが受け持つので外す、最小画素数 |
| `EnclosedHlMaxSelFrac` | `0.05f` | 不明 | EH | 芯は本体の数% という見立てによる成分の上限（選択量比）。掃引なし |
| `EnclosedHlSatFrac` | `0.60f` | 設計 | EH | 芯を脱彩とみなす彩度比。自動調整のツヤ署名 EvSheenSatFrac と同じ値にそろえた |
| `EnclosedHlRingRadius` | `4` | 実測 | EH | 1px 隣は AA 混色で地色を表さないという観察から、2〜4px 外側をリングにした |
| `matchThr` | `ColorZone.MatchStrengthFloor` | 設計 | EH | MatchStrengthFloor の局所別名 |
| `hueGate` | `ColorZone.ForgivenessHueGate` | 設計 | EH | ForgivenessHueGate の局所別名 |
| `R` | `EnclosedHlRingRadius` | 設計 | EH | EnclosedHlRingRadius の局所別名 |

## `Code/Core/PixelProcessor.MixtureBand.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `MixBandRadius` | `2` | 設計 | — | 混色帯の幅（AA の幅）。透明からこの距離以内も面にしない |
| `MixSolidStrength` | `DecontaminationInteriorThreshold` | 設計 | — | 確かな選択の下限。デコンタミの内部判定 0.97 と揃えた |
| `MixMinChroma` | `24f` | 設計 | — | α を色度で測れる素材の最小色度。8bit の 1 階調が α の 4% を超えない境 |
| `MixMinSeparation` | `24f` | 設計 | — | 素材と背景の色度の最小差。MixMinChroma と同じ理屈 |
| `MixResidAbs` | `8f` | 不明 | — | 色度の線分からのずれの絶対許容（量子化と雑音のぶん）。8 の記録なし |
| `MixResidFrac` | `0.25f` | 不明 | — | 有彩の面のときに許す向きのずれの比（約 14°）。値を選んだ記録なし |
| `MixHueFrac` | `0.10f` | 設計 | — | 無彩との混色で許す向きのずれ（約 6°）。無彩と混ぜても色相は変わらない理屈 |
| `MixNoiseK` | `2f` | 設計 | — | 素材の色度のばらつき（標準偏差）に掛ける倍率。2σ の慣例 |
| `MixLightAbs` | `24f` | 実測 | — | 明るさの合成式からのずれの許容。陰影の勾配が数 px で 20 階調前後という観察から |
| `MixLightFrac` | `0.25f` | 不明 | — | 素材と面の明るさの差に対する許容比。選んだ記録なし |
| `MixFlatChroma` | `12f` | 実測 | — | 面の色度の揃い方と、ほぼ無彩の境。生成り（色度 10 前後）が色づいた観察から |
| `MixAlphaMin` | `AchromaFringeMinAlpha` | 設計 | — | 素材がほぼ乗っていない被覆率。無彩フチ消しの下限 5% と揃えた |
| `MixAlphaPure` | `0.90f` | 不明 | — | これ以上の被覆率は素材そのもの。0.90 の記録なし |
| `MixAlphaOver` | `0.15f` | 不明 | — | 被覆率が 1＋これを超えたら F は出所でない。選んだ記録なし |
| `MixCoreRangeMargin` | `0.03f` | 不明 | — | 内側の画素の射影の最小値と比べるときの余白。選んだ記録なし |
| `MixAsMaterial` | `2f` | 定義 | — | 被覆率 0..1 の外に置いた「素材そのもの」の印 |
| `FlatYesB` | `1` | 計算 | — | 「面か」の覚え書きの値（はい。0 = まだ調べていない）。番号だけ |
| `FlatNoB` | `2` | 計算 | — | 「面か」の覚え書きの値（いいえ）。番号だけ |
| `MixOther` | `0` | 計算 | — | 混色の判定の番号（どれでもない）。番号だけ |
| `MixMixture` | `1` | 計算 | — | 混色の判定の番号（混色）。番号だけ |
| `MixMaterial` | `2` | 計算 | — | 混色の判定の番号（素材そのもの）。番号だけ |
| `MixBackground` | `3` | 計算 | — | 混色の判定の番号（背景）。番号だけ |
| `MixOver` | `4` | 計算 | — | 混色の判定の番号（被覆率が 1 を超える）。番号だけ |
| `EnclDx` | `{ 1, -1, 0, 0, 1, 1, -1, -1 }` | 定義 | — | 8 近傍の x の向き |
| `EnclDy` | `{ 0, 0, 1, -1, 1, -1, 1, -1 }` | 定義 | — | 8 近傍の y の向き |
| `R` | `MixBandRadius` | 設計 | — | MixBandRadius のローカルな別名 |

## `Code/Core/PixelProcessor.Palette.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `PaletteCacheSize` | `2` | 計算 | — | 色の表のキャッシュ枠。縮小段とフル解像度段が互いを追い出さない数 |
| `PaletteLargeLen` | `1 << 20` | 計算 | — | 大きい表を 1 枚までにする画素数の境。メモリだけに効く |
| `Chunk` | `1 << 16` | 計算 | — | 画素のハッシュを並列に取る塊。キャッシュの鍵にだけ効く |
| `Chunk` | `4096` | 計算 | — | 色番号を並列に分ける塊の大きさ |
| `PaletteMaxColorsDivisor` | `4` | 計算 | — | 色数が画素数の 1/4 を超えたら表を作らない。表の有無で出力は同じ（test_palette_parity） |
| `PaletteMaxColorsCap` | `1 << 20` | 計算 | — | 色の表の色数の上限。表の有無で出力は同じ |
| `RecolorMemoSize` | `8192` | 計算 | — | 無彩パスの再着色メモの枠数（2 のべき）。当たり率だけに効く |

## `Code/Core/PixelProcessor.Recolor.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `OklabRemapFullChromaFrac` | `0.35f` | 不明 | — | L 再マップを全量かける彩度比（サンプル比）。効果の検証はあるが値の比較の記録なし |
| `HighlightLMult` | `2.0f` | 設計 | — | 暗い塗り先の明部の上限 tL×2。tL≥0.5 では従来どおりになる値 |
| `HlWashResidEps` | `0.035f` | 実測 | — | 模様とみなす OkLab 残差。実測で模様 ≥0.033 と色相をずらした光点 ≤0.01 が分かれる |
| `HlWashTintKeepLo` | `0.012f` | 実測 | — | 白寄せを保つ色相弧の上限。実測で鏡面ハイライトの色相弧は ≤0.007 |
| `HlWashTintKeepHi` | `0.024f` | 実測 | — | 元色の保持へ切り替える色相弧。実測で色相をずらした光点は ≥0.023 |
| `HlWashKeepWhiteLo` | `0.15f` | 実測 | — | 元色を全部残す白からの距離。光点の芯が実測 ΔE≈0.135 |
| `HlWashKeepWhiteHi` | `0.25f` | 実測 | — | 元色の保持を 0 にする白からの距離。周りのにじみが実測 ΔE≥0.26 |
| `AnchorStrengthMin` | `0.9f` | 不明 | — | アンカーの統計に使うコアマッチの strength の下限。値を比べた記録なし |
| `AnchorMinPixels` | `100` | 設計 | — | 統計に要る最少画素数。ハイライトのサンプル補正と同じ基準 |
| `AnchorBodySatrFrac` | `0.5f` | 不明 | — | 地色とみなす飽和度 C/L の下限（候補の中央値の半分）。選んだ記録なし |
| `AnchorLPct` | `0.90f` | 不明 | — | 代表 L の分位。上位約 10% の鏡面の芯を外す意図。比べた記録なし |
| `AnchorLBandLoPct` | `0.80f` | 不明 | — | 代表 C を取る L 帯の下限の分位。選んだ記録なし |
| `AnchorLBandHiPct` | `0.97f` | 不明 | — | 代表 C を取る L 帯の上限の分位。選んだ記録なし |
| `AnchorMinLSpread` | `0.02f` | 不明 | — | L の P95−P05 がこれ未満なら平坦として補正しない。選んだ記録なし |
| `AnchorSatrHistMax` | `4f` | 近似 | — | 飽和度ヒストグラムの値域の上限。超えた分は最上位のビンに入る |
| `AnchorChromaHistMax` | `0.5f` | 近似 | — | 彩度ヒストグラムの値域。sRGB の OkLab 彩度（約 0.33 まで）を覆う |
| `AnchorCountBudget` | `1 << 21` | 計算 | — | 色ごとの個数を数える配列の合計の上限（8MB）。人数だけが変わり結果は同じ |
| `AnchorRetrySatrFrac` | `0.25f` | 実測 | — | 推定し直しの境。実測で乗り換えたとき 0〜0.20、正しいとき 0.31 以上の間 |
| `AnchorMaterialSatrFrac` | `OklabRemapFullChromaFrac` | 設計 | — | 推定し直しで地色から外す飽和度比。L 再マップの線（OklabRemapFullChromaFrac）と同じ |
| `AnchorMaterialHueCos` | `0.70710678f` | 実測 | — | 推定し直しで外す色相差 45°。正解画素の 97% が 25° 以内という実測に余裕を取った |

## `Code/Core/PixelProcessor.Selection.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `VBlock` | `16` | 計算 | — | ボックス和の縦パスを 16 列ずつまとめてキャッシュラインに合わせる。出力ビット不変 |
| `QuantSmoothLevels` | `1` | 量子化 | QS | 8bit で 1 階調以内の隣だけ平均する（量子化誤差の幅そのもの） |
| `Chunk` | `4096` | 計算 | FH | 穴埋め 2 パス目以降の並列の分割単位。書き込みはパスの最後なので出力は不変 |
| `Chunk` | `4096` | 計算 | BE | 境界回復 2 パス目以降の並列の分割単位。出力は不変 |
| `ChromaCeilProtectRadius` | `12` | 実測 | CC | 素材自身の高彩度の帯を守る半径。実測で GT の高彩度画素の 99%+ を覆う幅 |
| `matchThr` | `ColorZone.MatchStrengthFloor` | 設計 | CC | MatchStrengthFloor の局所別名 |
| `matchThr` | `ColorZone.MatchStrengthFloor` | 設計 | EN | MatchStrengthFloor の局所別名 |
| `R` | `ChromaCeilProtectRadius` | 設計 | CC | ChromaCeilProtectRadius の局所別名 |
| `Inf` | `1 << 28` | 量子化 | CC | 距離変換の「無限大」の番兵。どの距離より大きく、+1 しても桁あふれしない |

## `Code/Core/PixelProcessor.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `Ph*` | 11 個 | 計算 | — | 処理時間の計測の段の番号（PhaseCount を含む）。値は計測の表の位置だけ |
| `Sp*` | 30 個 | 計算 | — | 処理時間の計測の小段の番号。値は計測の表の位置だけ |
| `s_defaultParallelism` | `ComputeDefaultParallelism(Environment.ProcessorCount)` | 計算 | — | 既定の並列数（コア数から決める。出力は並列数によらない） |
| `OpaqueAlphaMin` | `128` | 設計 | — | α の中点（0.5）以上を不透明とみなす仕様の境目 |
| `SubPhaseCount` | `30` | 計算 | — | 計測の小段の数（Sp* の数） |
| `DecontaminationInteriorThreshold` | `0.97f` | 不明 | — | デコンタミが α 分解しない「内部」の strength 下限。導入時から記録なし |
| `GaussianRadiusPerSigma` | `2.5f` | 近似 | — | ガウス核の打ち切り半径（σ の 2.5 倍、裾の質量は約 1%） |
| `EdgeFeatherBlurMin` | `0.01f` | 量子化 | — | これ以下の σ では核がほぼ単位元になるので、ぼかし 0 とみなす境目 |
| `RecolorMinStrength` | `0.001f` | 量子化 | — | 8bit で 1 階調未満の変化しか生まない強度を読み飛ばす境目 |
| `AchromaWeightActiveMin` | `1e-4f` | 量子化 | — | 無彩パスの重みを 0 とみなす ε |
| `fm` | `AchromaFringeExclusionMargin` | 設計 | — | 無彩フチ消しが読む範囲（照合半径＋背景窓）の局所別名 |

## `Code/Core/UvChartMap.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `UvQuant` | `100000f` | 量子化 | — | UV の辺を同じとみなす量子化の刻み 1e-5。float の UV の精度から決まる |
| `UvKeyStride` | `4_000_037L` | 量子化 | — | 量子化した UV (x,y) を long 1 つに詰める素数の係数。\|v\| が約 20 未満なら一意 |
| `PaddingInf` | `int.MaxValue / 2` | 量子化 | — | にじみ代の距離変換で「届かない」を表す番兵。+1 しても桁あふれしない |
| `Inf` | `PaddingInf` | 量子化 | — | PaddingInf を関数の中で別名にしたもの（距離変換の番兵） |

## `Code/Core/ZoneAutoTuner.Evidence.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `EvErodeR` | `2` | 設計 | EV | 証拠の芯の収縮量。残存島の計測の「境界 2px は無害」に揃えた |
| `EvMinErodedPixels` | `256` | 不明 | EV | 収縮後の芯がこれ未満なら細い部位として収縮なしで使う。由来の記録なし |
| `EvMinMaterialFrac` | `0.80f` | 実測 | EV | 無彩地色の汚染門。汚染したセグメント 0.63、他の 13 被写体 0.91 以上を見て間に置いた |
| `EvMinMaterialFracChromatic` | `NormOutlierFrac` | 設計 | EV | 有彩地色の汚染門。裾判定と同じ 1/10。実測で裾 0.002 と通常 0.28 以上の間にあると確認 |
| `EvMinDomainPixels` | `MinNearSampleCount` | 設計 | EV | ドメインの最小標本数。MinNearSampleCount に揃えた |
| `EvCoverTarget` | `0.98f` | 実測 | XS | 補完を始める被覆率。0.2% の取り残しを埋めると別素材と衝突し、8% 未被覆では効いた |
| `EvCoverGainMin` | `0.002f` | 不明 | EV | 1 色足しても被覆が増えないときの打ち切り幅（TB の被覆許容にも流用）。由来の記録なし |
| `EvMaxExtraSamples` | `3` | 近似 | XS | 補完で足す色の上限数（試す候補の数）。由来の記録なし |
| `EvUncoveredBinMinFrac` | `0.002f` | 実測 | XS | 未被覆の V 帯の最小画素数（ドメイン比）。絶対数 64 では小さな縁取り島を拾えなかった |
| `EvUncoveredBinMinCount` | `8` | 不明 | XS | 上の比の絶対下限（ノイズよけ）。由来の記録なし |
| `EvBleedK` | `3f` | 実測 | XS | 補完色の溢れ判定の倍率。縁取りで比 2〜5、柄の色で比≈50 を見て間に置いた |
| `EvDupSep` | `0.03f` | 実測 | XS | 補完候補の重複距離。0.10 では脱彩した明端が 0.08 で棄却されたので細かくした |
| `EvSheenSatFrac` | `0.60f` | 不明 | EV | ツヤ署名の彩度条件（地色 S×0.6 未満）。由来の記録なし |
| `EvSheenValMargin` | `0.05f` | 不明 | EV | ツヤ署名の明度条件（地色 V+0.05 超）。由来の記録なし |
| `EvSheenMinCount` | `50` | 設計 | HE | ツヤ署名でハイライト補助を点ける最小画素数。従来判定の絶対床 50 と同じ値 |
| `EvSheenMinFrac` | `0.001f` | 実測 | HE | 署名の最小面積比。1 島のツヤは芯の 0.1〜数%（靴では 0.5% 未満）という観察の下端 |
| `EvShadowFloorMarginFrac` | `0.25f` | 設計 | EG | 陰影の明度下限の余白（連結域の幅比）。免除の 25% デッドマージンに揃えた |
| `ShadowValueFloorMin` | `1f / AutoToneValueBins` | 量子化 | EG | 明度下限の最小有効値 = V ヒストグラムの 1 bin 幅。導出の分解能未満は無効にする |
| `EvChromaCeilPercentile` | `0.95f` | 設計 | EG | 証拠の彩度・色相包絡のパーセンタイル。tolerance 導出の P95 に揃えた |
| `EvChromaCeilHeadroomFrac` | `0.25f` | 設計 | EG | 包絡の余白（÷0.75）。免除の 25% に揃えた。57 ケースの試算で 0.20〜0.40 は悪化 0 |
| `EvHoleFillMarginFrac` | `0.25f` | 設計 | EG | 穴埋め判定の余白。免除と同じ 25%。実測で 50% は正解の外の塗りが 1.4 倍 |
| `EvTolHeadroom` | `2f` | 実測 | TB | tolerance 上限の余白倍率。57 ケースで 1.5 倍は同素材の島が削れ、2 倍では削れない |
| `EvTolBisectSteps` | `6` | 近似 | TB | 上限探索の二分の回数。6 回で導出値の 1/64 の分解能 |
| `VB` | `AutoToneValueBins` | 近似 | EV | ドメインの V ヒストグラムの分割（AutoToneValueBins の別名） |
| `VB` | `AutoToneValueBins` | 近似 | XS | 未被覆代表の V 帯の分割（AutoToneValueBins の別名） |
| `R` | `EvErodeR + 1` | 定義 | EV | 縁クリックで最寄りの芯を探す半径 = 収縮量 + 1（境界帯の幅から決まる） |
| `SB` | `256` | 近似 | EG | 彩度パーセンタイルのビン数（≈8bit 相当）。結果は bin の上端に丸まる |
| `HB` | `512` | 近似 | EG | 色相のずれのビン数（1 bin≈0.35°）。結果は bin の上端に丸まる |
| `Chunk` | `4096` | 計算 | EV | 被覆を数える並列区間の長さ。整数の和なので出力は変わらない |

## `Code/Core/ZoneAutoTuner.Normalize.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `NormSatBins` | `16` | 近似 | NM | 正規化の彩度の分割。bin 内は実色の平均を取るので粗くてよい |
| `NormValBins` | `32` | 近似 | NM | 正規化の明度の分割。粗いほど連結が切れにくい |
| `NormSatFloorFrac` | `0.95f` | 量子化 | NM | 候補の彩度下限 = クリック S×0.95（8bit 量子化ぶんの緩み。1 階調より広め） |
| `NormMinShift` | `0.02f` | 不明 | — | 実質同色とみなす RGB 距離（これ未満は寄せない）。縁クリックの判定にも流用。由来の記録なし |
| `NormOutlierFrac` | `0.10f` | 設計 | — | クリック帯が最頻帯のこの割合未満なら裾。ツヤ・柄 1% 前後と陰影「数分の 1」の間 |
| `NormEdgeFrac` | `0.10f` | 不明 | NM | トーン連結域の端とみなす外側の割合。機能の実測（悪化 0）のみで、10% の由来は記録なし |
| `NormTintedChromaticPopFrac` | `0.5f` | 設計 | NM | 寄せ直しの母集団の下限 = 過半。実測で採用 0.59〜0.81、不採用 0.18・0 の間 |
| `NormAchromaSatBins` | `16` | 近似 | NM | 無彩クリックの彩度の分割（帯幅で正規化）。tint の有無を分けられる分解能 |
| `NormAchromaValBins` | `128` | 近似 | NM | 無彩クリックの明度の分割。V 0.95 と 1.00 を別の bin にできる分解能 |

## `Code/Core/ZoneAutoTuner.Tolerance.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `DistBins` | `600` | 近似 | — | 距離ヒストグラムの分割数（分解能 0.0025） |
| `DistMax` | `1.5f` | 設計 | — | 距離ヒストグラムの値域の上限。無彩の RGB 距離（≤1）と近傍窓内の HSV 距離を覆う |
| `AchromaSampleSatMax` | `0.15f` | 不明 | — | 無彩の導出経路へ分ける源色彩度。全段の有彩・無彩の分岐に使う。由来の記録なし |
| `AchromaClusterSatMax` | `0.20f` | 不明 | — | 無彩クラスタに入れる画素の彩度上限（有彩を除く）。由来の記録なし |
| `AchromaVWindow` | `0.30f` | 設計 | — | 無彩クラスタの明度窓（想定シェーディング幅）。NearValDist と同じ 0.30 |
| `AchromaPercentile` | `0.95f` | 設計 | — | 無彩 tolerance の距離パーセンタイル（P95）。有彩経路と同じ |
| `AchromaMargin` | `0.04f` | 不明 | — | 無彩 tolerance の P95 への上乗せ。由来の記録なし |
| `AchromaTolMin` | `0.12f` | 不明 | — | 無彩 tolerance の床。旧マスク認識経路・従来式の 0.12 を継いだ。由来の記録なし |
| `AchromaTolMax` | `0.40f` | 不明 | — | 無彩 tolerance の上限。旧マスク認識経路の 0.40 を継いだ。由来の記録なし |
| `ChromaPercentile` | `0.95f` | 設計 | — | 有彩 tolerance の距離パーセンタイル（P95）。無彩経路に揃えた |
| `ChromaMargin` | `0.03f` | 不明 | — | 有彩 tolerance の P95 への上乗せ。旧マスク認識経路と同じ 0.03。由来の記録なし |
| `ChromaTolMin` | `0.08f` | 実測 | — | 有彩 tolerance の床。暗く鮮やかな青の素材で導出 0.13・最適≈0.08 の実測から 0.12 を 0.08 に |
| `ChromaTolMax` | `0.22f` | 設計 | — | 有彩の上限（色相≈79°）。0.40 で暖色の低彩度が張り付いた観察から、理屈で下げた |
| `ChromaClusterSatFrac` | `0.35f` | 設計 | — | 有彩クラスタの相対彩度床（S×0.35 未満は無彩寄り）。HueReliableSatFrac と同じ値・同じ意味 |
| `ForeignHueBins` | `200` | 近似 | — | core の色相ずれのヒストグラム分割（[0,0.10] を 200 分割） |
| `CoreHueWindow` | `0.03f` | 不明 | — | core 抽出の色相窓。由来の記録なし |
| `CoreSatWindow` | `0.15f` | 不明 | — | core 抽出の彩度窓。由来の記録なし |
| `CoreValWindow` | `0.20f` | 不明 | — | core 抽出の明度窓。由来の記録なし |
| `ForeignGateK` | `2.5f` | 不明 | — | 同パーツの色相ゲート = K×core の広がり + 床、の係数。合成 1 ケースで機能を確認しただけ |
| `ForeignGateFloor` | `0.015f` | 不明 | — | 色相ゲートの式の加算床。由来の記録なし |
| `ForeignGateMin` | `0.03f` | 不明 | — | 色相ゲートの下限。色相幅の下限にも流用（0.02〜なしでも悪化 0）。元の由来は記録なし |
| `ForeignRatioThresh` | `0.25f` | 不明 | FC | foreign/core 比がこれを超えたら隣接の別パーツと判定。由来の記録なし |
| `ForeignMinCount` | `30` | 不明 | FC | foreign の画素数の下限（ノイズよけ）。由来の記録なし |
| `ForeignLowFloor` | `0.04f` | 不明 | — | 打ち切ったときの tolerance の床（通常の床より低い）。CV の縮小の床にも流用。由来の記録なし |
| `ForeignCapEps` | `0.005f` | 不明 | FC | foreign 距離の P10 の手前に取る余白。由来の記録なし |
| `CoreSpreadDefault` | `0.02f` | 不明 | — | core が少なく P90 を取れないときの色相の広がり。由来の記録なし |
| `AutoToneHueBand` | `0.06f` | 不明 | — | 同パーツとみなす色相帯。トーン抽出・正規化・証拠ドメインで共有。由来の記録なし |
| `AutoToneSatFrac` | `ColorZone.HueReliableSatFrac` | 設計 | — | トーン抽出の相対彩度床。色相が信用できる線（HueReliableSatFrac）に揃えた |
| `AutoTonePartSatRelax` | `0.85f` | 不明 | — | 彩度バンドの下限 = 近傍の彩度 P10×0.85。バンドが要ることは実測済み、0.85 の由来は記録なし |
| `AutoToneDarkPct` | `0.12f` | 不明 | AS | 暗部の代表色を取る明度パーセンタイル。由来の記録なし |
| `AutoToneMidPct` | `0.50f` | 設計 | AS | 中間の代表色を取る明度パーセンタイル（中央値） |
| `AutoToneLightPct` | `0.85f` | 不明 | AS | 明部の代表色を取る明度パーセンタイル。由来の記録なし |
| `AutoToneMinSep` | `0.10f` | 不明 | AS | 代表色どうし・クリック色との重複判定距離。由来の記録なし |
| `AutoToneMinPixels` | `200` | 不明 | — | トーン抽出・正規化の母集団の最小画素数。由来の記録なし |
| `AutoToneValueBins` | `64` | 近似 | — | トーン抽出の V 分割。連結域・明度下限・ツヤ救済の bin の単位にもなる |
| `AutoToneSatBins` | `64` | 近似 | — | 近傍の彩度 P10 を取るヒストグラムの分割 |
| `AutoToneGapFloorFrac` | `0.0005f` | 不明 | — | V ヒストグラムの「空の谷」を判定する床（総数比）。由来の記録なし |

## `Code/Core/ZoneAutoTuner.Verify.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `HighlightGrowthMaxFrac` | `0.15f` | 実測 | CV | ハイライト系だけで増える画素の上限比。本物のツヤ +1〜3%、肌の巻き込み +80% の間 |
| `VerifyMinBaseSelected` | `64` | 不明 | CV | 検証を行う基礎選択の最小画素数。MinNearSampleCount と同じ 64。由来の記録なし |
| `BrightForgiveMaxFrac` | `0.15f` | 設計 | CV | 明部の免除だけで増える画素の上限比。成長テストと同じ 15% |
| `EvidenceKeepFrac` | `0.97f` | 不明 | CV | tolerance を縮めるときに保つ近傍の被覆率（導出時比）。由来の記録なし |
| `TolShrinkSteps` | `{ 0.8f, 0.65f, 0.5f }` | 近似 | CV | 明部の免除が多すぎるとき tolerance を下げて試す倍率の候補（3 段） |
| `SheenGrowMaxAddedFrac` | `0.05f` | 不明 | CV | ツヤ救済で許す追加画素の上限比。「ツヤは数%」という理屈だけで、測定の記録なし |
| `SheenSigMinFrac` | `0.90f` | 不明 | CV | 追加画素のうちツヤ署名が占めるべき割合。由来の記録なし |
| `SheenMinAddedCount` | `24` | 不明 | CV | これ未満の追加は証拠不足として採らない。由来の記録なし |
| `SheenVConnMarginBins` | `2` | 設計 | CV | 連結域の上端から許す V の余白。連結歩行の橋渡し幅（2 bin）に揃えた |
| `SheenTolGrowSteps` | `{ 1.15f, 1.3f, 1.5f }` | 近似 | CV | 明部のツヤの取りこぼしを救うとき tolerance を上げて試す倍率の候補（3 段） |

## `Code/Core/ZoneAutoTuner.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `SaturationGuardActivateSS` | `0.95f` | 実測 | — | 彩度ガードの自動提案を始める源色彩度。S0.96・0.50 の素材で悪化、S1.0 で改善を見て絞った |
| `SaturationGuardMinAtActivate` | `0.5f` | 不明 | — | 発動境界でのガード値（補間の始点）。感度解析は発動条件の方で、この値の由来は記録なし |
| `SaturationGuardMaxAtHighSS` | `0.8f` | 不明 | — | 源色 S=1.0 でのガード値（補間の終点）。由来の記録なし |
| `HistogramBins` | `32` | 近似 | — | 従来統計（H・S・V）のヒストグラム分割。P10・P90 の分解能は 1/32 |
| `NearHueDist` | `0.10f` | 不明 | — | 近傍窓の色相の半幅。ほぼ全導出の母集団を決める。初版の値で由来の記録なし |
| `NearSatDist` | `0.20f` | 不明 | — | 近傍窓の彩度の半幅。初版の値で由来の記録なし |
| `NearValDist` | `0.30f` | 不明 | — | 近傍窓の明度の半幅（想定シェーディング幅）。初版の値で由来の記録なし |
| `MinTextureDim` | `32` | 不明 | — | これ未満の寸法は解析せずヒューリスティック既定を返す。由来の記録なし |
| `MinNearSampleCount` | `64` | 不明 | — | 統計を信用する最小画素数。近傍・クラスタ・ドメインの床に広く流用。由来の記録なし |
| `NoProgress` | `-1f` | 定義 | — | 進捗を報告しない印（進捗は 0〜1 なので負値）。出力に関係しない |

## `Code/MaskSuggest/Ops/SamImageOps.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `InputSize` | `1024` | 定義 | — | SAM エンコーダの入力正方形の一辺（モデル仕様） |
| `Mean` | `{ 123.675f, 116.28f, 103.53f }` | 定義 | — | SAM の前処理の画素平均（参照実装の pixel_mean） |
| `Std` | `{ 58.395f, 57.12f, 57.375f }` | 定義 | — | SAM の前処理の画素標準偏差（参照実装の pixel_std） |

## `Code/MaskSuggest/Ops/SamMaskPostprocess.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `LowRes` | `256` | 定義 | — | SAM デコーダが出す低解像度ロジットの一辺（モデルの出力仕様） |
| `DefaultFloodRejectFrac` | `0.40f` | 設計 | — | 背景に流れた候補を捨てる面積比。試作で仮に置いた値で、掃引の記録は無い |
| `StabilityOffset` | `1.0f` | 定義 | — | SAM 標準の stability score のオフセット（参照実装の既定値） |
| `StabilityThreshold` | `0.80f` | 実測 | — | 安定とみなす下限。15 被写体×3 クリックで 0.70〜0.90 の結果が同じだったので、その中央 |
| `S` | `SamImageOps.InputSize` | 定義 | — | InputSize を関数の中で別名にしたもの（256→1024 に拡大するときの格子） |

## `Code/MaskSuggest/Ops/SamMaskRefine.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `MinSamples` | `16` | 設計 | — | 確信領域の平均色を信用する最小画素数。統計の下限として置いた値で、計測の記録は無い |
| `MinCellSamples` | `4` | 不明 | — | 1 セルを色モードとして扱う最小画素数。4 という値の根拠は書かれていない |
| `MaxWindowRadius` | `8` | 不明 | — | 統計が足りないときに窓を 2→8 セルまで広げる上限。確かめたのは合成図形だけ |
| `MinWindowRadius` | `2` | 不明 | — | 近傍グリッドの窓の最初の半径（5×5 セル）。ここから 2 セルずつ広げる。2 を選んだ記録は無い |
| `FringeWindowRadius` | `2` | 不明 | — | 房の成長で背景・生地の色を集める窓の半径（5×5 セル）。選んだ記録は無い |
| `AaBlendMin` | `0.10f` | 設計 | — | 混色としてマスクに含める混合率の下限。10% 未満は再着色しても見分けられない |
| `AaResidFracSq` | `0.35f * 0.35f` | 不明 | — | 混色の線分から外れてよい量の上限（線分長の 0.35 倍）。0.35 の根拠は書かれていない |
| `AaMinContrastSq` | `24f * 24f` | 不明 | — | 混合軸を作れる最小のコントラスト（RGBA 距離 24）。「継ぎ目が見えない」線 |
| `AaMaxPasses` | `3` | 実測 | — | AA 包含の反復の上限。ぼかし縁が実測で 2d 前後なので 3 回で収束する |
| `FringeColorThresh` | `26f` | 実測 | — | 房を「背景から遠い」とみなす色距離。房のある被写体を横断する試作で決めた |
| `ThinBandFracMin` | `0.5f` | 設計 | — | 「細い成分」の判定。画素の半分以上が帯にあれば幅はおよそ 4d 以下 |
| `PeelSameColorSq` | `AaMinContrastSq` | 設計 | — | 地色と見分けが付かない色の差。AaMinContrastSq に揃えた |
| `PeelDistinctSq` | `4.0 * PeelSameColorSq` | 設計 | — | 地色とはっきり違う色の差。「見分けが付かない」線の 2 倍の距離 |
| `PeelMinDetailFrac` | `0.25f` | 実測 | — | 剥がす門の細部の割合 1/4。門が要ることは部位提案で実測、値の掃引記録は無い |
| `PeelStepSq` | `PeelSameColorSq / 4.0` | 実測 | — | 地を辿る隣同士の差（距離 12）。落ち影 2〜4 階調と描き込みの縁 60 階調の間 |
| `PeelLocalSameSq` | `2.0 * PeelStepSq` | 実測 | — | 近くの地と同じ色とみなす差（距離約 17）。布の粒の差 11 以内と歯の 22〜26 の間 |
| `GrowReachCells` | `6` | 実測 | — | 描き込みの続きを足す範囲。SAM が端で切る長さの実測（約 5.5d）から 6d |
| `GrowGap` | `2` | 設計 | — | 続きとして連ねる隙間 2px。縁取り 1px と AA 1px を挟む構造から |
| `Inf` | `1 << 28` | 量子化 | — | L1 距離変換で「反対側が無い」を表す番兵。+1 しても桁あふれしない |

## `Code/MaskSuggest/Ops/SamZoomOps.cs`

| 定数 | 値 | 種類 | 段 | 根拠 |
|---|---|---|---|---|
| `MinCropSide` | `256` | 設計 | — | ズームで切り出す辺の下限。辺 256〜1024 で結果がほぼ変わらないことは小さい部品で計測済み |
| `ZoomFactor` | `4` | 不明 | — | クリックした成分の長辺に対する切り出し辺の倍率（周りの文脈を含める）。掃引記録は無い |
| `PositionGridDenom` | `8` | 近似 | — | 切り出し位置を辺の 1/8 の格子に寄せる。近いクリックで埋め込みのキャッシュを当てる |
