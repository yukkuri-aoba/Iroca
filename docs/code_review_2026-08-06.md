# コードベース横断レビュー（2026-08-06）

対象コミット: `31e2948`（develop）。UX・パフォーマンス・実装品質を 4 領域
（UI/UX / Core アルゴリズム / Infra・AI マスク提案・自動化 / ビルド・CI・テスト基盤）の
並列レビューで洗い出し、重要度の高い新規指摘は実コードで個別に裏取りした。
前回監査（`docs/code_audit_2026-07-13.md`）とパフォーマンス評価
（`docs/performance_review_2026-07-29.md`）の指摘は、現況（修正済み / 残存）を確認した。

- **[裏取り済み]** = 該当コード・git・現物を本レビューで直接確認した項目
- **[報告のみ]** = 領域別レビューの報告で、個別の再確認はしていない項目（行番号・機構の説明は具体的）
- **（既知: X）** = 過去文書で指摘済みの残件。X は出典（監査 2026-07-13 / perf 2026-07-29 の項目 ID）

ファイルは一切変更していない（本ドキュメントの追加のみ）。

---

## 1. 前回指摘の現況

### 修正済みと確認できたもの [裏取り済み]

| 旧 ID | 内容 | 確認箇所 |
|---|---|---|
| A-1(1) | 暗ターゲット明度キャップ topL の消失 | `PixelProcessor.Recolor.cs` に topL / HighlightLMult が再移植済み |
| A-2 | 上書きエクスポート後のソースキャッシュ失効漏れ（二重適用） | `InvalidateSourceAndRepaint` を `ExportView.cs:312,551` から呼出 |
| A-3 | CleanupOrphans の即時削除によるデータ喪失 | `.orphan` 退避 + 30 日猶予 + 復元経路（`SessionFileStore.cs:165-236`） |
| A-5 | 同寸法テクスチャ切替の PreviewParityCache stale 転写 | `PreviewView.cs:220` で null 化 |
| A-6 | 近黒サンプルの輝度盲 | グレーモード彩度天井ゲート追加（`01ae8bf`） |
| UI M-1 | ブラシ半径の幅基準変換（縦長テクスチャでずれ） | 塗り格子＝プレビュー画素格子への再設計で解消（`PreviewView.Input.cs:465-470`） |
| UI M-5 | 非 PNG 上書き時の無言 .png 新規作成 | 専用ダイアログ追加（`ExportView.cs:182-186`） |
| UI M-6 | ゾーン並べ替えの hotControl 未取得 | hotControl 取得・自己キャプチャ判定済み（`ZoneList.cs:245,590-602`） |
| 4-1 | visual_review approve の空承認 | compare パネル存在 + Code/ より新しいことを検証（`visual_review.py:288-300`） |
| 2-3 | push/PR CI 不在 | 最小 CI 新設（`936a7cb`） |
| 1-5 派生 | Build-VpmPackage の -UnityPackagePath 省略事故 | 省略をエラー化、明示スイッチ方式（`12f583c`） |
| 2026-06-28 High | package.json 不正 JSON / displayName 文字化け | parse 成功・displayName「いろか」を確認 |
| perf F1/F2/F5/F6/F8 | RegionStats 単スレ等 | 実装済み・4K 実測 -52%（`e63c564`、出力ビット不変） |

### 未解決のまま残っているもの

| 旧 ID | 内容 | 現況 |
|---|---|---|
| A-1(2) | スポイト位置正規化（MatchSampleNormalizer）の再移植 or 正式廃棄 | **判断未決のまま**。Code/ に存在せず、廃棄の記録もない [裏取り済み] |
| A-4 | 0.2.0 listing の SHA/URL/実資産 3 者不一致 | リリースは機械的にブロック済み（`a6ff1cb`）だが、**復旧タスク自体は 1 か月超未消化**（`docs/RELEASING.md:40`） [裏取り済み] |
| A-7 | dev_safe のドリフト | 自動 commit+push フック導入後も **origin に対し ahead 3 / behind 45**。push が失敗している（別マシンとの分岐の疑い）。検証基盤の「唯一の正」が再び分裂中 [裏取り済み] |
| 1-2 | release-verify が資産差し替えで再発火しない | 未対応（→ §6-1） |
| 1-3/1-8 | タグ検証と Pages(main) 配信の乖離 | 未対応。**main は 2026-05-14 から 361 コミット遅れ + 逆方向 6 コミット分岐** [報告のみ] |
| 3-3 | Baselines 不在時のサイレント skip | 未対応どころか悪化を確認（→ §6-12） |
| perf F3/F4/F7/F9〜F12, A1〜A13, U1〜U11 | パフォーマンス残課題 | 大半が未着手。本レビューで再確認されたものは各節に（既知: …）付きで再掲 |

---

## 2. 総括 — 新規指摘の最優先 8 件

| # | 重大度 | 領域 | 要旨 |
|---|--------|------|------|
| N-1 | **HIGH** | Core | **CleanAchromaFringe が除外マスクを参照せず、マスク保護画素を直接上書きする**（マスク契約違反） |
| N-2 | **HIGH** | テスト基盤 | **headless ハーネスの `useFloodFill` 既定が false で、製品既定（true）と乖離** — 「実 C# を測る」原則の穴 |
| N-3 | **HIGH** | テスト基盤 | **回帰テストの現行ベースラインと品質ゲート較正 CSV がどの git にも存在しない**（このマシン限り） |
| N-4 | **HIGH** | ML | MaskSuggest の StateChanged 購読解除経路が無く、ウィンドウ再オープンで旧購読者が提案を横取りし得る |
| N-5 | **HIGH** | UI | ジョブ実行中でも「リセット」が押せる（DisabledGroup の外）+ ジョブオーバーレイが画面外に出る計算 |
| N-6 | **HIGH** | リリース | 配布 zip の内容が git 追跡外のローカル `.meta` に依存し、クリーン clone から同一 zip を再生成できない |
| N-7 | HIGH寄りM | UI | 「マスクを元に戻す」ボタンがグローバル `Undo.PerformUndo()` で、直前操作がマスク以外でもそれを巻き戻す |
| N-8 | HIGH寄りM | 運用 | dev_safe が origin に対し ahead 3 / behind 45 — 自動 push 失敗が無警告で継続 |

### N-1. CleanAchromaFringe のマスク契約違反 [裏取り済み]

- `Code/Core/PixelProcessor.Decontam.cs:260-270` の BG ドナー収集は `s <= 0 && a > 0` のみで
  `maskExcluded` を見ない。書き込み側（`:283-305`）も strength / claimed / 近傍のみで
  マスク除外を判定せず、条件が揃えば **`pixels[i]` を直接上書き**する。
- `DecontaminateAaBoundary` は `05ecca8` で除外マスク画素をドナーから隠す修正が入ったのに、
  フリンジ消し側だけ未対応の非対称。
- 失敗シナリオ: 無彩ゾーン + マスク使用時、「マスクで守ったはずのサンプル同色パーツ」の
  境界近傍が target 混色で塗られる。
- 対処: ドナー収集と書き込みの両方に `decontamMaskExcluded` 参照を追加（改善サイクルで
  GT + 視覚レビューに乗せる）。

### N-2. ハーネスと製品の useFloodFill 既定不一致 [裏取り済み]

- `scripts/headless-run/Harness.cs:67` は `useFloodFill = false`、
  `Code/Automation/IrocaAutomation.cs:90-91` は `useFloodFill = true`
  （コメント自身が「既定 ON＝製品 UI の標準と一致」と明記）。
- zones JSON が明示指定しないケースで、**テストが測る挙動と製品既定が別物**になる。
  Python プロキシ乖離（IoU 0.26）の教訓と同型の「測っているものが製品でない」リスク。
- 付随: `samples` / `seedUV`（Harness.cs:47,68）が Automation 側 DTO に無く、JsonUtility は
  未知フィールドを無言で捨てるためスキーマドリフトに気づけない。[報告のみ]
- 対処: 既定値を単一ソース化（`IrocaConsts` 等で共有）+ スキーマ一致の機械検査。

### N-3. ベースラインがどの git にも無い [報告のみ・重大]

- `dev_safe/Tests/Baselines/refactor-pre/*.json` と品質ゲート較正 CSV は
  **dev_safe リポジトリ側でも gitignore** されており、判定基準そのものがこのマシンにしか無い。
- 2026-08-03 の「環境全損」でローカル専用運用の脆さは実証済み（testing-architecture.md が
  それを理由に git 追跡へ昇格した）にもかかわらず、より重要な資産が同じ轍の上にある。
- 対処: Baselines / 較正 CSV を dev_safe の追跡対象へ昇格。N-8（push 失敗）の解消とセットで。

### N-4〜N-8

各節参照: N-4 → §5-1、N-5 → §3 高、N-6 → §6-8、N-7 → §3 高、N-8 → §1 未解決表 A-7。

---

## 3. UI/UX

### 高

- **[高]** `Code/UI/IrocaWindow.Layout.cs:59-67` — `DrawHeader()`（リセットボタン含む）が
  `BeginDisabledGroup(blocking)` の**前**にあり、エクスポート/手動自動調整のブロック中でも
  リセットが押せる。コメントの「ジョブ実行中はウィンドウ内 UI を全て無効化する」という
  不変条件に違反し、ジョブ完了時の apply と競合して状態不整合を生み得る。[裏取り済み]
- **[高]** `Code/UI/MaskPaintView.cs:206-209` — 「マスクを元に戻す (Ctrl+Z)」ボタンが
  グローバル `Undo.PerformUndo()` の薄いラッパーで、直前操作がスライダー変更やシーン編集なら
  **無関係な変更を巻き戻す**。ラベルが「マスクの」Undo を約束しており、非エンジニア層には
  破壊的サプライズ。[裏取り済み]
- **[高]** `Code/UI/IrocaWindow.Layout.cs:73-90` — レイアウトは `availableContentH` を
  上部＋中央＋エクスポートで使い切る設計なのに、ジョブオーバーレイ（進捗バー＋キャンセル）は
  その**後ろ**に約 48px を追加で積む。計算上、キャンセルボタンが下端で切れて押せない恐れ。
  [報告のみ・実機要確認]

### 中

- **[中]** `MaskPaintView.cs:125-134` + `Localization.cs:474-476` — 「マスクをクリア」の
  ツールチップが「**すべての**除外マスクを消去」と言うが、実装は編集対象の 1 枚のみ消去。
  両方向の誤解（全部消えると思って押す / 消したつもりでゾーン別マスクが残る）を生む。[報告のみ]
- **[中]** `ExportView.cs:199-216` — 「適用して保存」押下直後の `File.ReadAllBytes` +
  `LoadImage` + `GetPixels32` がメインスレッド同期で、4K では進捗表示ゼロのまま数秒フリーズ。
  自動調整側（`IrocaWindow.AutoTune.cs:169`）は進捗バーを出しており不揃い。[報告のみ]
- **[中]** `ExportView.cs:283-312` — BG 書き込み（`File.WriteAllBytes`）後〜apply 前に
  キャンセルされると、PNG はディスクに書かれたのに `ImportAsset` も
  `InvalidateSourceAndRepaint` も走らない中途半端な状態になる（上書きモードでは
  A-2 で塞いだはずの二重適用経路が復活）。onError 時の部分ファイル掃除も無い。[報告のみ]
- **[中]** `IrocaWindow.AutoTune.cs:201-207` — 手動自動調整の進捗バーが 10% 固定のまま
  完了時に 100% へ跳ぶ（Analyze に進捗レポータを渡していない）。約 5.5 秒（4K）の解析で
  「固まった」と誤認させる。[報告のみ]
- **[中]** `IrocaWindow.AutoTune.cs:236` — 完了通知の文言が「自動調整」のみで、
  完了/開始/失敗の判別がつかない。[報告のみ]
- **[中]** `IrocaWindow.Layout.cs:41-91,197,282` — OnGUI 全体に try/finally が無く、描画中の
  例外 1 回で `BeginDisabledGroup` / `BeginScrollView` / `labelWidth` が復元されず、以後
  毎フレーム連鎖エラーになる典型構造。[報告のみ]
- **[中]** `IrocaWindow.ZoneList.cs:135` + `Localization.cs:170-172` — 編集モードの
  ツールチップが、UI から隠した「かんたん」モードと、実際には起きない
  「サンプル変更で自動調整が裏で走る」挙動（コメントアウト済み）を案内したまま。[報告のみ]
- **[中]** `PreviewView.Input.cs:219-247` — Shift+クリックのシード設定先が「アクティブマスク
  対象が FF ゾーンならそれ、でなければ最初の有効 FF ゾーン」という暗黙ルールで、複数ゾーン時に
  どこへ入るかの事前表示が無く、シード十字も全ゾーン同色で事後確認もできない。[報告のみ]
- **[中]** `ExportView.cs:112-126` — `GetSectionHeight()` が描画コードのレイアウトを手書きで
  複製しており、コントロール 1 個の追加で横並びレイアウト全体が崩れる温床。実測方式
  （`_sideBySideTopHeight` と同じ手法）へ統一可能。[報告のみ]
- **[中]** `IrocaWindow.Layout.cs:397-398` — 開始点であるテクスチャ ObjectField に
  ツールチップが無い（プロジェクト規約違反）。[報告のみ]
- **[中]** `PreviewView.cs:332-337` — Read/Write 無効・true source 取得失敗時に Draw が
  無言で return。別ウィンドウ（IrocaPreviewWindow）では見出しだけ表示され理由が一切出ない。
  [報告のみ]
- **[中]** `IrocaWindow.ZoneList.cs:333-509` — ゾーンカード本体が毎フレーム・ゾーン数ぶん
  GUIContent を約 20 個生成（ヘッダ行だけキャッシュ済みで不統一）。（既知: perf U5 の再確認）

### 低

- `PreviewView.Input.cs:198-206` — ブラシカーソルが正方形 DrawRect で、実際に塗られる円形と
  形状不一致（四隅は塗られない）。
- `MaskPaintView.cs:174-176` — ブラシサイズのツールチップが「ピクセル単位」だが実単位は
  プレビュー格子セル（4K では 1 ≒ 10 テクセル超）。
- `Localization.cs:641-643` — 比較モード「高ズーム時は使用不可」に対応する無効化コードが無い
  （実挙動は「押せるが詳細プレビューが無効になる」）。
- `ExportView.cs:281` ほか — BG 例外メッセージが日本語ハードコードで英語 UI にもそのまま表示。
  `MaskPaintView.cs:309` の Debug.Log も同様。
- `IrocaColors.cs:27-28` — 除外=赤 / 含める=緑の区別が色のみで、赤緑色覚では判別困難
  （形状・ラベル等の冗長表現なし）。
- `MaskPaintView.cs:770-781` — ゾーンオーバーレイ色が index 由来で、並べ替え（優先度変更）で
  ゾーンの色が変わり同一性が崩れる。id ベースへ。
- `PresetsView.cs:186-229` — プリセット読込が現在の全設定を確認なしで上書き
  （保存側は上書き確認ありで非対称。Undo はある）。
- `PresetsView.cs:58-62` — プリセット名が空のまま保存できる（ファイル名化の挙動未検証）。
- `IrocaWindow.cs:108-109` — `ShowWindow()` が 728×786 未満のとき毎回強制リサイズし、
  ユーザーの意図的な縮小を開き直すたびに上書きする。
- `IrocaWindow.ZoneList.cs:141` — Simple→Normal の黙ったセッション書き換えが OnGUI 中・
  Undo 登録なしで走る（他のミューテーションは遅延 + Undo で統一されており流儀違反）。
- `IrocaWindow.cs:354-366` — `ResetCurrentSession` だけ Undo 登録前の `SyncBuffersToState()` を
  省略（削除・クリアは同期→Undo の順で統一）。buffers と state が乖離した状態で
  リセットすると Undo 復元がずれる可能性。[要確認]
- `IrocaWindow.cs:144-151` — `OnDisable` が `_autoTuneJob` を Cancel/Dispose しない
  （OnDestroy のみ）。ドメインリロード時に BG スレッドが残る。
- `DetailPreviewView.cs:262-275` / `PreviewView.Async.cs:288-301` — diff 生成（閾値 10・
  ハイライト色込み）が完全重複。片方だけ直す事故の温床。
- `IrocaWindow.Layout.cs:56-57` — Layout イベント中のモーダル `DisplayDialog`。
  `delayCall` 経由が安全。
- `Localization.cs:280,269,160,351,364,708,346` — UI から参照されない死に文字列
  （Zoom / BrushMode / AdvancedMode / LayerIndex / PanHint / EdgeStopThreshold / Saved）。
- foldout ヘッダ 4 箇所（加工設定・カラーゾーン・除外マスク・プリセット）がツールチップ無しの
  string 直渡し + `StepPrefix` 連結の毎フレーム文字列生成。
- `Core/UndoHelper.cs:19` — ほぼ全操作の Undo 名が既定「Iroca Edit」で、Undo 履歴から
  何が戻るのか分からない。
- `ExportView.cs:481-489` — 休眠中の一括適用が `StartAssetEditing` 内で確認モーダルを出し得る
  同期処理のまま（再有効化前に要修正。既知: perf U11）。
- `IrocaWindow.ZoneList.cs:254` — ゾーン削除（×）が確認なし・通知なし
  （プリセット削除は確認ありで不揃い。「Ctrl+Z で戻せます」通知等の補助を推奨）。
- （既知: perf U4/U6/U7/U9）プリセット一覧の毎フレームディスク列挙（`PresetsView.cs:90`）/
  サブウィンドウ 2 枚の無条件 10Hz Repaint / ペイント中の `new Color32[spanW]` /
  `EnsureTrueSource` のメインスレッド同期読み込み — いずれも残存を再確認。

---

## 4. Core アルゴリズム

### 高

- **[高]** N-1（CleanAchromaFringe のマスク契約違反）→ §2 参照。[裏取り済み]
- **[高]** `Code/Core/ColorZone.Match.cs:16-28` — `UpdateCacheIfNeeded` の無効化判定に
  `chromaThreshold` が含まれない。`BuildSampleCache` は chromaThreshold から
  `chromaConfidence` を導出するため、ライブインスタンス再利用経路
  （`IrocaAutomation.cs:360-363` 等）では陳腐化した confidence でマッチする潜在バグ。
  プレビュー/エクスポートは `Clone()` で偶然回避されているだけ。[報告のみ・certain（機構）]

### 中

- **[中]** `PixelProcessor.cs:320-322` — `ApplyChromaCeilingGate` が `zone.mode` を見ずに
  全ゾーン適用され、**Rect モードを破壊**する（Rect は sampleColor が既定白のためグレーモード
  判定が真になり、有彩画素が削除される）。現行 UI で Rect は設定不可だが、enum は public・
  シリアライズ対象で旧プリセット JSON から到達可能。[報告のみ]
- **[中]** `ColorZone.Match.cs:163-169` — 遅延 `UpdateCacheIfNeeded()` の「安全網」が
  Parallel.For 内で発火するとスレッド非安全（共有 `_sampleCaches` を無同期で再構築）。
  主経路はループ前初期化だが、安全網自体が危険。[報告のみ]
- **[中]** `PreviewParityCache.cs:38-66` — Dictionary に同期が無く、安全性が
  「未公開インスタンスにだけ書く」という遠隔の呼び出し規律にのみ依存
  （同型の SelectionCache は lock 保護済みで非対称）。[報告のみ]
- **[中]** `MaskRle.cs:65` — `w * h` の未チェック乗算。w=h=65536 で len=0 となり
  「成功・空マスク」を silent に返す。ラン総和が len 未満の切断データも成功扱い。[報告のみ]
- **[中]** `PixelProcessor.Selection.cs:570-602` — `RecoverEnclosedNeutral`（`bf75e01` の
  閉領域判定）が単スレッド逐次で最悪 24 スイープ×2 方向。渦巻き状の未選択領域では
  数秒規模の遅延になり得る。並列化も bbox 制限も無い。[報告のみ]
- **[中]**（既知: perf F7）`PixelProcessor.cs:216-220` — decontam バッファ
  （`new bool[len]` + `new Color32[len]`、4K で約 84MB）と `decontamMaskExcluded` が
  呼び出しごとの LOH 確保のまま。周辺の ArrayPool 徹底と矛盾。残存を裏取りで再確認。
- **[中]**（既知: perf F7/U3）`SelectionCache.cs:59-66` — 容量無制限
  （4K で 67MB/ゾーン恒久保持、LRU なし）。残存を再確認。

### 低

- `PixelProcessor.cs:167-171` — `originalPixels` の Rent が try 外にあり、引数不正
  （null / 長さ不足）でプール配列 64MB がリーク。引数検証自体も無い。
- `PixelProcessor.Selection.cs:449-467` — ceiling gate の保護 dilation が bbox 無し全画素×12 回。
  ただし 2026-07-29 の実測では +45ms であり実害は小（グレーモードゾーン増加時は要再計測）。
- `PixelProcessor.cs:385-387` — FF keep のビットパックが 16.8M 画素の単スレッドループ
  （前後工程は並列化済み）。
- （既知: perf F9）`PixelProcessor.cs:201-204` ほか同型 3 箇所 — HSV 事前計算等が要素単位
  デリゲートの `Parallel.For(0, len)` のまま。
- `PixelProcessor.Achroma.cs:84,144` / `PixelProcessor.Selection.cs:195-205` — achroma 初期化・
  `ConstrainBlur` のマスク生成が全画素逐次で bbox 方針と不整合。
- `PixelProcessor.Selection.cs:70` — `BoxFilterSum` 垂直パスがブロックごとに
  `new float[VBlock]`（decontam 1 回で数百個の小確保）。
- `PixelProcessor.Highlight.cs:36,60,62` — `passes=3`・近傍 0.1・減衰 0.95 が根拠コメント無しの
  生マジックナンバー（改善サイクル規約に反する数少ない箇所）。同 `:168-193` の帯候補判定と
  `Selection.cs:606-621` の閉領域復帰判定は **α を見ず**、全透明画素（RGB ゴミ）を
  strength=1 に採用し得る（Matched 系は α≥128 要求で不整合）。
- `PixelProcessor.Selection.cs:733-829` — `GetRelaxedMatchStrength` が主経路の劣化コピーとして
  併存し、定数追加のたびに 2 実装の手動同期が必要（`relaxedSatRamp` は未使用引数のまま残置。
  グレーモードの AA ソフトランプ床も relaxed 側に未適用）。
- 4 近傍 dilation/erosion のほぼ同一ループが 3 箇所複製
  （`Selection.cs:449-467` / `Achroma.cs:86-104` / `:146-164`）。
- `PixelProcessor.cs:874-913` — debug 用ブランチ再現が `RecolorPixel` の分岐を手書き複製
  （コメント自身が手動同期を警告。過去に乖離で踏んだ形跡あり）。
- `ColorZone.Match.cs:18-24` / `ColorZone.cs:248` — Unity の `Color ==`（近似比較）で
  キャッシュ無効化を判定する一方 `BuildSelectionKey` はビット厳密で、同一性の定義が層ごとに
  食い違う。
- `PixelProcessor.Selection.cs:340-374` — `FillSmallHoles` が `minNeighbors<=0` を検証せず、
  プリセット JSON 由来の 0 で relaxed 許可領域全体が毎パス湧く。
- `PixelProcessor.cs:1019-1026` — `BoxDownsample` の src 範囲がクランプ無しで、scale と
  dst 寸法の整合を呼び出し側契約に丸投げ（不整合で IndexOutOfRange）。
- （既知: 監査 L-2）`PreviewJob.cs:68-70` — 旧 CTS の Cancel 直後 Dispose。タイミング依存の
  `ObjectDisposedException` が世代不一致として黙殺され診断困難。
- （既知: 監査 L-6）`PixelProcessor.cs:89-91` — 選択キーのマスクハッシュが maskW/maskH を
  含まない。
- （既知: 監査 S-5 同族）`ColorZone.Match.cs:430-435` — `IsInRect` の UV がピクセル角基準
  （+0.5 補正なし）で矩形境界が半画素ずれる。
- （既知: perf F4）`ZoneAutoTuner.Tolerance.cs:529-567` ほか — AutoTuner の全画面走査
  約 15 本が単スレッド・HSV 毎回再計算のまま（キャンセル対応は入った）。
  `HighlightSampleCorrector.cs:48-57` も既製の並列ヒスト基盤未使用。

---

## 5. Infra・AI マスク提案・自動化・Debug

### 高

- **[高]** `Code/MaskSuggest/MaskSuggestController.cs:56-60` — `svc.StateChanged +=` の
  購読解除経路がコードベース全体に存在しない（`-=` は grep 0 件 [裏取り済み]）。サービスは
  ドメイン寿命の静的保持のため、ウィンドウを閉じても旧コントローラがイベント経由で生存する。
  再オープン後は新旧 2 購読者が並び、旧側が `TryTakeProposal`（:126-133）で提案を先取りして
  捨てる（「クリックしても時々何も起きない」）、破棄済み EditorWindow への
  `RequestRepaint`（fake-null は `?.` をすり抜ける）を招く。[横取りの実挙動は報告のみ]

### 中

- **[中]**（既知: 監査 L-1 の格上げ）`Infra/AtomicFile.cs:26-30` — 「クラッシュで壊さない」が
  目的のクラスなのに一時ファイルを fsync（`Flush(true)`）せず rename しており、電源断で
  新旧両方を失い得る（rename の原子性はデータ永続性を保証しない）。
- **[中]** `Infra/MaskFileStore.cs:109-118` — `FromJson` が null を返すケースで
  `unreadable=false` のまま返す。SessionFileStore（:126）は同ケースを `unreadable=true` に
  しており非対称。破壊的保存の抑止（`lastLoadFailed`）が効かず、手描きマスクの
  空保存→恒久喪失が起き得る。
- **[中]** `Infra/PathUtils.cs:19-23` — `StartsWith` がカルチャ依存・大小区別で、`..` の正規化も
  無い。Windows のドライブレター大小揺れで null を返し、`Assets/../外部` が「Assets 相対」として
  通る。`IrocaAutomation.IsWithinProjectRoot`（OrdinalIgnoreCase）と判定基準が食い違う。
- **[中]** `Infra/PresetStore.cs:38,55,117-119` — `EnsureDirectory` / `Directory.GetFiles` が
  try 外で、権限不足・読み取り専用時に例外が UI ボタンハンドラ / MCP へ素通し。
- **[中]** `SentisIntegration/SentisModelRepository.cs:100-122` — 固定名
  `Assets/IrocaModelImportTemp` を作業フォルダにし、finally で**フォルダごと削除**。
  ユーザーが偶然同名フォルダを持っていると中身ごと消える。GUID 付き一意名へ。
- **[中]**（既知: perf A2）`MaskSuggest/MaskSuggestController.cs:180-203` — コミットの
  `GetPixels32` + `IncludeAaTransition` がメインスレッド同期のまま（4K で目に見える固まり）。
- **[中]**（既知: 監査 M-3 の具体化）`Automation/IrocaAutomation.cs:617-624` —
  `ResolveOutputPath` に `output == source` のガードが無く、MCP/エージェント経由で
  **元テクスチャ PNG をそのまま上書き**できる（非可逆破壊）。
- **[中]** `MaskSuggest/MaskSuggestSection.cs:181` — 再起動ボタンが
  `OpenProject(Directory.GetCurrentDirectory())`。CWD がプロジェクトルートである保証は無く、
  `Path.GetDirectoryName(Application.dataPath)` を使うべき。
- **[中]** `MaskSuggest/MaskSuggestModelDownload.cs:93,156-158` — モデル置き場が
  LOCALAPPDATA の全プロジェクト共有なのに複数 Unity インスタンス間の排他が無い
  （`.download` の無条件削除で共有違反、読み込み中 onnx の削除）。ロックファイル等が必要。
- **[中]** `Infra/MaskFileStore.cs:146-248` — orphan 処理一式（約 100 行）が
  SessionFileStore とほぼ逐語一致の重複。実際に Load の null 検査は既に乖離済みで、
  片側だけ直す事故が現実化している。拡張子パラメータ化で共通化を。

### 低

- `MaskSuggestModelDownload.cs:66` — `Start()` の `CreateDirectory` が try 外で、失敗が
  `Error` プロパティに載らず UI に出ない。
- `MaskSuggestModelDownload.cs:62-74,87` — 再試行が常に file 0 から。検証済み配置済みでも
  約 40MB を再取得（ハッシュ一致でスキップ可能）。
- `MaskSuggestBurstWatch.cs:36-37,49-53` — 監視が「最初の update まで」+ メインスレッド発
  ログのみ購読で、初回推論時（数分後）・ワーカースレッド発の Burst エラーは検知漏れ。
- `SentisIntegration/EditorIteratorPump.cs:45-51` — `Stop()` がコールバック参照を null クリア
  せず、CHW 12MB 級を捕捉したクロージャが次の Start まで残る。
- `SentisMaskSuggestService.cs:233,439-440` — `PeekOutput as Tensor<float>` の null 許容
  キャスト直後にメンバアクセス。出力名変更時に診断不能な NRE メッセージになる。
- `MaskFileStore.cs:198-207`（SessionFileStore も同型） — `File.Move` 後の
  `SetLastWriteTimeUtc` 失敗時、退避 mtime が古いまま残り 30 日猶予を待たず即削除され得る。
- （既知: 監査 L-2）`MaskFileStore.cs:81,174-192` — `.bak` が CleanupOrphans の対象外で
  恒久残留。
- `SessionFileStore.cs:81-107` — 保存中の `state.maskState` インプレース差し替え。
  BG ジョブが将来セッションを読むと空マスクを観測する構造リスク。スナップショット化が安全。
- `Infra/IrocaAssetWatcher.cs:15-24` — フォルダ削除時は `OnWillDeleteAsset` が 1 回しか
  呼ばれず、配下テクスチャのキャッシュは即時削除されない（30 日 orphan 経路頼み）。
- `Infra/BuildHelper.cs:54-66` — `included` 0 件でも「エクスポート完了(0 アセット)」で
  正常終了し、空配布物が exit code に現れない。
- （既知: perf A12）`MaskSuggestSection.cs:107` — NoModel 中は毎 Layout に `File.Exists`×2。
- `MaskSuggest/Ops/SamZoomOps.cs:130-158` — `ExtractCrop`/`PasteCrop` に境界検証が無く、
  Harness の CLI 引数から生 `IndexOutOfRangeException` で落ちる。
- `Debug/DebugCaptureContext.cs:100-102` — コメント「clone しない」と実装 `Clone()` の矛盾。
  契約が判断できず将来の「最適化」事故を誘発。
- `Debug/DebugDumpStore.cs:112-223` — delta 着色等が DebugWindow と重複実装
  （凡例と PNG の色ずれリスク）。`:264-268` の手書き JSON エスケープは制御文字非対応。
- `scripts/headless-run/Harness.cs:85-93` — `ReadRaw` が w/h 未検証
  （int オーバーフロー・短読み）で破損 raw が不明瞭な例外になる。
- `Automation/IrocaAutomation.cs:660-671` — `WriteMcpFile` が `AtomicFile` を使わず直書き。
  クラッシュで壊れた result.json を呼び出し側が読む。

---

## 6. ビルド・CI・テスト基盤

### CI / リリース

1. **[高]**（既知: 監査 1-2 の格上げ）`release-verify.yml` — リリース資産の
   アップロード/差し替え/削除は `edited` イベントを**発火しない**。publish 後に zip だけ
   差し替えても再検証が走らず、SHA256 検証の存在意義が publish 直後の一度きりに限定される。
2. **[中]** `release-verify.yml` — 検証対象が当該バージョンの zip 1 本のみ。他バージョンの
   listing 整合は未検証、unitypackage は存在チェック（warning）のみでハッシュ検証なし。
3. **[中]** workflows 3 本 — サードパーティ Action がタグ固定（SHA 固定でない）。特に
   `softprops/action-gh-release@v2` は `contents: write` 権限で動く。`dotnet-version: 8.0.x`
   も浮動。
4. **[中]**（既知: 監査 1-3/1-8）`release.yml` — タグが main 上のコミットであることを
   検証しない。VCC が読む Pages は main 配信のため、検証対象と配信物が乖離し得る。
   **main は 2026-05-14 で停止（develop から 361 遅れ + 逆方向 6 コミット分岐）**であり
   現実的リスク。
5. **[中]** `ci.yml` — 挙動テストがゼロ（型チェック + JSON 妥当性のみ）。`scripts/golden/` は
   合成入力で自己完結し、build-check の GameCI コンテナに必要な Unity DLL は既にあるため、
   決定性テストと `test_selection_key_audit.py` だけでも CI 化可能なのに配線されていない。
6. **[低]** `release.yml` — タグ名が `${{ }}` でシェルへ直接展開（インジェクション面）。
   semver 形式検証なし、prerelease 判定が `contains(beta)` の部分一致。
7. **[低]** `ci.yml` — `concurrency` 未設定。BOM チェックが 2 ファイル決め打ちで、
   mojibake 検出の CI 化（2026-06-28 レビューの対応案）は未実施のまま。

### ビルド・リリーススクリプト

8. **[高]** `scripts/Build-VpmPackage.ps1` — zip が `Code/` を再帰収集するため、
   **git 追跡外の `.meta`（ホスト Unity が生成）や未追跡ファイルがそのまま配布物に入る**。
   GUID の安定性と再現性がこの 1 台の作業ツリーに依存し、クリーン clone から同一 zip を
   再生成できない。[報告のみ]
9. **[中]**（既知: 監査 1-5）同上 — zip 自体も非決定的（mtime 焼き込み、PowerShell 版による
   JSON 整形差）。listing の SHA と手動アップロード zip の同一性は人間の注意力だけが担保。
10. **[中]**（既知: A-4）`docs/RELEASING.md:40` — 「公開前の一度きり復旧タスク」が
    2026-07-02 起票のまま 1 か月超未消化。リリース手順が一度も end-to-end で通っていない。
11. **[低]** Build-VpmPackage — 作業ツリーのクリーン確認・CHANGELOG 節の存在確認・
    unitypackage ファイル名が release-verify の期待形式かの検証がいずれも無い。

### テスト基盤

12. **[高]** N-3（ベースラインがどの git にも無い）→ §2 参照。
13. **[中]** `tools/check_visual_review.py` / `visual_review.py` — 出荷ゲートの鮮度判定が
    全面 mtime 依存。checkout・stash 復元で偽ブロックと偽通過が両方起こり、承認と
    レビュー対象 diff の対応付けが無い。
14. **[中]** `tools/check_visual_review.py` — ゲート対象が Code/ ほぼ全部（UI/Infra 含む）で
    過剰に広く、UI 文言 1 行の修正でも「csharp compare 再実行 or SKIP_VISUAL_REVIEW=1」の
    二択になる。**バイパスを日常動作として学習させ、ゲート自身を弱める**設計。
15. **[中]** 同 `run_quality_gate_validate` — 「高速な --validate」が較正 CSV 不在時に
    フル再計測へフォールバック（fresh clone で pre-commit が数分ブロック）。さらに
    `headless_io.run` / `golden_lib.run_csharp` に subprocess timeout が無く、ハーネスの
    ハングで pre-commit / pytest が無期限に固まる。
16. **[中]** `scripts/hooks/pre-commit` — 実効性が「clone ごとの手動 hooksPath 設定」+
    「approved.json はどの git にも無い」に依存し、CI 側の裏取りが皆無。フック未設定・
    SKIP 変数・手書き approved.json のいずれでも無音で素通り。venv パスは Windows 決め打ち。
17. **[中]** `dev_safe/Tests/regression/fixtures.py` — 1 ケース = 1 dotnet プロセス + 巨大 raw
    往復で、作業領域が固定共有ディレクトリのため pytest-xdist 並列でファイル名衝突する構造。
    使用済み raw が掃除されず蓄積（dev_safe は現在約 10GB）。
18. **[中]** `test_csharp_quality_gate.py` — 既定実行は bandana 1 被写体のみで、全被写体は
    `VACC_CSHARP_GATE_FULL=1` の手動設定頼み（コメントが言う「CI の重い tier」は存在しない）。
    既定の緑が品質カバレッジを過大表示する。
19. **[中]** `scripts/golden/` — golden の再生成が全ハッシュ無警告一括上書き可能で、
    golden テスト自体が改善サイクルの標準コマンドにも pre-commit にも含まれない。
    `unity_dll_missing()` は csproj のパス解決を手書きミラーしており、陳腐化すると
    防ぐはずだったサイレント skip が再発する。
20. **[低]** ルートに `pytest.ini` / `pyproject.toml` が無く、rootdir・マーカー登録が曖昧
    （`pytest scripts/golden/` 単独実行で未登録マーカー警告）。
21. **[低]** `dev_safe/Tests/requirements.txt` — 下限指定のみでロックが無く、
    numpy/scipy/Pillow の更新で IoU・golden 出力が動き得る。
22. **[低]** `tools/visual_review.py` — 比較パネルが高さ 512px サムネイルのみで、
    チェックリストが要求する「元にないノイズ・質感」は 2K/4K では潰れて見えない。
    等倍クロップ列・差分ヒートマップが無く、視覚レビューの検出力が構造的に制限されている。

---

## 7. リポジトリ衛生

- **[中]** ルート `CODE_REVIEW.md`（2026-06-28 付）— High 指摘（package.json 破損・mojibake）は
  全て解消済みで内容が現状と完全乖離。公開リポのルートに「壊れている」と書かれた古い文書が
  残り続けている。削除か docs/ へのアーカイブを。[裏取り済み（解消の事実）]
- **[中]** `docs/booth_new/promo/out/`（PNG 5 枚 + 756KB HTML、計約 2.7MB）が git 追跡。
  `.gitignore` は `docs/booth/out/` を除外しており生成物ポリシーが不整合。[報告のみ]
- **[中]** `.gitignore` の「内部向けドキュメントは公開リポに含めない」宣言と、日付付き内部
  レビュー文書（監査・perf 評価・ロードマップ等）の追跡が矛盾。方針をどちらかに統一。[報告のみ]
- **[中]** dev_safe（プライベート側）— `diag_*`/`probe_*`/`sweep_*` 等 100 本超の使い捨て
  スクリプトと `_*_out.txt` がトップ直下にフラットに追跡され、コミットは
  「auto-sync 日時」のみ。正規の GT 生成スクリプトが埋もれ、履歴から意図が追えない。[報告のみ]
- **[低]** `.gitignore` のベアパターン `Tests` / `texture_sample` が任意階層にマッチし、
  将来 `Code/Tests/` を作っても git が黙って無視する地雷。[報告のみ]
- **[低]** 追跡済み `CLAUDE.md` が gitignore 済み `.claude/instructions/*.md` を @-include して
  おり fresh clone で参照切れ。通知先の ntfy.sh トピックは認証なしの公開トピックで、
  購読・偽通知注入が誰でも可能。[報告のみ]
- **[低]** dev_safe に literal `"` を含むディレクトリ名の追跡ファイル 4 件（過去のパスバグの
  痕跡、Windows で checkout 不能）。[報告のみ]
- **[低]** `README.md` 見出しの「Ver 0.2.0」ハードコード — バージョンが 4 か所に重複し、
  release.yml の整合検証は README を対象外。[報告のみ]

---

## 8. 推奨対応順（私見）

1. **N-8 + N-3**（dev_safe の push 復旧 + Baselines の git 追跡化）— 検証基盤そのものの保全。
   他の全改善の前提になる
2. **N-2**（ハーネス既定値の単一ソース化）— 「実 C# を測る」原則の穴。改善サイクルの
   信頼性に直結し、修正は小さい
3. **N-1**（CleanAchromaFringe のマスク参照追加）— ユーザーの明示的なマスク指定を破る
   契約違反。GT + 視覚レビュー付きで 1 件ずつ
4. **N-5 / N-7 / ExportView キャンセル残骸**（UI の状態整合系）— いずれも数行〜数十行で、
   「プレビュー＝出力」「マスクは守られる」という中核保証まわり
5. **N-4**（StateChanged 購読解除）— ウィンドウ開閉を跨ぐ不可解な不具合の芽
6. **N-6 + §6-1**（zip の git 由来化・release-verify の穴）— 次のリリース前までに。
   A-4 復旧タスクの消化とセットで
7. UX 文言系（マスククリア/編集モード/ブラシ単位のツールチップ不一致、進捗 10% 固定、
   完了通知文言）— 実装コスト極小で非エンジニア層の体感に直結
8. perf 残課題は `docs/performance_review_2026-07-29.md` の推奨順（F12 → F3/F4 → A1〜 → U1〜）
   を維持。本レビューの新規 perf 指摘（RecoverEnclosedNeutral、FF ビットパック等）は
   その次に編入

## 手法と限界

- 4 領域の並列レビュー（静的読解）+ 過去指摘の現況確認。実行による再現確認はしていない
  （「失敗シナリオ」は機構からの推論）。
- §2 の N-1/N-2/N-5/N-7 と「前回指摘の現況」表は本レビューで現物を直接確認した。
  [報告のみ] の項目は着手前に該当行の確認を推奨（行番号は `31e2948` 時点）。
- 「問題なし」領域の全数検証はしていない。過去文書が健全と確認した項目
  （並列化の disjoint-write 規律、ArrayPool の finally 返却、座標変換、非同期世代管理など）は
  今回も概ね維持されていることを領域レビューが確認している。
