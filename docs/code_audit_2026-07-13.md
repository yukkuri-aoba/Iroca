# コードベース監査レポート（2026-07-13）

対象コミット: `b5ddff5`（develop）。バグ・パフォーマンス懸念・仕様の妥当性が不明な箇所を、領域別の並列レビュー（Core アルゴリズム / 自動調整・キャッシュ / UI / Infra・自動化 / ビルド・リリース・テスト基盤）で洗い出し、重大度の高いものは実コード・git 履歴で裏取りした。

- **[裏取り済み]** = 該当コード・履歴・現物を直接確認した項目
- **[報告のみ]** = レビューエージェントの報告で、個別の再確認はしていない項目（行番号・機構の説明は具体的で信頼度は高い）

ファイルは一切変更していない（本ドキュメントの追加のみ）。

---

## 総括 — 最優先の 7 件

| # | 重大度 | 領域 | 要旨 |
|---|--------|------|------|
| A-1 | **HIGH** | Core | **過去の修正 2 件が reset で消失したまま**（暗ターゲット明度キャップ topL、スポイト位置正規化） |
| A-2 | **HIGH** | UI/Export | **上書きエクスポート後にプレビューと実出力が乖離**（再着色の二重適用） |
| A-3 | **HIGH** | Infra | **CleanupOrphans がブランチ切替中などの一時的 GUID 未解決で手描きマスク・セッションを恒久削除** |
| A-4 | **HIGH** | リリース | **docs/index.json 0.2.0 の SHA/URL/実資産の 3 者不一致が未解消**（公開すると VCC でインストール不能） |
| A-5 | HIGH寄りM | キャッシュ | 同寸法テクスチャへの切替で PreviewParityCache が無効化されず、旧テクスチャの選択・統計が詳細プレビューに転写される |
| A-6 | HIGH寄りM | Core | 近黒サンプルのグレーモード距離が彩度のみに退化し、**純白画素を距離 0 でマッチ**（輝度盲） |
| A-7 | MEDIUM | 運用 | dev_safe（入れ子別リポ）が未コミット 14 ファイル + 未プッシュ 4 コミットのドリフト状態 |

### A-1. 過去の修正 2 件が履歴から消失したまま [裏取り済み]

`git merge-base --is-ancestor` で以下 3 コミットが **現在の HEAD の祖先でない**（= reset で失われ、再移植されていない）ことを確認した。

1. **`f77a2ff` 暗ターゲットの明度キャップ（topL）**
   - 現行 `Code/Core/PixelProcessor.Recolor.cs:213-216` の L 2 区間リマップは上端を常に 1.0（白）に固定。`topL` / `HighlightLMult` は Code/ 全体で grep 0 件。
   - 症状: 濃紺など暗い色をターゲットにすると、明部が `tL + (oL-sL)/(1-sL)*(1-tL)` で白方向へ暴走する。暗いサンプル画素をクリックしたとき顕著。
   - メモリ `project_dark_target_brightness_cap` に「要再移植」と記録されて以降、未対応のまま。
2. **`0752eb4` スポイト位置の正規化（normalizeMatchSample）+ `50a856f` その表示修正**
   - `Code/Core/MatchSampleNormalizer.cs` ごと現行ツリーに存在しない。スポイト位置（影/明部）による選択のブレを素材代表トーン（median）へ寄せる修正が丸ごと失われている。
   - 当時「ON/OFF で変化なし」という未解決の実挙動報告もあったため、**再移植するか、正式に不採用として記録を閉じるかの判断が必要**。

> 教訓としても重要: reset 運用で「コミット済み＝安全」が成立していない。同型の消失が他にもある可能性は否定できない。

### A-2. 上書きエクスポート後、プレビューが「二重適用」を表示しないまま実出力が二重適用になる [裏取り済み]

- `Code/UI/PreviewView.cs:191-201` `InvalidateSourceCache()` の呼び出し元はテクスチャフィールド変更時（`IrocaWindow.Layout.cs:322`）とセッションリセット（`IrocaWindow.cs:324`）のみ。**エクスポート適用後に呼ばれない**。
- 一方エクスポートは `File.ReadAllBytes(srcPath)`（`ExportView.cs:197`）で**ディスクの現物**を毎回読み直す。
- 失敗シナリオ: ①上書き保存（`saveAsNewFile=OFF`, `ExportView.cs:182`）→ ②色を微調整 → ③再度「適用して保存」。プレビューは「旧画素×1 回再着色」を表示し続けるが、実出力は「上書き済み画素×もう 1 回再着色」= 二重適用。**プレビュー＝実出力の一致というツールの中核保証が壊れる。**
- 外部エディタでソース PNG を編集して再インポートされた場合も同様（`OnPostprocessAllAssets` フックは repo に存在しない）。
- 対処: エクスポート適用後と再インポート検知時（AssetPostprocessor）に `InvalidateSourceCache()`。

### A-3. CleanupOrphans が一時的な GUID 未解決だけで実データを恒久削除 [裏取り済み]

- `Code/Infra/SessionFileStore.cs:172-176`（`MaskFileStore.cs:146-171` も同型）: orphan 判定は `AssetDatabase.GUIDToAssetPath(guid)` が空か否か**のみ**で、即 `File.Delete`。
- 呼び出しはウィンドウ `OnEnable` 毎回（`IrocaWindow.cs:102-103`）。
- 失敗シナリオ: テクスチャが存在しないブランチへ git switch した状態で Unity がウィンドウレイアウトを復元 → GUID が解決できず、**手描きマスクとセッションが無警告で恒久削除**。ブランチを戻しても復元不能。Library 再構築中も同型リスク。
- 対処: 猶予付き削除（N 日未解決で初めて削除）か `.orphan` リネーム退避。

### A-4. リリース listing の SHA/URL/実資産の 3 者不一致（既知残件・未解消を現物確認）[裏取り済み]

- `docs/index.json:34` の 0.2.0 `zipSHA256: 17e628b8...` は **リネーム前（VACC 時代）の zip のハッシュ**（`git log -S` で 8a6cb8c 由来と確認）。URL は `com.yukkuri-aoba.iroca-0.2.0.zip`（`docs/index.json:29`）だが、その名前の資産は v0.2.0 リリースに存在しない可能性が高い。
- `05c8ecb` のコミットメッセージ自身に「公開前に Build-VpmPackage.ps1 で再生成すること」と明記されたまま未実施。`docs/RELEASING.md:40-48` の「公開前の一度きり復旧タスク」チェックリストも全項目未消化。
- 公開（GitHub Pages 有効化）すると VCC はダウンロード 404 か SHA 不一致でインストール不能。

### A-5. 同寸法テクスチャ切替で PreviewParityCache が stale 転写 [裏取り済み]

- `InvalidateSourceCache()`（`PreviewView.cs:191-201`）はソース画素と SelectionCache は破棄するが **`_host.previewParityCache` を null にしない**。
- 詳細プレビューの採否は寸法一致のみ（`DetailPreviewView.cs:166-169`）。パリティキャッシュの差し替えはフルプレビュー完了時のみ（`PreviewView.Async.cs:204`）。
- 失敗シナリオ: 2048²/4096² などアバター用途で寸法が揃った**別テクスチャ**へ切替 → 新フルプレビュー完了までの数秒間、詳細プレビューが**前テクスチャの keep ビット（FF 結果）と再着色統計（anchor/wash/領域 L）を転写** → ズーム中に明確に壊れた選択・色が見える。フル完了で自己回復する過渡バグ。
- 対処は `InvalidateSourceCache` に `previewParityCache = null` の 1 行（+ 関連: 切替時に `detailJob.Cancel()` / 表示破棄も呼ばれていない。下記 UI L-2）。
- 付随 [報告のみ]: `PreviewParityCache.generation` フィールドは**読み書きとも 0 箇所の死にフィールド**で、クラスコメント「世代スタンプで管理」は実装と乖離（`PreviewParityCache.cs:30-31`）。「世代で守られているつもり」の穴の温床。

### A-6. 近黒サンプルのグレーモードが輝度盲（純白が距離 0）[裏取り済み]

- `Code/Core/ColorZone.Match.cs:219-223`: `sc.sV < GrayModeDarkSampleValue(0.3)` のとき `effectiveDist = Lerp(rgbDist, pS, darknessFactor)`。純黒サンプル（sV≈0）では距離が **対象画素の彩度そのもの**に完全退化する。
- 純白画素は pS=0 なので距離 0 = 完全マッチ。さらに `matchConf = 1` で FF の確信コアにもなり、連結成分アンカリングでも落ちない。彩度整合ゲート（`:232-238`）は `sc.sS > ChromaGateActivateSat` が条件のため純黒サンプル（sS≈0）では作動しない。
- 失敗シナリオ: 黒い布をスポイト → 同一テクスチャ内の純白パーツ（UV 背景・白装飾）まで巻き込んで変色。
- コメント上は「黒布のグレーハイライトは同素材」という意図的設計だが、**明度情報を完全に捨てる**ため白と黒を区別できない。輝度項を距離に残す（例: pS と |pV−sV| の合成）などの再設計候補。

### A-7. dev_safe（テスト資産の入れ子リポ）のドリフト [裏取り済み]

- 未コミット 14 ファイル（`test_csharp_quality_gate.py`、`quality_thresholds.py` などテスト本体を含む）+ 未プッシュ 4 コミット（origin/main: ahead 4）。
- テスト・GT の「唯一の正」がローカル作業ツリーにしか存在しない状態。マシン故障で改善サイクルの検証基盤ごと失われる。commit/push を推奨。

---

## Core 再着色アルゴリズム（PixelProcessor / ColorZone）

### バグ

| ID | 重大度 | 場所 | 内容 | 確度 |
|----|--------|------|------|------|
| B-1 | high | `PixelProcessor.Recolor.cs:213-216` | 暗ターゲット明度キャップ topL の消失（→ A-1） | [裏取り済み] |
| B-2 | med-high | `ColorZone.Match.cs:219-223` | 近黒サンプルの輝度盲（→ A-6） | [裏取り済み] |
| B-3 | medium | `ColorZone.Match.cs:249-254` | **グレーモードのハイライト回収に色ゲートが無い**。`pV > 0.8` と `\|pV-sV\|` のみで発火し、有彩モード側 `CalculateHighlightRecovery`（`:370-387`）が持つ色相ゲート・彩度ゲートが無い。暗いグレー/黒サンプルで無関係な明るい有彩パーツが highlightPotential を得て `PropagateHighlights` で滲む | [裏取り済み]（コード確認。実害はテクスチャ依存） |
| B-4 | low | `PixelProcessor.cs:896-906` vs `HighlightSampleCorrector.cs:73-83` | percentile の bin→値マッピングが `b/(Length-1)` と `b/255f` で不整合。現状 256bin のみなので実害なしだが保守の罠 | [報告のみ] |
| B-5 | low | `PixelProcessor.Recolor.cs:39,115` | アンカー chroma ヒストグラムが 0.5 でクランプされ、極彩度で代表 C を過小評価しうる | [報告のみ]（speculative） |

### 仕様・脆いヒューリスティック

- **S-1** `ColorZone.Match.cs:345-365` — 明部免除のみ距離を最大 70% 短縮する非対称はコメント・メモリとも「意図的」と整合。ただし同色相・高明度の別素材を引き込むリスクを常に持ち、防波堤は色相ゲート `ForgivenessHueGate=0.15` のみ。[報告のみ]
- **S-3** `AchromaFormGain=1.5`（三角 1 ケース由来の較正）など、複数被写体 sweep 由来と単一ケース視覚較正由来の定数が混在。汎化テストの継続注視対象。[報告のみ]
- **S-4** `PixelProcessor.Achroma.cs:83-101` — `RejectNeutralForAchromaTarget` の保護 dilation が 4 近傍×2 回でマンハッタン距離。斜め方向の保護が弱く、`NeutralRejectProtectRadius=2`（ユークリッド意図）とずれる。[報告のみ]
- **S-5** `ColorZone.Match.cs:396-401` — `IsInRect` が `x/texWidth` で除算（FF シードは `w-1` 規約）。Rect 選択の右端・上端 1 画素列の取りこぼし可能性。実害小。[報告のみ]
- **S-6** `PixelProcessor.Achroma.cs:180-208` — `ComputeAchromaWeight` / `ComputeAchromaSelectWeight` の二重定義は理由コメントありで正当だが、片方だけ直すと非対称に壊れる保守リスク。[報告のみ]

### パフォーマンス

- **P-1** (med) `PixelProcessor.Highlight.cs:156-157,192-199` — `GrowHighlightBand` が未プールの `bool[len]`×2 をゾーンごとに GC 確保（4K で 16.7MB×2）+ コアシード収集が bbox 非限定の全画素走査。既定 ON の通常経路。
- **P-2** (med) `PixelProcessor.Decontam.cs:188-211` — `CleanAchromaFringe` の BoxFilterSum 5 本が全画素処理（最終ループのみ bbox 限定）。無彩ターゲットで毎回。
- **P-3** (med) `PixelProcessor.Decontam.cs:73-100` — `DecontaminateAaBoundary` の BoxFilterSum 4 本 + 全画素 Parallel.For 2 本が bbox 非限定（既定 ON）。後段パスは bbox 最適化済みなのにデコンタミだけ全画素のまま。
- **P-4** (low-med) 領域統計・bbox 計算が逐次シングルスレッド全画素走査で複数回重複（`PixelProcessor.cs:739-753` は直前計算済み bbox とほぼ同範囲を再走査）。
- **P-7** (low) `PixelProcessor.cs:157-158` — `originalPixels` を毎回 `new Color32[len]`（4K で 67MB）で確保、LOH churn。ほかのバッファはプール化済みなのにここだけ非プール。
- ほか P-5（OkLab 変換の重複最大 4 回/画素）、P-6（`MaskHash` がキー生成毎に全 packed mask 走査）。[すべて報告のみ]

### 問題なしと確認された点
int オーバーフロー（BoxDownsample は long 集計）/ hue 折り返し / ゼロ除算ガード / Parallel.For の競合（distinct index + 事前 `UpdateCacheIfNeeded` + ゾーン Clone）/ ArrayPool 返却の try-finally / 全透明・純黒白サンプルの退化ガード。

---

## 自動調整（ZoneAutoTuner）・キャッシュ・非同期ジョブ

- **H-1** PreviewParityCache の無効化漏れ（→ A-5）
- **M-1** (med) `PreviewParityCache.cs:30-31` — `generation` が死にフィールド、コメントと実装の乖離（A-5 付随）。[報告のみ]
- **M-2** (med) `ZoneAutoTuner.cs:236-289` — **`TryAnalyzePixels` だけ除外マスクを見ていない**。tolerance 導出・トーン抽出・閉ループ検証は全て `IsMaskExcluded` を尊重するのに、ここだけテクスチャ全体を走査。ここから決まる `shadowForgivenessSatMin` / `saturationStrictness` / highlightRecovery 初期値が、マスクで除外したはずのパーツの画素に汚染される。マスク運用時の自動調整の質を直接下げる非対称。[報告のみ・certain]
- **M-3** (med) ZoneAutoTuner.Analyze が**キャンセル不能・単一スレッドで最大 15 回前後の全画面走査**。work デリゲートが CancellationToken を一切見ないため `Cancel()` はゾンビ実行を止められない（`IrocaWindow.AutoTune.cs:179-184`）。各パスで `Color.RGBToHSV` を毎回再計算。付随: stride 判定が `w <= 2048` と**幅のみ**で、縦長テクスチャで不均衡（`ZoneAutoTuner.cs:249` ほか）。[報告のみ]
- **M-4** (med) `PreviewJob.cs:109-115` — `Cancel()` 後も work は走行し続けるのに `IsRunning=false` になるため、AutoTune で旧解析と新解析が**並走**（CPU 2 倍）し、同一 progress への報告で進捗バーが飛ぶ。結果適用は世代ガードで正しさは保たれる。[報告のみ]
- **M-5** (med) `IrocaWindow.AutoTune.cs:167-184` — AutoTune が live の `ColorZone` / セッションを**クローンせず**背景スレッドへ渡す。かんたんモードの非ブロック自動実行では解析中に編集可能で、`zone.sampleColor` を 6 箇所で独立再読取するため混成サンプルから無意味な tolerance を導出しうる。`session.zones` 列挙中の追加/削除で `InvalidOperationException`。プレビュー経路の Clone 流儀と不整合。[報告のみ]
- **M-6** (med/perf) `SelectionCache.cs:59-66` — ゾーンごとに `float[w*h]` フルコピー（4K で 67MB/ゾーン）×フル/プロキシ 2 系統を上限なしに保持。`Store` が毎回 new で LOH churn。ゾーン削除時の evict API もない。
- **L-3** (low/spec) `ZoneAutoTuner.Verify.cs:159-173` — 閉ループ検証が「免除超過を検出したが縮められなかった」場合と「解析不能でヒューリスティック既定を返した」場合を、成功と**区別なく**「自動調整しました」と通知する。TuneResult に品質フラグを持たせる価値。
- ほか L-1（`PercentileBin` の下端/上端エッジ不統一で sP10 等が最大 1/32 下振れ）、L-2（旧 CTS 即 Dispose の狭い競合窓）、L-4（`HighlightGrowthMaxFrac=0.15` は光沢面積の大きい素材で正当な highlightRecovery を強制 OFF にする汎化リスク）、L-6（`BuildSelectionKey` の MaskHash がマスク寸法を含まない）、L-7（キャンセル済み詳細プレビューの pending 結果が一瞬適用される）。[すべて報告のみ]

### 問題なしと確認された点
PreviewJob の世代管理 3 経路 / SelectionCache の lock + コピー / parityCache の公開後不変 / TextureSlot の明示破棄（Texture2D リークなし）/ ZoneAutoTuner のゼロ除算到達不能 / ドメインリロード耐性。

---

## UI 層

- **H-1** 上書きエクスポート後のキャッシュ失効漏れ（→ A-2）
- **M-1** (med) `MaskPaintView.cs:387-388` — ブラシの表示 px→マスク px 変換が**幅のみ**基準（`maskWidth / min(maskWidth, MaxSize)`）。プレビュー縮小率は長辺基準なので、**縦長テクスチャ（例 512×2048）ではカーソル円の 1/4 の半径しか塗られない**。長辺基準へ修正。[裏取り済み]
- **M-2** (med) `PreviewView.cs:210-249` — `EnsureTrueSource` が PNG デコード + `GetPixels32`（8K で 268MB）を OnGUI 内で同期実行。8K 級で数秒フリーズ + ウィンドウを閉じるまで常駐。[裏取り済み（コード構造）]
- **M-3** (med/spec) `IrocaWindow.AutoTune.cs:146-164` — **自動調整だけ「インポート済み（圧縮・縮小された）画素」を解析**している。選択・プレビュー・エクスポート・スポイトは true source（ディスク原本）で動くのに、AutoTune は `tex.GetPixels32()`。圧縮ノイズ・縮小で彩度/距離分布が歪み、導出パラメータが実処理経路とずれる。`EnsureTrueSource` の流用で一貫化可能。[報告のみ・likely]
- **M-4** (med/perf) `PreviewView.Input.cs:395-407` — ストローク補間間隔が「マスク 1 画素」。4K で 1 回のドラッグイベントに最大数千回の円形塗りが走る。ブラシ半径比例（r/2 等）で桁削減可能。[報告のみ]
- **M-5** (med/spec) `ExportView.cs:182-188` — 上書きモードで非 PNG ソース（.jpg/.tga）のとき、`Path.ChangeExtension(srcPath, ".png")` で**元ファイルを上書きせず隣に .png を新規作成**する。確認ダイアログの「上書きします」と矛盾し、マテリアルは旧ファイル参照のままなので「色が変わらない」ように見える。[報告のみ・certain]
- **M-6** (med) `IrocaWindow.ZoneList.cs:235-241` — ゾーン並べ替えドラッグが `GUIUtility.hotControl` を取らない。ウィンドウ外リリースで `_dragZoneIndex` が残留し、**次の無関係な MouseUp で意図しない並べ替え（=優先度変更=出力変化）が確定**する。マスクペイント側は hotControl を正しく使っており、同じ作法へ。[報告のみ・likely]
- **L-2** (low) テクスチャ切替時に `_detailView.InvalidateDisplay()` / `detailJob.Cancel()` を呼ばず、旧テクスチャの in-flight 詳細クロップが一瞬新テクスチャ上に重なる（A-5 と同族）。
- **L-3** `RunAutoTune` が `pixels == null`（GetPixels32 失敗）を未ガードで背景ジョブへ渡し、不親切な NullReference 通知になる。
- ほか L-1（パン hotControl の解放漏れ経路・speculative）、L-5（OnGUI 毎フレーム GUIContent 生成の取りこぼし数カ所）、L-7（折りたたみ Foldout 類のみツールチップ欠落 — ボタン/スライダー/トグル/フィールドは全数付与済み）、L-8（休眠中のバッチ適用コードはゾーン Clone なし・StartAssetEditing 内モーダル等、再有効化前に要修正）。[すべて報告のみ]

### 問題なしと確認された点
Y 反転・座標変換は全経路正しい（詳細クロップ・可視クロップ・ペイント/シード/スポイト UV・オーバーレイを個別検算済み）/ Texture2D リークなし / マスク永続化はアトミック書き込み + `.bak` 退避 + リスケール対応で堅牢 / Undo 整合 / 非同期プレビューの連打耐性 / Localization の両言語プレースホルダ一致・空 catch なし。

---

## Infra・自動化・Debug

- **H-1** CleanupOrphans のデータ喪失リスク（→ A-3）
- **H-2** (high) `Code/Debug/DebugView.cs:47-55` + `DebugCaptureContext.cs:86-95` + `DebugWindow.cs:87-99` — キャプチャ context を処理開始前に公開し、背景ジョブが `List.Add` する同じリストを DebugWindow の OnGUI が foreach する**スレッド競合**。キャプチャ有効時（既定 ON）は常に踏み得る。完了時に `LatestContext` へ swap する形が安全。[報告のみ・likely]
- **M-1** (med/spec) `Code/Infra/BuildHelper.cs:42-45` — **unitypackage には Code/Debug が同梱される**（除外なしの Recurse）一方、zip は `Build-VpmPackage.ps1:90` で除外。`docs/RELEASING.md:54` は「zip にも同梱」と記述しており 3 者が食い違う。Debug asmdef は defineConstraints なしなので、同梱されると全ユーザーで Debug ウィンドウが可視。**どちらを正とするか要判断**。[報告のみ・likely]
- **M-2** (med) `MaskFileStore.cs:82` / `PresetStore.cs:132` の schemaVersion は**書くだけで Load 時に不検査**。`IrocaSessionState` にはフィールド自体がない。JsonUtility は欠落フィールドを型既定値にするため、将来「既定 true/非ゼロ」のフィールド追加で旧ファイルが silently 別挙動になる。[報告のみ・certain（機構）]
- **M-3** (med/security) `IrocaAutomation.cs:341,386-389` + `IrocaMcpTools.cs:41-61` — 自動化/MCP が**任意絶対パスへの読み書き無制限**（拡張子非強制）。エージェント経由で Assets 外の既存ファイルを PNG バイトで上書き可能。拡張子強制 + プロジェクト配下制限を推奨。[報告のみ・certain]
- **M-4** (med/perf) `DebugCaptureContext.cs:33-35` — キャプチャのメモリ見積りコメント「4K≒360MB」は実際約 1.5GB。`DebugWindow._overlayBuiltFrom` が前世代を掴み再キャプチャ直後は 2 世代同時生存（4K で ~3GB）。キャプチャは既定 ON。[報告のみ]
- **M-5** (med/perf) `MaskPaintView.Persistence.cs:65-87` — **ストローク毎**に common+全ゾーンのマスクを RLE+Base64 で全量再エンコードし、さらに `RegisterCompleteObjectUndo` が Base64 込みのウィンドウ全体を Undo に積む。4K×複数ゾーンでメインスレッド数十 ms。[報告のみ]
- **L-5** (low/spec) `PerfView.cs:203-208` — EditorPrefs のスレッド数オーバーライドが起動時に無条件適用され、デバッグモード OFF だと変更 UI が無いまま製品性能に効き続ける。
- ほか L-1（AtomicFile が flush-to-disk なし=電源断に弱い）、L-2（`.bak` を誰も掃除しない）、L-3（プリセット名サニタイズが Windows 予約名 CON/NUL 等未処理）、L-4（DebugDumpStore の無言スキップと manifest 不整合）、L-6（`Iroca.LastTextureGuid` が EditorPrefs=全プロジェクト共有で、コピーしたプロジェクト間で初回自動ロードが混線しうる）、L-7（ゾーン削除後も死んだ zoneId のマスクがファイルに残留し続ける）、L-8（`RunFromCommandLine` を非 batchmode で呼ぶと保存確認なしに Editor 即終了）。[すべて報告のみ]

### 問題なしと確認された点
「マスク以外を保存」の設計は SaveSession の一時差し替え + finally 復元で一貫 / テクスチャ切替の保存順序 / 日本語パス（.NET Unicode API のみ）/ `lastLoadFailed` 防御の対称実装 / RLE デコードの境界チェック。

---

## ビルド・リリース・テスト基盤

- **1-1** (HIGH) index.json 0.2.0 の 3 者不一致（→ A-4）
- **1-2** (med) `release-verify.yml:8-9` — `release.edited` は**アセット差し替えでは発火しない**。RELEASING.md の「fail したら zip 差し替えで解消」の通りにすると、再検証されないまま放置される。workflow_dispatch の追加か手順書への明記を推奨。[報告のみ]
- **1-3** (med) release-verify は**タグ時点**の index.json と照合するが、VCC が読む GitHub Pages は **main 最新**。listing だけ後から更新すると検証済み SHA と配信 SHA の乖離を検出できない。[報告のみ・機構 certain]
- **1-4** (med) `release.yml:97-104` — draft はタグに紐付かないため再実行毎に**重複生成**（RELEASING.md:44 に実害記録あり）。古い draft へ資産をアップロードして publish する事故経路。[報告のみ]
- **1-5** (med) `Build-VpmPackage.ps1:59-107` — zip が**非再現ビルド**（タイムスタンプ正規化なし）。コミット済み SHA と一致する zip は生成時の 1 ファイルだけで、紛失すると listing 再生成 + タグ打ち直しが必要。[報告のみ]
- **2-2** (med) `Code/McpIntegration/` は build-check 両 csproj からも Harness からも除外され CI も無いため、**どのビルド検証も通らないコードが配布 zip に同梱**される（`IROCA_MCP_PRESENT` ゲートにより通常ユーザーではコンパイルされないが、MCPForUnity 導入環境で初めてエラーが露呈しうる）。[報告のみ]
- **2-3** (med) **push/PR で走る CI が存在しない**（workflows は release 系 2 本のみ）。型チェックも回帰もローカル任意実行のみで、コンパイル不能なコミットが develop に入っても機械検出されない。build-check だけでも CI 化の価値。[報告のみ]
- **3-2** (med) pytest 外の計測スクリプト 3 本が旧 `VACCHeadless.dll` 参照のまま: `dev_safe/measure_autotune_robust.py:27` / `dev_safe/probe_achromatic.py:26` / `dev_safe/scripts/_verify_maxmag_bitexact.py:19`。改善サイクル中に使うと**旧バイナリを測る/壊れる**。[報告のみ]
- **3-3** (med) Baselines/ が gitignore のため、クローン直後・別マシンでは IoU 系の大半が **skip でスイート全体が緑に見える**（過去のサイレント skip 事故と同型の再発形）。`-ra` 運用か「skip 数閾値超過で fail するメタテスト」を推奨。[報告のみ]
- **3-4** dev_safe のドリフト（→ A-7）[裏取り済み]
- **4-1** (med) `tools/visual_review.py:272-283` + `tools/check_visual_review.py:104-122` — **approve は compare を実行したかを一切検証しない**（mtime 比較のみ）。「compare → Read 確認 → approve」手順の強制力は自己申告と同等。approve 時に compare 出力 PNG の mtime 検証を足せば機械的に塞げる。[報告のみ]
- ほか 1-6（Build-VpmPackage の Next Steps 案内が旧ブランチ名で陳腐化）、1-7（生成物 zip/unitypackage が gitignore されず、`git add -A` 巻き込み事故の経路）、1-8（rc タグ非対応・main 上のタグであることの未検証）、4-2（hooksPath は clone 毎の手動有効化前提）。[すべて報告のみ]

### 健全と確認された点
本体 build-check は `Code\**\*.cs` の再帰 glob で新規ファイルを自動カバー / `fixtures.py::require_harness` の「環境不備=skip・ビルド失敗=fail」二分設計 / strict xfail 運用 / release.yml の検証内容は RELEASING.md と一致 / 日本語パスでの dotnet ビルド・pytest 収集は現状動作。

---

## 推奨対応順（私見）

1. **A-1**: 消失修正の再移植 or 正式廃棄の判断（topL は再現手順が明確なので GT/視覚レビュー付きで再移植可能）
2. **A-2 + A-5 + UI L-2**: キャッシュ無効化トリガの追加（いずれも数行、プレビュー=出力一致の中核保証の回復）
3. **A-3**: CleanupOrphans の猶予付き削除化（ユーザーデータ喪失の防止）
4. **A-7 + 3-2**: dev_safe の commit/push と旧 DLL 名スクリプト修正（検証基盤の保全）
5. **A-4**: 0.2.0 公開復旧タスク（公開の意思が固まったタイミングで RELEASING.md のチェックリスト消化）
6. **A-6 / B-3 / M-2(AutoTune)**: アルゴリズム系は改善サイクル（GT + 視覚レビュー）に乗せて 1 件ずつ
7. perf 系（P-1〜P-3, M-5(Persistence), M-4(Input)）: 体感に効く順で。出力ビット不変を検証しながら

## 手法と限界

- レビューは静的読解が中心で、実行による再現確認はしていない（アルゴリズム系の「失敗シナリオ」は機構からの推論）。
- [報告のみ] の項目は着手前に該当行の現物確認を推奨（行番号は b5ddff5 時点）。
- 「問題なし」とした領域も全数検証ではなく抜き打ち検証。
