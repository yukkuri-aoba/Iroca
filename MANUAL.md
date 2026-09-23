# いろか ユーザーマニュアル

*[日本語](#日本語) | [English](#english)*

> 設定の効き方をその場で試せる **[オンライン版マニュアル](https://yukkuri-aoba.github.io/Iroca/manual/)** もあります。
> An [online version](https://yukkuri-aoba.github.io/Iroca/manual/) with interactive demos is also available.

---

## 日本語

### 目次

- [インストール](#インストール)
- [基本的な使い方](#基本的な使い方)
- [カラーゾーンの設定](#カラーゾーンの設定)
- [加工設定](#加工設定)
- [プレビュー機能](#プレビュー機能)
- [マスク（除外・含める）](#マスク除外含める)
- [AI マスク提案（実験的機能）](#ai-マスク提案実験的機能)
- [プリセット](#プリセット)
- [エクスポート](#エクスポート)
- [トラブルシューティング](#トラブルシューティング)
- [よくある質問](#よくある質問)

---

### インストール

#### 前提条件

- Unity 2022.3（2022.3.22f1・Windows で検証しています。自動調整と AI マスク提案に使う Unity Sentis 2.1.3 は 2022.3.11f1 以降が必要です）
- PNG / JPG のテクスチャはそのまま使えます
- PSD・TGA・EXR などは Read/Write Enabled が必要です。無効のときはウィンドウに警告が出て、「Read/Write を自動で有効にする」ボタンで有効にできます（Undo できないため確認が出ます）

#### 手順

1. [GitHub Releases](https://github.com/yukkuri-aoba/Iroca/releases) から `Iroca_Installer.unitypackage` をダウンロードします。
2. Unity のプロジェクトウィンドウへドラッグ＆ドロップし、「Import」を押します。
3. 確認ダイアログで「Install」を押すと、いろかの最新版がダウンロードされ、プロジェクトの `Packages` に入ります（インターネット接続が必要です。インストーラ自体は自動で消えます）。
4. メニューの `Tools > いろか` でウィンドウを開きます。

VCC / ALCOM を使っているなら、リポジトリ `https://yukkuri-aoba.github.io/Iroca/index.json` を追加して、プロジェクトの管理画面から「いろか」を追加する方法でも導入できます。新しい版が出たら、VCC / ALCOM から更新するか、インストーラをもう一度読み込みます。

> 旧版（0.1.0、旧名 VRC Avatar Color Changer）を使っていた場合は、`Assets/VACC` フォルダを削除してください。いろかとは別物として入るため、残すと旧版も動いたままになります。プロジェクト内に保存したプリセットが必要なら、削除の前にバックアップしてください。

---

### 基本的な使い方

ウィンドウは ① 元テクスチャ → ② カラーゾーン → ③ プレビュー → ④ エクスポート の順に上から並んでいます。番号どおりに進めれば色替えできます。

#### ステップ 1: 元テクスチャを選ぶ

「① 元テクスチャ」の「テクスチャ」欄へ、色を変えたいテクスチャをドラッグします。

<!-- スクリーンショット: テクスチャ選択後のウィンドウ全体 -->

#### ステップ 2: カラーゾーンを追加する

「+ ゾーン追加」を押します。色替え 1 つぶんが「カラーゾーン」1 つです。

#### ステップ 3: 変える色を指定する

1. 「サンプルカラー」欄の右の「スポイト」を押し、プレビュー上の変えたい色をクリックします。いちばん鮮やかな部分を選ぶとうまくいきます。
2. すぐ下の「自動調整」を押します。そのパーツの暗部からハイライトまでを覆うように、許容範囲などを自動で合わせます（AI モデルが必要です。未導入ならウィンドウ上部に案内が出ます）。
3. 範囲が広い・狭いときは「許容範囲」で微調整します。

自動調整を使わなくても、新しいゾーンは許容範囲 0.20 で始まるので、色を指定すればプレビューはすぐ変わります。

> 色は必ず「スポイト」ボタンで取ってください。欄をクリックして開くカラーピッカーのスポイトは、色がわずかにずれるうえクリック位置が残らず、自動調整が AI 提案を使えません。

#### ステップ 4: 変更後の色を決める

「変更先カラー」をクリックして色を選びます。プレビューにすぐ反映されます。

#### ステップ 5: 仕上がりを調整する（必要なら）

| 設定 | 内容 |
|---|---|
| 模様保持 | 元の柄をどれだけ残すか（0 = ベタ塗り、1 = 柄を残す。既定 1.0） |
| 出力彩度 | 純色がベタ塗りに見えるとき 0.7〜0.9 に下げると陰影が戻る（既定 1.0） |
| 連続領域モード | つながった塊だけに変換を絞り、離れた同色や背景への色移りを防ぐ（既定 ON） |

さらに細かい調整は「[カラーゾーンの設定](#カラーゾーンの設定)」を参照してください。

#### ステップ 6: 保存する

「④ エクスポート」で保存します（→「[エクスポート](#エクスポート)」）。

---

### カラーゾーンの設定

#### 基本の項目（常に表示）

**サンプルカラー**

色替え対象の基準色です。この色に近いピクセルが対象になります。右の「スポイト」ボタンで取ると、クリック位置も「自動調整」の手がかりとして記憶します。

**自動調整**

テクスチャを解析して、許容範囲・彩度制限などをまとめて決めます。スポイトした位置に AI マスク提案（MobileSAM）をかけ、そのパーツの暗部からハイライトまでを取りこぼさないように導出します。スポイトの直後に押すのが最も効果的です。

- 元テクスチャが未設定、画素を取り出せない、サンプルカラーが未指定（白のまま）のときは押せません。
- AI が未導入のときは導入の案内が出ます（→「[AI マスク提案](#ai-マスク提案実験的機能)」の「必要なもの」）。準備中のときは終わるまで待ちます（進捗バーと中止ボタンが出ます）。
- カラーピッカーで色を指定したゾーンは位置がないため、AI 提案なしで解析します（通知が出ます）。
- 自分で値を変えているときだけ、上書きの確認が出ます。

**許容範囲**

色の一致をどこまで許すかです（0.0〜1.0）。低いほど厳密（選択が狭い）、高いほど広く拾います（ノイズも増えます）。目安は 0.15〜0.40 です。

**連続領域モード（Flood Fill）**

色が一致した領域のうち、確信度の高い芯を含む「つながった塊」だけに変換を絞ります。離れた同色パーツや背景へのにじみが自動で外れます。既定は自動で、シードは要りません。

特定の塊だけ残したいときはシードを置きます。「シード (任意)」行の「指定」を押してからプレビューをクリックするか、プレビューを Shift+クリックします。「自動へ」で解除します。

**変更先カラー**

変えたあとの色です。

**模様保持**

元の明度をどれだけ残すかです。0 に近いほどベタ塗り風、1 に近いほど元の柄をそのまま残します（既定 1.0）。

**出力彩度**

再着色後の鮮やかさです（既定 1.0）。純赤など彩度 100% の色は明暗が潰れてベタ塗りに見えがちです。0.7〜0.9 に下げると、色相は保ったまま陰影が戻ります。

#### 詳細の項目（「詳細設定」を開くと表示）

上から処理の走る順（選択の範囲 → ハイライト → 暗部・無彩色 → 色の写り方 → マッチング距離の重み）に並んでいます。触りすぎたときは、ゾーン末尾の「詳細を既定値に戻す」で詳細だけ初期化できます（色・許容範囲・名前は残ります）。

**サンプル自動補正（再着色）**（既定 ON）

影をスポイトしても、パーツの明るい面が変更先の色に合うよう基準を補正します。クリックした画素そのものを変更先の色にしたいとき、意図的に明るく塗りたいときは OFF にします。選択範囲は変わりません。

**エッジ柔らかさ**（既定 0）

0 で硬いエッジ、上げるとアンチエイリアス境界をなめらかに拾います。ぼかしのあるテクスチャ向けです。

**彩度制限（影の厳しさ）**（既定 0.50）

薄い影や AO（暗い陰り）をどこまで拾うかです。上げるとはみ出しが減り、境界に色のドットが残りやすくなります。下げるとその逆です。

**彩度ガード（無彩色よけ）**（既定 0）

鮮やかな色を選んだとき、白・黒・灰色が混ざるのを防ぎます。許容範囲を大きく上げるときに使います。選んだ色が灰色寄りなら自動で無効になります。

**ハイライト補助**（既定 ON）

高明度・低彩度のハイライト（鏡面反射・光沢）もマッチさせ、光沢素材の変換漏れを防ぎます。ON のときに出る「ハイライト帯の拡張」（既定 ON）は、本体につながった描き込みハイライトまで範囲を広げます。

**ハイライト白寄せ合成**（既定 OFF）

明部を白へ寄せて、鏡面ハイライトの白い反射を表現します。光沢・プラスチック向けです。ON のときに出る「ハイライト自動補正」（既定 OFF）は、パーツの地色を自動で見つけて白寄せを全体に効かせます。髪など細い房の多いテクスチャでは広がりすぎることがあるので、その場合は OFF にします。

**暗部・無彩色**

| 設定 | 説明 | 既定 |
|---|---|---|
| シャドウ彩度低下 | この明度より暗いピクセルの彩度を落とす。下げると暗い色も鮮やかに染まる | 0.35 |
| シャドウ巻き込み最低彩度 | 暗いピクセルを影として拾うのに必要な最低彩度。純粋なグレー・黒の色付けを防ぐ | 0.05 |
| 自動しきい値(無彩色判定) | サンプルの彩度がこの値以下なら、色相を無視して無彩色（黒・グレー）として抽出する | 0.05 |

**マッチング距離の重み**

色の距離式そのものの係数です。ふつうは触りません。

| 設定 | 説明 | 既定 |
|---|---|---|
| 明度重み | 高いほど明度差に敏感（別素材を分離しやすい）。低いほど同じ素材の影・ハイライトを吸収する | 1.0 |
| 彩度距離重み | 高いほど彩度差に敏感 | 0.15 |
| 彩度ランプスケール | 大きいほど彩度閾値付近でなだらかにフェードする | 0.10 |

これらの値もプリセットに保存されます。

#### ゾーンの優先度（並び順）

ゾーンが重なる部分には、リストで上にあるゾーンだけが適用されます。各ゾーン左の `☰` をドラッグして並べ替えます。

---

### 加工設定

全ゾーン共通の、エッジとノイズの処理です。

#### エッジぼかし

選択境界をぼかして、エッジの色をなじませます（0〜5）。

| 値 | 効果 |
|---|---|
| 0 | オフ（エッジがシャープ） |
| 0.5〜1.5 | 標準的なぼかし |
| 2.0 以上 | 強いぼかし（なめらかなテクスチャ向け） |

#### AA境界クリーンアップ

アンチエイリアス境界に残るドットを回収するパス数です。0 でオフ、3 が標準（既定）、4〜5 で強めです。

#### 境界クリーンアップ（α分解）

境界に出る薄汚れた中間色（ハロー）を防ぎます。既定は ON で、通常はそのままで構いません。

#### 詳細設定（折りたたみ）

通常は既定のままで構いません。

| 設定 | 説明 | 既定 |
|---|---|---|
| 穴埋めパス数 | アンチエイリアス端の孤立ドットを埋めるパス数 | 5 |
| 穴埋め最小隣接数 | 穴を埋めるのに必要な一致隣接ピクセル数。低いほど積極的に埋める | 4 |
| 境界復元 彩度最小 | 境界復元時の彩度最小閾値 | 0.02 |
| 境界復元 彩度ランプ | 境界復元時の彩度ランプ幅 | 0.08 |
| α分解 近傍半径 | α分解で背景色を推定する近傍の半径 | 4 |

---

### プレビュー機能

設定を変えると、約 0.2 秒後に自動で更新されます。

#### ズームとパン

- Ctrl + スクロール: ズーム（ピクセル単位まで拡大できます）
- ドラッグ: ビューの移動
- 中ボタンドラッグ / Alt + ドラッグ: マスクを塗っている最中でも移動できます
- 「③ プレビュー」見出し右端の「リセット」: ズームを 100% に、表示位置を先頭に戻します

#### 表示モード

- 前後比較: 変更前後を左右に並べます（高ズーム時は使えません）
- 差分表示: 変わったピクセルだけを強調します
- 「元を表示」ボタン: **押している間だけ**変更前を表示します
- 「ソロ」（ゾーンの行）: そのゾーンだけをプレビューします。表示だけの機能で、保存される内容は変わりません

#### いまのモード表示

操作行のすぐ下に、プレビュー上のクリックがいま何をするか（スポイト／シード指定／マスクを塗る・消す／AI 提案）が 1 行で出ます。**Esc でどのモードも解除できます。**

#### プレビュー上の目印

- 十字: 連続領域モードのシード位置
- 菱形: スポイトで色を取った位置

どちらもゾーンごとの色（マスクの重ね表示と同じ色）で出ます。

---

### マスク（除外・含める）

プレビューにブラシで塗って、色替えの範囲を手で直します。

- **除外マスク**: 塗った領域を色替えから外します。全ゾーンに効く共通マスクと、ゾーン別マスクがあります。
- **含めるマスク**: 塗った領域を必ず色替えします。強い光沢や、離れた場所の同じパーツなど、色の判定で拾えなかった部分を足すのに使います。ゾーン別のみです。

両方に塗られた画素は**除外が優先**されます。

<!-- スクリーンショット: 除外マスクを描いた状態のプレビュー -->

#### 使い方

操作は「Iroca マスク編集」ウィンドウにまとまっています。

1. マスク欄の「マスクを編集...」を押してウィンドウを開きます。
2. 「編集対象」で共通マスクか各ゾーンを、「マスクの種類」で除外／含めるを選びます（含めるはゾーン選択時のみ）。
3. ツールの「塗る」「消す」を選び、プレビュー上をドラッグします。ブラシサイズは 1〜64 です。同じボタンをもう一度押すか Esc で抜けます。
4. 「AI 提案」ツールでは、パーツを右クリックすると AI が推定した領域が足されます（→「[AI マスク提案](#ai-マスク提案実験的機能)」）。

重ね表示は、赤が共通の除外、ゾーンの色がゾーン別の除外、緑が含めるです。編集中の 1 枚は明るく、ほかは薄く出ますが、薄いマスクも色替えには効いています。

ウィンドウを閉じるとマスク編集モードは解除されます。

#### 含めるマスクの色の写り方

含めるマスクで足した領域は、そのゾーンの色マッチした部分と同じ素材として色替えされます。足した領域の色や明るさは、ゾーンのほかの部分の仕上がりに影響しません。

#### 取り消しとリセット

- Ctrl+Z（または「直前の操作を元に戻す」ボタン）: 直前のストロークを取り消します。
- このマスクをクリア: いま選んでいる対象・種類のマスク 1 枚だけを消します。

---

### AI マスク提案（実験的機能）

プレビュー上のパーツを**右クリック**すると、AI（MobileSAM）がそのパーツの領域を推定し、編集対象のマスクへその場で足します。手描きで囲む手間を減らせます。

#### 必要なもの（自動調整にも必要）

未導入のときは、いろかのウィンドウ上部に案内の帯が出ます。そこから 2 つとも導入できます。

1. **Unity Sentis パッケージ**: 帯の「AI 機能を有効化（Sentis を導入）」を押します。手動の場合は Package Manager →「+」→「Add package by name...」→ `com.unity.sentis`（バージョン `2.1.3`）。
2. **AI モデル（2 ファイル・合計約 44 MB）**: 帯の「モデルをダウンロード」を押します。保存先はユーザー共通のフォルダ（Windows は `%LOCALAPPDATA%\Iroca\Models`）なので、別プロジェクトでの再ダウンロードは不要です。手動の場合は [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) から 2 つの `.onnx` を取得し、「モデルフォルダを開く」で開いたフォルダへ置きます。

#### 使い方

1. マスク編集ウィンドウでツールの「AI 提案」を押します。画像の解析がここで始まるので、進捗表示が消えるのを待ちます。
2. 選びたいパーツの内側を**右クリック**（mac は Control+クリック）します。左ドラッグはプレビューの移動のままです。
3. 推定された領域が、「編集対象」「マスクの種類」で選んでいるマスクへすぐ足されます（確定ボタンはありません）。パーツが複数の島に分かれているときは、島を順に右クリックします。
4. 間違えたら Ctrl+Z で 1 つずつ戻します。足した領域は通常のマスクなので、ブラシで整えられます。

1 回の右クリックで取れるのは、つながった 1 つの領域（UV アイランド）です。テクスチャ上でいくつにも分かれたパーツ（衣装 1 着ぶんなど）を全部選ぶには、数回〜十数回のクリックが要ります。取った領域がまれに隣のパーツへはみ出すこともあるので、重ね表示で確かめてください。

#### 苦手なケース

- 白背景に白いパーツなど、見た目の境界が無いもの。手描きマスクを使ってください。
- 数十個の小さなピースに分かれたパーツ。大きな塊だけ AI で選び、残りはブラシで足すのが早道です。
- 「背景まで広がった可能性があります」と警告が出たら、Ctrl+Z で戻し、粒度を「細かい」にするか、パーツのより内側を右クリックし直します。

#### うまく動かないとき

- **左クリックしても何も起きない**: 指定は右クリックです。
- **Unity 起動後の最初の 1 回だけ遅い**: AI エンジン（Burst）のコンパイルが入るためで、異常ではありません。
- **小さいパーツで少し待つ**: 周辺を自動で拡大して推定し直すためです。
- **右クリックしてもマスクが変わらない**（「AI が領域を返しませんでした」「内部コンパイル（Burst）が失敗しています」）: Burst の初期化失敗です。同じセッションでは直らないので、欄の「Unity を再起動」を押します。再発するときは、プロジェクトの `Library\BurstCache` と `Library\Bee` を削除してから起動し直します（自動で再生成されます）。
- **「マスクが変わりませんでした（すでに追加済み）」**: クリックした領域はすでにマスクに入っています。

#### クレジット / ライセンス

この機能は [MobileSAM](https://github.com/ChaoningZhang/MobileSAM)（Apache License 2.0）を ONNX 形式に変換して使用しています。モデルの著作権は原作者に帰属します。変換済みモデルは [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) で Apache License 2.0 のもと配布しています（詳細は同リポジトリの NOTICE）。

---

### プリセット

カラーゾーンと加工設定を保存・読み込みできます。

#### 保存と読み込み

「プリセット」セクションで保存先を選び、名前を入れて「保存」を押します。一覧の「読込」で読み込み、「×」で削除します。

- プロジェクト内: `Assets` フォルダ内に保存します。Git などでチームと共有できます。
- ユーザー共通: OS のユーザーフォルダに保存します。この端末のすべてのプロジェクトで使えます。

#### マスクの保存・読み込み

「マスクも保存する」を ON にすると、塗ってあるマスクもプリセットに入ります。「マスクも読み込む」を OFF にすると、読み込み時にマスクだけ無視します。

#### JSON の書き出し・読み込み

「JSONエクスポート」「JSONインポート」で、設定をファイルとしてやり取りできます。

---

### エクスポート

「適用して保存」で保存します。出力は常に PNG です。

- 新規ファイルとして保存: ON なら元のテクスチャを残し、「ファイル名」の別ファイルに保存します。OFF なら元のファイルを上書きします（確認が出ます。元に戻せないのでバックアップをお勧めします）。
- インポート設定を引き継ぐ（既定 ON）: 元テクスチャのインポート設定（タイプ・圧縮・ミップマップなど）を引き継ぎます。
- フォルダを開く: 保存先のフォルダを開きます。
- Project で表示: 保存したテクスチャを Project ウィンドウで選択します（マテリアルへの差し替え用）。

保存に失敗したときは、エクスポート欄にエラーが残ります。詳細は Console にも出ます。

---

### トラブルシューティング

#### 図形の周りに薄い色やドットが残る

アンチエイリアスやぼかしで薄くなった縁が、変換から漏れています。次の順に試してください。

1. 彩度制限（影の厳しさ）を 0.7〜0.9 に上げる
2. 許容範囲を狭める
3. 連続領域モードで離れた残りを切り離す
4. 残った部分を除外マスクで保護する

#### 色がはみ出す

彩度制限を 0.8〜0.95 に上げ、許容範囲を狭めます。白・黒・灰を巻き込んでいるなら彩度ガードを上げます。エッジ柔らかさを 0.0〜0.5 で調整するのも有効です。

#### 境界に細かいノイズが残る

彩度制限を 0.1〜0.4 に下げます（そのぶんはみ出しやすくなります）。あわせて AA境界クリーンアップを 3〜5、エッジぼかしを 0.5〜1.5、エッジ柔らかさを 0.3〜0.7 のいずれかで試します。

#### 境界がギザギザしている・硬い

エッジぼかしを 0.5〜1.5 から入れます。足りなければエッジ柔らかさを 0.3〜0.7 に上げ、彩度制限を 0.3〜0.45 まで少し下げます。

#### 仕上がりがベタ塗りになる

出力彩度を 0.7〜0.9 に下げます。模様を残したいときは模様保持を上げます。

#### テクスチャ全体が変わってしまう

許容範囲を 0.05〜0.15 まで下げ、サンプルカラーをより限定的な色で取り直します。離れた領域まで変わるときは連続領域モードで絞ります。

#### 黒い色に変更できない

黒は明度の情報がほとんどなく、模様保持が効きにくくなります。模様保持を 0〜0.3、エッジ柔らかさを 0 にします。保護したい部分は先に除外マスクで囲っておきます。

---

### よくある質問

**Q: PSD のレイヤー構造をサポートしていますか？**

いいえ。統合済みのテクスチャが対象です。PSD があるなら、そちらを直接編集する方が確実です。

**Q: Undo は使えますか？**

マスクの描画は Ctrl+Z で取り消せます。テクスチャへの色適用は元に戻せないので、上書き保存の前にバックアップをお勧めします。

**Q: 複数のプロジェクトで使えますか？**

はい。各プロジェクトでインストーラを読み込むか、VCC / ALCOM から追加するだけです。プリセットの保存先を「ユーザー共通」にすると、プロジェクト間で設定を共有できます。

**Q: 対応しているファイル形式は？**

入力は PNG / JPG が基本で、出力は常に PNG です。TGA・EXR・PSD は、Unity が取り込んだ画素から書き出します（インポート設定の縮小・圧縮が反映されるため、原本と同じ解像度・画質とは限りません。書き出し時に知らせます）。

**Q: 大きなテクスチャでも使えますか？**

4096×4096 まで検証しています。処理はメモリ上で行うため、一時的にメモリ使用量が増えます。それより大きいテクスチャは未検証です。

**Q: マニュアルの画像のテクスチャは？**

画面写真と実演画像には、かなﾘぁさんち「[ハオラン-HAOLAN](https://booth.pm/ja/items/3818504)」のテクスチャ（一部は色替え後）を、作者の規約に基づき掲載しています。

---

## English

> The Japanese text above is authoritative. The English UI labels inside the tool are machine-translated with AI assistance, so the wording here is kept close to those labels.

### Table of Contents

- [Installation](#installation)
- [Basic Usage](#basic-usage)
- [Color Zone Settings](#color-zone-settings)
- [Processing Settings](#processing-settings)
- [Preview](#preview)
- [Masks (Exclude / Include)](#masks-exclude--include)
- [AI Mask Suggestion (Experimental)](#ai-mask-suggestion-experimental)
- [Presets](#presets)
- [Export](#export)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)

---

### Installation

#### Prerequisites

- Unity 2022.3 (tested on 2022.3.22f1 on Windows; Unity Sentis 2.1.3, used by Auto-tune and AI Mask Suggestion, needs 2022.3.11f1 or later)
- PNG and JPG textures work as they are
- PSD, TGA, EXR and similar formats need Read/Write Enabled. When it is off, the window shows a warning with an "Enable Read/Write automatically" button (it cannot be undone, so a confirmation appears first)

#### Steps

1. Download `Iroca_Installer.unitypackage` from [GitHub Releases](https://github.com/yukkuri-aoba/Iroca/releases).
2. Drag it into the Unity project window and click "Import".
3. Click "Install" in the confirmation dialog. The latest version of Iroca is downloaded into your project's `Packages` (requires an internet connection; the installer removes itself).
4. Open the window from `Tools > いろか`.

If you use VCC / ALCOM, you can instead add the repository `https://yukkuri-aoba.github.io/Iroca/index.json` and add "いろか" from your project's management screen. To update, use VCC / ALCOM or import the installer again.

> If you used the old version (0.1.0, formerly VRC Avatar Color Changer), delete the `Assets/VACC` folder. It installs separately from Iroca, so leaving it keeps the old version running too. Back up any presets saved inside the project before deleting it.

---

### Basic Usage

The window runs top to bottom: 1. Source Texture, 2. Color Zones, 3. Preview, 4. Export. Follow the numbers to recolor a texture.

#### Step 1: Pick a source texture

Drag the texture you want to recolor onto the "Texture" field under "Source Texture".

#### Step 2: Add a color zone

Click "+ Add Zone". One color zone is one recoloring.

#### Step 3: Choose the color to change

1. Press "Eyedropper" to the right of the "Sample Color" field, then click the color you want on the preview. The most vivid spot of the area works best.
2. Press "Auto-tune" just below it. It sets the tolerance and related values so the part is covered from its shadows to its highlights (the AI models are required; a notice at the top of the window offers to install them).
3. If the selection is too wide or too narrow, fine-tune it with "Tolerance".

You do not have to use Auto-tune: a new zone starts with a Tolerance of 0.20, so the preview changes as soon as you set the colors.

> Always sample with the "Eyedropper" button. The eyedropper inside the color picker (opened by clicking the field) reads a slightly different color and records no position, so Auto-tune cannot use the AI suggestion.

#### Step 4: Set the target color

Click "Target Color" and choose the new color. The preview updates right away.

#### Step 5: Adjust the result (optional)

| Setting | What it does |
|---|---|
| Pattern Preserve | How much of the original pattern to keep (0 = flat recolor, 1 = keep pattern; default 1.0) |
| Output Saturation | Lower it to 0.7-0.9 when a pure color looks flat, and the shading comes back (default 1.0) |
| Connected Region (Flood Fill) | Restricts recoloring to one connected region and avoids bleed into separate parts or the background (default ON) |

For finer controls, see [Color Zone Settings](#color-zone-settings).

#### Step 6: Save

Save under "Export" (see [Export](#export)).

---

### Color Zone Settings

#### Core controls (always shown)

**Sample Color**

The reference color for the target. Pixels close to it are selected. When you take it with the "Eyedropper" button, the clicked position is also remembered as the hint for "Auto-tune".

**Auto-tune**

Analyzes the texture and sets the tolerance, saturation strictness, and related values together. It runs the AI mask suggestion (MobileSAM) at the sampled position so that the part is covered from its shadows to its bright highlights. It is most effective right after sampling.

- It is disabled when the source texture is not set, when no pixels can be obtained, or when the sample color is still unset (white).
- If the AI is not installed, you are asked to install it (see "Requirements" under [AI Mask Suggestion](#ai-mask-suggestion-experimental)). If it is still getting ready, Auto-tune waits (a progress bar and a Cancel button are shown).
- A zone whose color came from the color picker has no position, so it is analyzed without the AI suggestion (a notice is shown).
- A confirmation before overwriting appears only when you have changed values by hand.

**Tolerance**

How far a color can be from the sample and still match (0.0-1.0). Lower is stricter (narrower selection); higher is looser (wider, with more noise). About 0.15-0.40 works for most cases.

**Connected Region (Flood Fill)**

Restricts recoloring to the connected region that contains a high-confidence core. Same-color parts elsewhere and bleed into the background drop out automatically. It is automatic by default, with no seed needed.

To keep one particular region, set a seed: press "Set" on the "Seed (optional)" row and click the preview, or Shift+click the preview. "Auto" clears it.

**Target Color**

The color applied after recoloring.

**Pattern Preserve**

How much of the original brightness to keep. Near 0 gives a flat recolor; near 1 keeps the original pattern as is (default 1.0).

**Output Saturation**

The vividness after recoloring (default 1.0). Fully saturated colors such as pure red flatten the shading and look solid-filled. Lowering it to 0.7-0.9 keeps the hue while bringing the shading back.

#### Detail controls (open "Details" to show them)

They are laid out in the order the processing runs: Selection range, Highlights, Shadows and neutrals, Color mapping, Matching distance weights. If you over-tweak them, "Reset details to default" at the end of the zone restores only the detail values (color, tolerance, and name are kept).

**Auto Sample Anchor** (default ON)

Adjusts the reference so the lit side of the part matches the target color even when you sampled in a shadow. Turn it OFF to map the clicked pixel exactly to the target, or for an intentionally brighter result. The selection does not change.

**Edge Softness** (default 0)

0 is a hard edge; raising it picks up anti-aliased boundaries more smoothly. For blurred textures.

**Saturation Strictness** (default 0.50)

How far faint shadows and AO are picked up. Raising it reduces bleed but tends to leave color dots at edges; lowering it does the opposite.

**Saturation Guard** (default 0)

Keeps white, black, and gray out of the result when you pick a vivid color. Use it when you raise the tolerance a lot. It disables itself if the picked color is already grayish.

**Highlight Recovery** (default ON)

Also matches high-brightness, low-saturation highlights (specular, gloss) so glossy materials are not left unrecolored. "Highlight Band Expansion" (default ON), shown while it is ON, extends the range into drawn-in highlights connected to the body.

**Highlight White Blend** (default OFF)

Pushes bright areas toward white to reproduce the white reflection of specular highlights. For glossy or plastic materials. "Auto Highlight Sample" (default OFF), shown while it is ON, finds the part's base tone so the white blend covers the whole part. On hair-like textures with many thin strands it can spread too much; turn it OFF there.

**Shadows and neutrals**

| Setting | Description | Default |
|---|---|---|
| Shadow Desaturation | Pixels darker than this lose saturation. Lower it to let dark colors recolor more vividly | 0.35 |
| Shadow Forgiveness Sat Min | Minimum saturation to pick up a dark pixel as shadow. Prevents pure gray or black from being colorized | 0.05 |
| Auto Grayscale Threshold | If the sample's saturation is at or below this, hue is ignored and the area is treated as grayscale (black/gray) | 0.05 |

**Matching distance weights**

The coefficients of the color-distance formula itself. You normally leave these alone.

| Setting | Description | Default |
|---|---|---|
| Value Weight | Higher is more sensitive to brightness (separates different materials); lower absorbs shadow and highlight of the same material | 1.0 |
| Sat Distance Weight | Higher is more sensitive to saturation differences | 0.15 |
| Sat Ramp Scale | Larger fades more gradually near the saturation threshold | 0.10 |

These values are saved in presets too.

#### Zone priority (list order)

Where zones overlap, only the zone higher in the list is applied. Drag the `☰` handle on the left of a zone to reorder.

---

### Processing Settings

Edge and noise handling, shared by all zones.

#### Edge Feather

Blurs the selection boundary so edge colors blend in (0-5).

| Value | Effect |
|---|---|
| 0 | Off (sharp edges) |
| 0.5-1.5 | Standard blur |
| 2.0+ | Strong blur (for smooth textures) |

#### AA Edge Cleanup

Number of passes that recover dots left at anti-aliased boundaries. 0 is off, 3 is standard (default), 4-5 is strong.

#### Edge Decontamination

Prevents the muddy mid-color (halo) that appears at edges. It is ON by default and usually fine to leave that way.

#### Details (collapsed)

The defaults are usually fine.

| Setting | Description | Default |
|---|---|---|
| Hole Fill Passes | Passes that fill isolated dots at anti-aliased edges | 5 |
| Hole Fill Min Neighbors | Matched neighbors needed to fill a hole. Lower fills more aggressively | 4 |
| Boundary Sat Min | Minimum saturation threshold for boundary recovery | 0.02 |
| Boundary Sat Ramp | Saturation ramp width for boundary recovery | 0.08 |
| Decontamination Radius | Neighborhood radius used to estimate the background color | 4 |

---

### Preview

After you change a setting, the preview updates automatically in about 0.2 seconds.

#### Zoom and pan

- Ctrl + Scroll: zoom (down to pixel level)
- Drag: pan the view
- Middle-button drag / Alt + drag: pans even while you are painting a mask
- "Reset" at the right end of the "③ Preview" heading: returns the zoom to 100% and the view to the top

#### View modes

- Compare: shows before and after side by side (not available at high zoom)
- Diff: highlights the pixels that changed
- "Original" button: shows the texture before recoloring **while you hold it down**
- "Solo" (on a zone's row): previews only that zone. It affects the preview only; what gets saved does not change

#### Current mode row

Just below the toolbar row, a single line shows what a click on the preview does right now (Eyedropper / Seed / Painting or Erasing a mask / AI Suggest). **Press Esc to leave any of these modes.**

#### Markers on the preview

- Cross: the Connected Region seed position
- Diamond: the position you sampled with the Eyedropper

Both are drawn in the zone's own color, the same color as its mask overlay.

---

### Masks (Exclude / Include)

Paint on the preview to fix the recolored area by hand.

- **Exclude mask**: keeps the painted area out of recoloring. There is a common mask for every zone, and per-zone masks.
- **Include mask**: always recolors the painted area. Use it to add what color matching could not reach, such as strong gloss or a piece of the same part that sits somewhere else. Per-zone only.

Where both are painted, **Exclude wins**.

#### How to use

Mask editing lives in the "Iroca Masks" window.

1. Press "Edit Masks..." in the mask section to open it.
2. Choose the common mask or a zone under "Edit Target", and Exclude or Include under "Mask Type" (Include needs a zone).
3. Pick the Paint or Erase tool and drag on the preview. Brush size ranges from 1 to 64. Press the same button again, or Esc, to leave.
4. With the AI Suggest tool, right-click a part and the AI-estimated region is added (see [AI Mask Suggestion](#ai-mask-suggestion-experimental)).

In the overlay, red is the common exclude mask, the zone's color is a per-zone exclude mask, and green is include. The mask being edited is bright and the rest are dim, but the dim ones are still in effect.

Closing the window leaves mask editing.

#### How include-mask areas are colored

Regions added with the include mask are recolored as the same material as the color-matched part of that zone. Their own color and brightness do not affect how the rest of the zone comes out.

#### Undo and reset

- Ctrl+Z (or the "Undo Last Action" button): undoes the last stroke.
- Clear this mask: clears only the currently selected target and kind.

---

### AI Mask Suggestion (Experimental)

**Right-click** a part on the preview and the AI (MobileSAM) estimates that part's region and adds it to the mask being edited right away. It saves you from outlining parts by hand.

#### Requirements (also required by Auto-tune)

When they are missing, a notice appears at the top of the Iroca window. Both items can be set up from there.

1. **Unity Sentis package**: press "Enable AI feature (install Sentis)" in the notice. To install manually: Package Manager → "+" → "Add package by name..." → `com.unity.sentis` (version `2.1.3`).
2. **AI models (2 files, ~44 MB total)**: press "Download models" in the notice. They are stored in a per-user shared folder (`%LOCALAPPDATA%\Iroca\Models` on Windows), so other projects do not need to download them again. To install manually, get the two `.onnx` files from [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) and place them into the folder opened by "Open model folder".

#### How to use

1. Press the "AI Suggest" tool in the mask window. Image analysis starts here, so wait for the progress indicator to disappear.
2. **Right-click** inside the part you want (Control+click on macOS). A left drag still pans the preview.
3. The estimated region is added at once to the mask chosen by "Edit Target" and "Mask Type" (there is no confirm button). If the part is split into several islands, right-click them one by one.
4. Undo mistakes one at a time with Ctrl+Z. Added regions are ordinary mask data, so you can touch them up with the brush.

One right-click picks one connected region (UV island). Selecting a part that is split into many pieces on the texture (such as a whole outfit) takes several to a dozen or more clicks. A picked region occasionally spills into a neighboring part, so check the overlay.

#### Known limitations

- Parts with no visible boundary, such as white pieces on a white background. Use hand-painted masks there.
- Parts split into dozens of tiny pieces. Select the large chunks with AI and fill the rest with the brush.
- If you see the "may have spread into the background" warning, undo with Ctrl+Z, then set the granularity to "Fine" or right-click further inside the part.

#### If it does not work

- **Nothing happens on a left click**: AI Suggest is driven by a right-click.
- **The very first use after starting Unity is slow**: the AI engine (Burst) compiles once. This is expected.
- **Small parts take a little longer**: the area around the click is automatically zoomed and re-estimated.
- **Right-clicking never changes the mask** ("The AI returned no region" / "the internal compiler (Burst) failed"): Burst failed to initialize. It cannot recover within the same session, so press the "Restart Unity" button shown with the message. If it keeps happening, delete the project's `Library\BurstCache` and `Library\Bee` folders and start Unity again (they are regenerated automatically).
- **"The mask did not change (already added)"**: the region you clicked is already in the mask.

#### Credits / License

This feature uses [MobileSAM](https://github.com/ChaoningZhang/MobileSAM) (Apache License 2.0) converted to ONNX. The model is copyrighted by its original authors. The converted models are distributed under the Apache License 2.0 in [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) (see its NOTICE file).

---

### Presets

Save and load color zones and processing settings.

#### Save and load

In the "Presets" section, choose a storage location, enter a name, and click "Save". Use "Load" in the list to load one, or "×" to delete it.

- In Project: saved inside the project's `Assets` folder. Shareable via Git.
- Shared (User): saved in the OS user folder. Available to every project on this machine.

#### Saving and loading masks

Turn "Include masks when saving" ON to store the painted masks in the preset. Turn "Apply masks when loading" OFF to ignore the masks when loading.

#### JSON export and import

Use "Export JSON" and "Import JSON" to exchange settings as files.

---

### Export

Press "Apply & Save" to save. The output is always PNG.

- Save as new file: when ON, the original texture is kept and the result goes to a separate file named by "File Name". When OFF, the original file is overwritten (a confirmation appears first; it cannot be undone, so keep a backup).
- Inherit Import Settings (default ON): the output inherits the source texture's import settings (texture type, compression, mipmaps, and so on).
- Open Folder: opens the save folder.
- Show in Project: selects the saved texture in the Project window (handy when assigning it to a material).

If saving fails, the error stays in the Export section. The details are also written to the Console.

---

### Troubleshooting

#### Faint colors or dots remain around shapes

Edges lightened by anti-aliasing or blur are being left unrecolored. Try these in order.

1. Raise Saturation Strictness to 0.7-0.9
2. Narrow the Tolerance
3. Use Connected Region (Flood Fill) to cut off separated leftovers
4. Protect what remains with the exclude mask

#### Color bleeds outside the intended area

Raise Saturation Strictness to 0.8-0.95 and narrow the Tolerance. If white, black, or gray is being pulled in, raise Saturation Guard. Adjusting Edge Softness within 0.0-0.5 can also help.

#### Fine noise remains at boundaries

Lower Saturation Strictness to 0.1-0.4 (with a bit more bleed). Along with that, try AA Edge Cleanup at 3-5, Edge Feather at 0.5-1.5, or Edge Softness at 0.3-0.7.

#### Boundaries look jagged or hard

Add Edge Feather from 0.5-1.5. If that is not enough, raise Edge Softness to 0.3-0.7 and lower Saturation Strictness slightly to 0.3-0.45.

#### The result looks flat (solid fill)

Lower Output Saturation to 0.7-0.9. Raise Pattern Preserve if you want to keep the pattern.

#### The whole texture changes

Lower the Tolerance to 0.05-0.15 and re-sample a more specific color. If separate areas still change, narrow it down with Connected Region (Flood Fill).

#### Cannot change to black

Black has almost no brightness information, so Pattern Preserve has little to work with. Set Pattern Preserve to 0-0.3 and Edge Softness to 0. Protect anything you want to keep with the exclude mask first.

---

### FAQ

**Q: Does it support PSD layer structures?**

No. The tool works on flattened textures. If you have a PSD, editing it directly is more reliable.

**Q: Is Undo supported?**

Mask painting can be undone with Ctrl+Z. Applying the recolor to a texture cannot be undone, so back up the original before overwriting.

**Q: Can I use it across multiple projects?**

Yes. Just import the installer, or add it via VCC / ALCOM, in each project. Choosing "Shared (User)" as the preset location lets you share settings between projects.

**Q: What file formats are supported?**

Input is normally PNG or JPG, and output is always PNG. TGA, EXR, and PSD are written out from the pixels Unity imported (import downscaling and compression apply, so the result may not match the original file's resolution or quality; the window tells you when this happens).

**Q: Does it work with large textures?**

It is tested up to 4096×4096. Processing happens in memory, so memory usage rises temporarily. Larger textures are untested.

**Q: Whose texture is shown in the manual images?**

The screenshots and demo images use the texture of "[HAOLAN](https://booth.pm/ja/items/3818504)" by かなﾘぁさんち (some recolored), shown under the creator's terms.
