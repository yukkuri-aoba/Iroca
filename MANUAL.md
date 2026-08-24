# いろか ユーザーマニュアル

*[日本語](#日本語) | [English](#english)*

---

## 日本語

### 目次

- [インストール](#インストール)
- [基本的な使い方](#基本的な使い方)
- [カラーゾーンの設定](#カラーゾーンの設定)
- [加工設定](#加工設定)
- [編集モード（通常・上級）](#編集モード通常上級)
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

- Unity 2022.3 以降
- 対象テクスチャは Read/Write Enabled が有効になっている必要があります

無効なテクスチャを選んだときは、ウィンドウに出る警告のボタンから有効化できます。

#### 手順

1. [GitHub Releases](https://github.com/yukkuri-aoba/Iroca/releases) から最新の zip をダウンロードして展開します。

2. 中の `.unitypackage` を Unity Editor のプロジェクトウィンドウへドラッグ＆ドロップします。

   [スクリーンショット: エクスプローラと Unity Editor]

3. ダイアログで「Import」をクリックします。

   [スクリーンショット: Import ダイアログ]

4. 読み込みが終わると `Assets/Iroca` フォルダができます。

5. メニューの `Tools > いろか` を選ぶとウィンドウが開きます。

   [スクリーンショット: Tools メニュー]

#### Read/Write Enabled の有効化

Read/Write Enabled が無効なテクスチャを選ぶと、ウィンドウに警告と「Read/Write を自動で有効にする」ボタンが出ます。ボタンを押すとインポート設定が書き換わり、有効になります。この操作は Undo できないため、確認ダイアログが出ます。

[スクリーンショット: Read/Write 警告と有効化ボタン]

---

### 基本的な使い方

ウィンドウは ① 元テクスチャ → ② カラーゾーン → ③ プレビュー → ④ エクスポート の流れで上から並んでいます。番号どおりに進めれば一通り色替えできます。

#### ステップ 1: 元テクスチャを選ぶ

「① 元テクスチャ」の「テクスチャ」欄に、色を変えたいテクスチャをドラッグするか、欄をクリックして選びます。選ぶとプレビューに表示されます。

[スクリーンショット: テクスチャ選択後のウィンドウ全体]

#### ステップ 2: カラーゾーンを追加する

「+ ゾーン追加」を押すと、色替え 1 つぶんの「カラーゾーン」ができます。ゾーン名は自由に変えられます（処理には影響しません）。

#### ステップ 3: 変える色を指定する

色はスポイトで指定します。

1. ゾーンの「サンプルカラー」欄をクリックします。
2. 開いたカラーピッカーのスポイトアイコンで、プレビュー上の変えたい色をクリックします。
3. すぐ下の「自動調整」を押すと、テクスチャを解析して許容範囲などを自動で合わせてくれます。スポイトの直後に押すのが一番効きます。
4. 思ったより範囲が広い・狭いときは「許容範囲」スライダーで微調整します。

色替えしたい部分の中で、いちばん鮮やかな色を選ぶとうまくいきやすいです。

#### ステップ 4: 変更後の色を決める

ゾーンの「変更先カラー」をクリックして、変えたい色を選びます。プレビューにすぐ反映されます。

#### ステップ 5: 仕上がりを調整する（必要なら）

| 設定 | 内容 |
|---|---|
| 模様保持 | 元の柄をどれだけ残すか（0 = ベタ塗り、1 = 柄を残す。既定 1.0） |
| 出力彩度 | 出力の鮮やかさ。純色がベタ塗りに見えるとき 0.7〜0.9 に下げると陰影が戻る（既定 1.0） |
| 連続領域モード | つながった塊だけに変換を絞り、離れた同色や背景への色移りを防ぐ（既定 ON） |

エッジや彩度のさらに細かい調整は「[カラーゾーンの設定](#カラーゾーンの設定)」を参照してください。

#### ステップ 6: 保存する

仕上がりが良ければ「④ エクスポート」で保存します。手順は「[エクスポート](#エクスポート)」を参照してください。

---

### カラーゾーンの設定

ゾーンは「色を指定する基本の項目」と「通常モードで出る詳細項目」に分かれます。複数ゾーンを並べたときの優先度は、リストの並び順で決まります。

#### 基本の項目（常に表示）

**サンプルカラー**

色替え対象を選ぶ基準色です。欄をクリックして開くカラーピッカーのスポイトで、プレビューや画面上の任意のピクセルから色を取れます。サンプルカラーに近い色のピクセルが、自動的に対象として検出されます。

**自動調整**

サンプルカラーと変更先カラーをもとにテクスチャを解析し、許容範囲・彩度制限などをまとめて決めます。スポイトで色を取った直後に押すと最も効果的です。元テクスチャが未設定のとき、テクスチャの Read/Write が無効のとき、サンプルカラーが未指定（白のまま）のときは押せません。すでに手動で変えたパラメータがあると、上書き確認のダイアログが出ます。

**許容範囲**

色の一致をどこまで許すかです（0.0〜1.0）。だいたい 0.15〜0.40 で調整します。

| 値 | 効果 |
|---|---|
| 低い | 厳密に判定する（選択が狭い） |
| 高い | 広く判定する（選択が広い、ノイズも増える） |

**連続領域モード（Flood Fill）**

色が一致した領域のうち、確信度の高い芯を含む「つながった塊」だけに変換を絞り込みます。離れた場所にある同じ色のパーツや、背景へのにじみを自動で取り除きます。既定は自動（シード不要）です。

塊が複数あって特定の 1 つだけ残したいときは、プレビュー上で Shift+クリックしてシードを置きます。「自動へ」を押すとシードを解除して自動に戻ります。

**変更先カラー**

変えたあとの色です。対象のピクセルがこの色になります。

**模様保持**

変更後に元の明度をどれだけ残すかです。

- 0 に近い: 変更先の明度に合わせます（ベタ塗り風）
- 0.5: 元の明度を半分ほど残します
- 1 に近い: 元の柄をほぼそのまま残します（既定 1.0）

**出力彩度**

再着色後の鮮やかさです（既定 1.0）。彩度 100% の純色（純赤など）は明暗のグラデーションが潰れてベタ塗りに見えがちです。0.7〜0.9 あたりに下げると、色相は保ったまま陰影が戻ります。

#### 詳細の項目（通常モードで表示）

通常モードでは、上の基本項目に加えて次の調整が出ます。値を触りすぎたときは、各ゾーン末尾の「詳細を既定値に戻す」で詳細だけ初期化できます（色・許容範囲・名前は残ります）。

**サンプル自動補正（再着色）**（既定 ON）

スポイトした位置の明るさに関わらず、パーツの明るい面が変更先の色に合うよう、再着色の基準を自動補正します。影をスポイトしても出力が過度に明るく・ベタ塗りになるのを防ぎます。クリックした画素そのものを厳密に変更先の色へ当てたいとき、または意図的に明るく塗りたいときは OFF にします。選択範囲は変わらず、色の写り方だけが変わります。

**エッジ柔らかさ**（既定 0）

選択エッジの硬さです。0 で硬いエッジ、上げるとアンチエイリアス境界をなめらかに拾います。ぼかしのあるテクスチャで上げると効きます。

**彩度制限（影の厳しさ）**（既定 0.50）

選んだ色の薄い影や AO（暗い陰り）を、どこまで仲間として拾うかの厳しさです。上げるとはみ出しが減りますが、境界に色のドットが残りやすくなります。下げるとドットは減りますが、まわりへはみ出しやすくなります。

**彩度ガード（無彩色よけ）**（既定 0）

鮮やかな色を選んだとき、白・黒・灰色など色味のない部分が結果に混ざるのを防ぐ安全装置です。許容範囲を大きく上げて色の芯まで拾うときに、無関係な黒や白の巻き込みを抑えます。選んだ色がもともと灰色寄りなら自動で無効になります。彩度制限が「薄い影の拾い方」を調整するのに対し、こちらは「無彩色そのものの除外」です。

**ハイライト補助**（既定 ON）

高明度・低彩度のハイライト（鏡面反射や光沢部分）も補助的にマッチします。光沢素材の変換漏れを防ぎます。ON のときは、すぐ下に「ハイライト帯の拡張」（既定 ON）が出ます。これは本体につながった描き込みハイライトへ変換範囲を広げ、許容範囲を上げずに薄いハイライトの取りこぼしを防ぎます。

**ハイライト白寄せ合成**（既定 OFF）

明部を白方向へ寄せて、鏡面ハイライトの白い反射を表現します。光沢・プラスチックなど、ハイライトが白く飛ぶ素材で効果的です。ON にすると、すぐ下に「ハイライト自動補正」（既定 OFF）が出ます。パーツの地色を自動で見つけて白寄せをドーム全体に効かせ、立体感を出します。髪など細い房の多いテクスチャでは広がりすぎることがあるので、その場合は OFF にします。

**シャドウ・ハイライト詳細設定**

暗部やグレーの扱いを細かく決めます。

| 設定 | 説明 | 既定 |
|---|---|---|
| シャドウ彩度低下 | この明度より暗いピクセルの彩度を落とす閾値。下げると暗い色も鮮やかに染まる | 0.35 |
| シャドウ巻き込み最低彩度 | 暗いピクセルを影として巻き込むのに必要な最低彩度。純粋なグレー・黒の色付けを防ぐ | 0.05 |
| 自動しきい値(無彩色判定) | サンプルの彩度がこの値以下なら、色相を無視して無彩色（黒・グレー）として抽出する | 0.05 |

#### ゾーンの優先度（並び順）

複数のゾーンが重なる部分は、上にあるゾーンだけが適用され、下のゾーンに対してはマスクのように働きます。優先度はリストの並び順で決まります。各ゾーン左の `☰` ハンドルを掴んでドラッグすると、並べ替え（＝優先度の変更）ができます。

---

### 加工設定

エッジやノイズの処理をまとめて調整します。変更はプレビューに自動で反映され、最後に「適用して保存」で確定します。

#### エッジぼかし

選択境界をぼかして、エッジの色を自然になじませます（0〜5）。

| 値 | 効果 |
|---|---|
| 0 | オフ（エッジがシャープ） |
| 0.5〜1.5 | 標準的なぼかし |
| 2.0 以上 | 強いぼかし（なめらかなテクスチャ向け） |

#### AA境界クリーンアップ

アンチエイリアス境界に残るドットを回収するパス数です。

| 値 | 効果 |
|---|---|
| 0 | オフ |
| 1〜2 | 弱い |
| 3 | 標準（推奨・既定） |
| 4〜5 | 強い |

#### 境界クリーンアップ（α分解）

アンチエイリアス境界で α 分解と再合成を行い、境界に出る薄汚れた中間色（ハロー）を防ぎます。既定は ON で、通常はそのままで構いません。

---

### 編集モード（通常・上級）

カラーゾーンの先頭にある「編集モード」トグルで、表示する項目の細かさを切り替えます。既定は「通常」です。普段のテクスチャは通常モードのままで十分で、思いどおりにならないときだけ「上級」にします。

上級モードにすると、ゾーンと加工設定の両方に、アルゴリズム内部のパラメータが追加で表示されます。

#### ゾーンに追加される項目

| 設定 | 説明 | 既定 |
|---|---|---|
| 明度重み | 距離計算での明度の重み。高いほど明度差に敏感（別素材を分離しやすい）。低いほど同じ素材の影・ハイライトを吸収する | 1.0 |
| 彩度距離重み | 彩度距離の重み。高いほど彩度差に敏感 | 0.15 |
| 彩度ランプスケール | 動的彩度ランプのスケール。大きいほど彩度閾値付近でなだらかにフェードする | 0.10 |

#### 加工設定に追加される項目

| 設定 | 説明 | 既定 |
|---|---|---|
| 穴埋めパス数 | アンチエイリアス端の孤立ドットを埋めるパス数 | 5 |
| 穴埋め最小隣接数 | 穴を埋めるのに必要な一致隣接ピクセル数。低いほど積極的に埋める | 4 |
| 境界復元 彩度最小 | 境界復元時の彩度最小閾値 | 0.02 |
| 境界復元 彩度ランプ | 境界復元時の彩度ランプ幅 | 0.08 |
| α分解 近傍半径 | 境界クリーンアップ（α分解）で背景色を推定する近傍の半径 | 4 |

うまくいく組み合わせが見つかったら、プリセットとして保存しておくと便利です。これらの値もプリセットに含まれます。

---

### プレビュー機能

#### ズームとパン

- Ctrl + スクロール: ズームイン・アウト
- ドラッグ: ビューを移動（ズーム 1 倍超のときのみ）

高解像度プレビューはピクセル単位まで拡大できます（上限はテクスチャ解像度に応じて自動調整）。

#### 表示モード

- 通常: 現在の処理結果を表示します（どちらのトグルも OFF）
- 前後比較: 変更前後を左右に並べて表示します（高ズーム時は使えません）
- 差分表示: 変わったピクセルだけを強調表示します

#### 自動更新

設定を変えると、短い遅延（約 0.2 秒）のあと自動で更新されます。

---

### マスク（除外・含める）

プレビュー上にブラシで塗って、色替えの範囲を手動で調整します。マスクは 2 種類あります。

- **除外マスク**: 塗った領域を色替えから外します（色が合っていても変更されません）。全ゾーンに効く共通マスクと、特定ゾーンだけに効くゾーン別マスクを使い分けられます。
- **含めるマスク**: 塗った領域を必ず色替えに含めます（色が合わなくても変更されます）。色の判定では拾いきれなかった部分 — たとえば強い光沢や、離れた場所にある同じパーツの取りこぼし — を追加するのに使います。ゾーン別のみです（どのゾーンの色にするかを決める必要があるため、共通マスクには「含める」はありません）。

両方に塗られた画素は**除外が優先**されます。

[スクリーンショット: 除外マスクを描いた状態のプレビュー]

#### 使い方

マスクの操作は **「Iroca マスク編集」ウィンドウ** にまとまっています。プレビューの隣に置いたまま、対象・種類・ツールをすべてそこで切り替えられます。

1. マスク欄の「マスクを編集...」を押すと、マスク編集ウィンドウが開いてペイントモードになります。
2. ウィンドウ上部の「編集対象」プルダウンで、共通マスクか各ゾーンを選びます。
3. 「マスクの種類」で除外／含めるを選びます（含めるはゾーン選択時のみ）。
4. ツールで「塗る」「消す」「AI 提案」を切り替えます（AI 提案は Sentis 導入済みのときのみ表示。モデル未取得のうちは押せません。同じボタンをもう一度押すとそのモードを抜けます）。
5. 「塗る」「消す」ではプレビュー上をドラッグしてマスクを塗り／消しします（赤い重ね表示が共通の除外、色付きがゾーン別の除外、緑が含める）。「AI 提案」ではパーツを右クリックすると AI が推定した領域が追加されます。

プレビューの重ね表示には、いま編集している 1 枚だけでなく**塗ってあるマスクすべて**が表示されます（編集対象は明るく、それ以外は薄く）。薄く表示されているマスクも色替えにはそのまま効いています。

ウィンドウを閉じるとマスク編集モード（ブラシ・AI 提案とも）が解除されます。メインウィンドウのマスク欄には「編集中のマスク: ◯◯ / ◯◯」の表示が残るので、閉じていても対象は確認できます。

#### 編集対象

- 共通マスク（全ゾーン）: すべてのゾーンで除外される領域
- ゾーン別マスク: 選んだゾーンだけの除外／含める領域

処理時は共通マスクとゾーン別マスクが合わせて適用されます。

#### 含めるマスクの色の写り方

含めるマスクで追加した領域は、そのゾーンの色マッチした部分から推定した「素材の基準」を使って、同素材として色替えされます。追加した領域の色や明るさは、ゾーン全体の色の写り方には影響しません（別素材を含めてもゾーンの他の部分の仕上がりは変わりません）。

#### ブラシ設定

ブラシサイズは 1〜64 で指定します。

#### 描画モード

- 塗る: ペイントモードに入り、選んだ種類のマスクを塗ります。再度押すと抜けます。
- 消す: 消しゴムモードに入り、選んだ種類のマスクを消します。再度押すと抜けます。

#### 取り消しとリセット

- Ctrl+Z: 直前のストロークを取り消します（Unity 標準の Undo に対応。マスク編集ウィンドウの「直前の操作を元に戻す」ボタンでも同じ操作ができます）。
- このマスクをクリア: いま選んでいる対象・種類のマスク 1 枚だけを消します（ボタンに対象名が出ます）。ほかのマスクは残ります。

---

### AI マスク提案（実験的機能）

プレビュー上のパーツを**右クリック**すると、AI（MobileSAM）がそのパーツの領域を推定し、その場で編集対象のマスクへ追加します。追加先はマスク編集ウィンドウの「編集対象」と「マスクの種類」に従います（除外＝色替えしない範囲／含める＝必ず色替えする範囲）。手描きでパーツを囲む手間を大幅に減らせます。左ドラッグはこれまでどおりプレビューの移動（パン）なので、AI 提案中でも見たい場所へ寄せながら選べます。

#### 必要なもの（任意インストール）

スクリプトを入れただけの素の状態でも、マスク欄に「AI マスク提案」の有効化導線が表示されます。以下の 2 つを Unity 上のボタンでセットアップすると有効になります（セットアップしなければ従来どおりの動作で、ストレージも消費しません）。セットアップが済むと導線は消え、以後は マスク編集ウィンドウのツール「AI 提案」から使います。

1. **Unity Sentis パッケージ**: マスク欄の「AI マスク提案」に出る **「AI 機能を有効化（Sentis を導入）」** ボタンを押すと、Package Manager 経由で自動導入されます（導入後 Unity が自動で再コンパイルします）。手動で入れる場合は Package Manager → 左上の「+」→「Add package by name...」→ `com.unity.sentis`（バージョン `2.1.3`）。
2. **AI モデル（2 ファイル・合計約 45MB）**: マスク欄の「AI マスク提案」→「モデルをダウンロード」を押すと自動で配置されます（sha256 検証つき）。モデルは **プロジェクトごとではなくユーザー共通のフォルダに 1 か所だけ** 保存されるため（Windows は `%LOCALAPPDATA%\Iroca\Models`）、別プロジェクトでも再ダウンロードは不要です。手動の場合はモデル配布リポジトリ [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) から 2 つの `.onnx` をダウンロードし、「モデルフォルダを開く」で開いたフォルダへ置いてください。

#### 使い方

1. マスク欄の「マスクを編集...」でマスク編集ウィンドウを開き、ツールの「AI 提案」を押します（ブラシペイントとは排他で、押すとブラシは解除されます）。
2. プレビュー上で、選びたいパーツの内側を **右クリック**（mac は Control+クリック）します。画像の解析は AI 提案に入った時点で先に始まるので、進捗表示が消えてから押すと待たずに済みます。小さいパーツ（模様・ワンポイントなど）を選んだ場合は、その周辺を自動で拡大して推定し直すため、待ち時間が少し延びることがあります。
   - **左ドラッグはプレビューの移動（パン）のまま**です。推定を待っている間もスクロール・拡大縮小・移動ができるので、次に選ぶパーツへ寄せながら進められます。
3. 推定された領域は **その場で編集対象のマスクへ追加され**（既定は除外＝色替えしない範囲）、マスクの色で表示されます。追加先のゾーンと種類は、同じウィンドウの上にある「編集対象」「マスクの種類」でいつでも切り替えられます。確定ボタンはありません。パーツが複数の島に分かれている場合は、島を順に右クリックすればそれぞれが足されていきます。
4. 外した提案が足されてしまったら **Ctrl+Z** で 1 つずつ戻せます（手描きブラシと同じ操作です）。
5. 足した領域は通常のマスクなので、ブラシでの微修正・ファイル保存はいつも通りです。仕上げに全体をブラシで整えられます。

> 右クリックした瞬間にマスクへ反映されるため、間違いは Ctrl+Z で戻す運用です。足される先は、同じウィンドウの「編集対象」と「マスクの種類」がそのまま示しています。

#### 苦手なケース

- 白背景に白いパーツなど、見た目の境界が無い場合は正しく提案できません。手描きマスクを使ってください。
- 数十個の小さなピースに分かれたパーツはクリック回数が多くなります。大きな塊だけ AI で選び、残りをブラシで足すのが早道です。
- 「直前のクリックが背景まで広がった可能性があります」と警告が出たら、Ctrl+Z で戻し、粒度を「細かい」にするかパーツのより内側を右クリックし直すのがおすすめです。

#### うまく動かないとき

- **左クリックしても何も起きない**: AI 提案の指定は **右クリック**（mac は Control+クリック）です。左クリック／左ドラッグはプレビューの移動に割り当てています。
- **Unity を起動して最初の 1 回だけ時間がかかる**: AI エンジン（Burst）のコンパイルが入るためで、異常ではありません。「AI 提案を開始」を押した時点で解析と暖機を先に済ませるので、進捗表示が消えてから右クリックすれば待ち時間はほぼありません。2 回目以降はすぐ返ります。
- **右クリックしてもマスクが何も変わらない**: 「AI が領域を返しませんでした」または「内部コンパイル（Burst）が失敗しています」と表示された場合、Unity 起動時に Burst の初期化に失敗しています。この状態は同じセッションでは直らないので、**Unity を再起動**してください（欄に出る「Unity を再起動」ボタンでプロジェクトを開き直せます）。再起動しても再発するときは、プロジェクトの `Library\BurstCache` と `Library\Bee` フォルダを削除してから起動し直すと直ることがあります（`Library` 配下は自動で再生成されるため削除して問題ありません）。
- **「マスクが変わりませんでした（すでに追加済み）」と出る**: 異常ではありません。クリックした領域はすでにマスクへ入っています。

#### クレジット / ライセンス

この機能は [MobileSAM](https://github.com/ChaoningZhang/MobileSAM)（Apache License 2.0）を ONNX 形式に変換して使用しています。モデルの著作権は原作者に帰属します。変換済みモデルは [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) リポジトリで Apache License 2.0 のもと配布しています。詳細は同リポジトリの NOTICE ファイルを参照してください。

---

### プリセット

カラーゾーンと加工設定をプリセットとして保存・読み込みできます。

#### 保存と読み込み

1. 「プリセット」セクションを開きます。
2. 保存先を選びます。
3. プリセット名を入れて「保存」を押します。
4. 一覧の「読込」で読み込み、「×」で削除します。

保存先は 2 種類あります。

- プロジェクト内: プロジェクトの `Assets` フォルダ内に保存します。Git などでチームと共有できます。
- ユーザー共通: OS のユーザーフォルダに保存します。この端末のすべてのプロジェクトで共有されます。

#### マスクの保存・読み込み

- マスクも保存する: ON にすると、いま塗っている共通・ゾーン別マスクもプリセットに同梱します。
- マスクも読み込む: ON にすると、プリセット内のマスクを読み込み時に復元します。OFF ならマスクは無視して他のパラメータだけ読みます。

#### JSON の書き出し・読み込み

「JSONエクスポート」「JSONインポート」で、設定を外部ファイルとしてやり取りできます。

---

### エクスポート

「新規ファイルとして保存」で保存方法を選んでから、「適用して保存」を押します。

#### 保存方法

新規ファイルとして保存を ON にすると、元のテクスチャを残したまま別ファイルに保存します。「ファイル名」を指定できます。

OFF にすると、元のテクスチャファイルを上書きします。上書き前に確認ダイアログが出ますが、バックアップを取っておくと安心です。

#### その他のオプション

- インポート設定を引き継ぐ: ON（既定）にすると、出力テクスチャが元テクスチャのインポート設定（テクスチャタイプ・圧縮・ミップマップなど）を引き継ぎます。OFF なら Unity の既定設定を使います。
- フォルダを開く: 保存先のフォルダをファイルマネージャーで開きます。

---

### トラブルシューティング

#### 図形の周りに薄い色やドットが残る

アンチエイリアスやぼかしのかかった周辺は薄い色になり、変換から漏れて残ることがあります。次の順に試してください。

1. 彩度制限（影の厳しさ）を 0.7〜0.9 あたりまで上げる。薄い色を選択から外せます。
2. 許容範囲を狭める。
3. 連続領域モードを使い、離れた残りを自動で切り離す。
4. それでも残る部分は除外マスクで保護する。

#### 色がはみ出す

彩度制限が低すぎるか、許容範囲が広すぎます。まず彩度制限（影の厳しさ）を 0.8〜0.95 あたりまで上げ、許容範囲を狭めます。鮮やかな色を選んでいて白・黒・灰を巻き込んでいる場合は、彩度ガード（無彩色よけ）を上げると効きます。エッジ柔らかさを 0.0〜0.5 で調整するのも有効です。

#### 境界に細かいノイズが残る

彩度制限が高すぎるか、境界処理が足りていません。彩度制限を 0.1〜0.4 あたりまで下げると境界のピクセルを拾いやすくなります（そのぶんはみ出しやすくはなります）。あわせて AA境界クリーンアップを 3〜5 に上げる、エッジぼかしを 0.5〜1.5 から入れる、エッジ柔らかさを 0.3〜0.7 にする、のいずれかを試します。

#### 境界がギザギザしている・硬い

エッジ処理が足りていません。エッジぼかしを 0.5〜1.5 から入れるのが基本です。あわせてエッジ柔らかさを 0.3〜0.7 に上げ、必要なら彩度制限を 0.3〜0.45 まで少し下げると境界を拾いやすくなります。

#### 仕上がりがベタ塗りになる

彩度 100% の純色は明暗が潰れてベタ塗りに見えます。出力彩度を 0.7〜0.9 に下げると、色相は保ったまま陰影が戻ります。模様を残したい場合は模様保持を上げます。

#### テクスチャ全体が変わってしまう

許容範囲が広すぎます。許容範囲を 0.05〜0.15 あたりまで大きく下げ、サンプルカラーをより限定的な色で取り直します。離れた領域まで変わってしまう場合は、連続領域モードで塊を絞り込みます。

#### 黒い色に変更できない

黒は明度の情報がほとんどないため、模様保持が効きにくくなります。模様保持を 0〜0.3 に下げ、エッジ柔らかさを 0 にします。保護したい部分は先に除外マスクで囲っておきます。

---

### よくある質問

**Q: 複数のカラーゾーンを組み合わせられますか？**

はい。「+ ゾーン追加」で複数のゾーンを追加できます。重なった部分の優先度は、リストの並び順（`☰` ハンドルのドラッグ）で決まります。

**Q: 連続領域モードは何をするものですか？**

色が一致した領域のうち、確信度の高い芯を含むつながった塊だけに変換を絞ります。離れた場所の同色パーツや背景への色移りを自動で防ぎます。塊が複数あるときは、プレビューを Shift+クリックして残したい塊を 1 つ指定できます。

**Q: 自動調整は何をしてくれますか？**

サンプルカラーと変更先カラーからテクスチャを解析し、許容範囲や彩度制限などを自動で設定します。スポイトで色を取った直後に押すのが一番効きます。

**Q: PSD のレイヤー構造をサポートしていますか？**

いいえ。本ツールは PNG などの統合済みテクスチャを対象としています。PSD があるなら、そちらを直接編集する方が確実です。

**Q: Undo は使えますか？**

除外マスクの描画は Ctrl+Z で取り消せます（Unity 標準の Undo に統合）。テクスチャへの色適用そのものは元に戻せないので、上書き保存の前にバックアップをお勧めします。

**Q: 複数のプロジェクトで使えますか？**

はい。`.unitypackage` を各プロジェクトに読み込むだけです。プリセットの保存先で「ユーザー共通」を選ぶと、プロジェクト間で設定を共有できます。

**Q: 対応しているファイル形式は？**

入力は PNG / JPG、出力は常に PNG です。TGA・EXR・PSD には対応していません。テクスチャは Unity 上で Read/Write Enabled を有効にしてください。

**Q: 大きなテクスチャでも使えますか？**

使えます。処理はメモリ上で行うため、大きなテクスチャでは一時的にメモリ使用量が増えます。

---

## English

> The Japanese text above is authoritative. The English UI labels inside the tool are machine-translated with AI assistance, so the wording here is kept close to those labels.

### Table of Contents

- [Installation](#installation)
- [Basic Usage](#basic-usage)
- [Color Zone Settings](#color-zone-settings)
- [Processing Settings](#processing-settings)
- [Edit Mode (Normal / Advanced)](#edit-mode-normal--advanced)
- [Preview](#preview)
- [Exclusion Mask](#exclusion-mask)
- [AI Mask Suggestion (Experimental)](#ai-mask-suggestion-experimental)
- [Presets](#presets)
- [Export](#export)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)

---

### Installation

#### Prerequisites

- Unity 2022.3 or later
- Target textures must have Read/Write Enabled turned on

If you pick a texture that does not, you can enable it from the warning button shown in the window.

#### Steps

1. Download the latest zip from [GitHub Releases](https://github.com/yukkuri-aoba/Iroca/releases) and extract it.
2. Drag the `.unitypackage` into the Unity Editor project window.
3. Click "Import" in the dialog.
4. After importing, an `Assets/Iroca` folder is created.
5. Open the window from `Tools > いろか`.

#### Enabling Read/Write

If a texture does not have Read/Write Enabled, the window shows a warning and an "Enable Read/Write automatically" button. Clicking it changes the import settings to turn it on. This cannot be undone, so a confirmation dialog appears first.

---

### Basic Usage

The window is laid out top to bottom: 1. Source Texture, 2. Color Zones, 3. Preview, 4. Export. Follow the numbers and you can recolor a texture end to end.

#### Step 1: Pick a source texture

Drag a texture onto the "Texture" field under "Source Texture", or click the field to choose one. It appears in the preview.

#### Step 2: Add a color zone

Click "+ Add Zone" to create one recoloring zone. You can rename it freely; the name does not affect processing.

#### Step 3: Choose the color to change

Colors are picked with the eyedropper.

1. Click the zone's "Sample Color" field.
2. In the color picker, use the eyedropper to click the color you want on the preview.
3. Press "Auto-tune" just below it. It analyzes the texture and sets the tolerance and related values for you. It works best right after sampling.
4. If the selection is too wide or too narrow, fine-tune it with the "Tolerance" slider.

Pick the most vivid color within the area you want to recolor for the best results.

#### Step 4: Set the target color

Click the zone's "Target Color" and choose the new color. The preview updates right away.

#### Step 5: Adjust the result (optional)

| Setting | What it does |
|---|---|
| Pattern Preserve | How much of the original pattern to keep (0 = flat recolor, 1 = keep pattern; default 1.0) |
| Output Saturation | Output vividness. Lower it to 0.7-0.9 when a pure color looks flat (default 1.0) |
| Connected Region (Flood Fill) | Restricts recoloring to one connected region and avoids bleed into separate parts or the background (default ON) |

For finer edge and saturation controls, see [Color Zone Settings](#color-zone-settings).

#### Step 6: Save

When you are happy with the result, save it under "Export". See [Export](#export) for details.

---

### Color Zone Settings

A zone splits into the core color controls and the detail controls shown in Normal mode. When several zones overlap, priority is decided by their order in the list.

#### Core controls (always shown)

**Sample Color**

The reference color used to find the target. Click the field to open the color picker, then use the eyedropper to sample from the preview or any on-screen pixel. Pixels close to the sample color are detected automatically.

**Auto-tune**

Analyzes the texture from the sample and target colors and sets the tolerance, saturation strictness, and related values together. It is most effective right after you sample a color. It is disabled when the source texture is not set, when the texture's Read/Write is off, or when the sample color is still unset (white). If you have already changed some parameters by hand, a confirmation dialog asks before overwriting them.

**Tolerance**

How far a color can be from the sample and still match (0.0-1.0). A range of about 0.15-0.40 works for most cases.

| Value | Effect |
|---|---|
| Low | Stricter matching (narrower selection) |
| High | Looser matching (wider selection, more noise) |

**Connected Region (Flood Fill)**

Restricts recoloring to the connected region that contains a high-confidence core, and removes bleed into same-color parts elsewhere or into the background. It runs automatically by default, with no seed needed.

When several regions exist and you want to keep only one, Shift+click the preview to set a seed. Press "Auto" to clear the seed and return to automatic behavior.

**Target Color**

The color applied after recoloring. Matched pixels become this color.

**Pattern Preserve**

How much of the original brightness to keep after recoloring.

- Near 0: matches the target brightness (flat recolor)
- 0.5: keeps about half of the original brightness
- Near 1: keeps the original pattern almost as is (default 1.0)

**Output Saturation**

The vividness after recoloring (default 1.0). Fully saturated colors such as pure red tend to flatten the brightness gradient and look solid-filled. Lowering it to around 0.7-0.9 keeps the hue while bringing the shading back.

#### Detail controls (shown in Normal mode)

In Normal mode, these appear in addition to the core controls. If you over-tweak them, the "Reset details to default" button at the end of each zone restores only the detail values; the color, tolerance, and name are kept.

**Auto Sample Anchor** (default ON)

Regardless of how bright the sampled spot was, this adjusts the recoloring reference so the lit side of the part matches the target color. It keeps the output from coming out too bright or flat when you sample in a shadow. Turn it OFF when you want the clicked pixel mapped exactly to the target, or when you intentionally want a brighter result. It changes how colors map, not which pixels are selected.

**Edge Softness** (default 0)

How hard the selection edge is. 0 is a hard edge; raising it picks up anti-aliased boundaries more smoothly. Useful for blurred textures.

**Saturation Strictness** (default 0.50)

How strictly the faint shadows and AO of the picked color are kept in the selection. Raising it reduces bleed but can leave color dots at edges. Lowering it removes dots but bleeds into surroundings more.

**Saturation Guard** (default 0)

A safety guard that keeps colorless areas (white, black, gray) out of the result when you pick a vivid color. Use it when you raise the tolerance to recover the core but want to avoid pulling in unrelated black or white. It disables itself automatically if the picked color is already grayish. Where Saturation Strictness tunes how shadows are handled, this one excludes achromatic pixels outright.

**Highlight Recovery** (default ON)

Also matches high-brightness, low-saturation highlights such as specular reflections and gloss, so glossy materials are not left unrecolored. When it is ON, "Highlight Band Expansion" (default ON) appears below it. That expands recoloring into drawn-in highlights connected to the body, preventing thin highlights from being missed without raising the tolerance.

**Highlight White Blend** (default OFF)

Pushes bright areas toward white to reproduce the white reflection of specular highlights. Useful for glossy or plastic materials where highlights blow out to white. When it is ON, "Auto Highlight Sample" (default OFF) appears below it. That finds the part's base tone automatically so the white blend covers the whole highlight dome and gives a sense of depth. On hair-like textures with many thin strands it can spread too much, so turn it OFF there.

**Shadow / Highlight Details**

Fine control over dark and gray pixels.

| Setting | Description | Default |
|---|---|---|
| Shadow Desaturation | Brightness threshold below which dark pixels lose saturation. Lower it to let dark colors recolor more vividly | 0.35 |
| Shadow Forgiveness Sat Min | Minimum saturation to include a dark pixel as shadow. Prevents pure gray or black from being colorized | 0.05 |
| Auto Grayscale Threshold | If the sample's saturation is at or below this, hue is ignored and the area is treated as grayscale (black/gray) | 0.05 |

#### Zone priority (list order)

Where zones overlap, only the upper zone is applied, acting like a mask for the ones below. Priority follows the list order. Grab the `☰` handle on the left of a zone and drag to reorder, which changes its priority.

---

### Processing Settings

These adjust edge and noise handling for all zones. Changes show in the preview automatically and are committed when you press "Apply & Save".

#### Edge Feather

Blurs the selection boundary so edge colors blend in naturally (0-5).

| Value | Effect |
|---|---|
| 0 | Off (sharp edges) |
| 0.5-1.5 | Standard blur |
| 2.0+ | Strong blur (for smooth textures) |

#### AA Edge Cleanup

Number of passes that recover dots left at anti-aliased boundaries.

| Value | Effect |
|---|---|
| 0 | Off |
| 1-2 | Weak |
| 3 | Standard (recommended, default) |
| 4-5 | Strong |

#### Edge Decontamination

Reconstructs anti-aliased boundary pixels through alpha decomposition and recomposition, preventing the muddy mid-color (halo) that appears at edges. It is ON by default and usually fine to leave that way.

---

### Edit Mode (Normal / Advanced)

The "Mode" toggle at the top of Color Zones switches how much detail is shown. The default is "Normal". For everyday textures, Normal is enough; switch to "Advanced" only when you cannot get the result you want.

Advanced mode adds internal algorithm parameters to both the zones and the processing settings.

#### Added to each zone

| Setting | Description | Default |
|---|---|---|
| Value Weight | Weight of brightness in the distance formula. Higher is more sensitive to brightness (separates different materials); lower absorbs shadow and highlight of the same material | 1.0 |
| Sat Distance Weight | Weight of saturation distance. Higher is more sensitive to saturation differences | 0.15 |
| Sat Ramp Scale | Scale of the dynamic saturation ramp. Larger fades more gradually near the threshold | 0.10 |

#### Added to processing settings

| Setting | Description | Default |
|---|---|---|
| Hole Fill Passes | Passes that fill isolated dots at anti-aliased edges | 5 |
| Hole Fill Min Neighbors | Matched neighbors needed to fill a hole. Lower fills more aggressively | 4 |
| Boundary Sat Min | Minimum saturation threshold for boundary recovery | 0.02 |
| Boundary Sat Ramp | Saturation ramp width for boundary recovery | 0.08 |
| Decontamination Radius | Neighborhood radius used to estimate background color for Edge Decontamination | 4 |

Once you find a combination that works, save it as a preset; these values are included.

---

### Preview

#### Zoom and pan

- Ctrl + Scroll: zoom in and out
- Drag: pan the view (only when zoom is above 1x)

The high-res preview can be magnified down to pixel level. The maximum zoom scales with the texture resolution.

#### View modes

- Normal: shows the current result (both toggles off)
- Compare: shows before and after side by side (not available at high zoom)
- Diff: highlights the pixels that changed

#### Auto-update

After you change a setting, the preview updates automatically following a short delay of about 0.2 seconds.

---

### Exclusion Mask

Paint on the preview to mark areas you do not want recolored. You can use a common mask that affects every zone, and per-zone masks that affect only one zone.

#### How to use

1. Choose the target in the "Edit Target" dropdown (common mask or a specific zone). Each zone card also has an "Edit this zone's mask" button.
2. Click "Exclude" to enter paint mode.
3. Drag on the preview to paint the mask (red overlay is the common mask, colored overlay is per-zone).
4. Painted areas are not recolored.
5. Click "Exclude" again to leave paint mode.

#### Edit target

- Common Mask (all zones): area excluded from every zone
- Per-zone mask: area excluded only from the selected zone

During processing, the common and per-zone masks are combined.

#### Brush settings

Brush size ranges from 1 to 64.

#### Brush modes

- Exclude: enters paint mode and adds to the mask. Press again to leave.
- Include: enters erase mode and removes from the mask. Press again to leave.

#### Undo and reset

- Ctrl+Z: undoes the last stroke (integrated with Unity's standard Undo; the "Undo Mask" button does the same).
- Clear Mask: removes the entire mask for the currently selected target.

---

### AI Mask Suggestion (Experimental)

Click a part on the preview and the AI (MobileSAM) estimates that part's region and adds it to the exclusion mask right away — a big time-saver over painting parts by hand.

#### Requirements (optional install)

Even with just the scripts dropped in, the "AI Mask Suggestion" panel appears in the Exclusion Mask section. Set up the two items below with in-Unity buttons to enable it (skip the setup to keep the classic behavior with no extra storage).

1. **Unity Sentis package**: Press the **"Enable AI feature (install Sentis)"** button shown under "AI Mask Suggestion" in the Exclusion Mask panel — it installs the package via the Package Manager (Unity recompiles automatically afterward). To install manually: Package Manager → "+" → "Add package by name..." → enter `com.unity.sentis` (version `2.1.3`).
2. **AI models (2 files, ~45MB total)**: Open "AI Mask Suggestion" and press "Download models" (with sha256 verification). Models are stored **once in a per-user shared folder, not per project** (`%LOCALAPPDATA%\Iroca\Models` on Windows), so other projects don't need to re-download. To install manually, download the two `.onnx` files from the model repository [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) and place them into the folder opened by "Open model folder".

#### How to use

1. Press "Start AI Suggestion" in the Exclusion Mask panel (mutually exclusive with brush painting).
2. Click inside the part you want to select on the preview. Image analysis starts as soon as you press "Start AI Suggestion", so clicking after the progress indicator disappears avoids the wait. Clicking a small part (a pattern, a small accent) may take slightly longer, because the area around the click is automatically zoomed and re-estimated.
3. The estimated region is **added to the exclusion mask (= the area that is not recolored) on the spot** and drawn in the mask color. There is no confirm button. If the part is split into multiple islands, click them one by one and each is added.
4. If a bad proposal got added, press **Ctrl+Z** to undo them one at a time (the same as with the hand brush).
5. Added regions are ordinary mask data, so brush touch-ups and file persistence work as usual. Finish up with the brush if needed.

> Because a click commits immediately, mistakes are undone with Ctrl+Z. The "Commit target: ◯◯" label shows which mask (common / zone) the region is being added to.

#### Known limitations

- Parts with no visible boundary (e.g. white pieces on a white background) cannot be proposed correctly — use hand-painted masks there.
- Parts split into dozens of tiny pieces need many clicks; select the large chunks with AI and fill the rest with the brush.
- If you see the "may have spread into the background" warning, press Ctrl+Z to undo it, then set the granularity to "Fine" or click again further inside the part.
- The first use after starting Unity takes longer because the AI engine (Burst) compiles once — this is expected.
- If clicks never change the mask ("The AI returned no region" / "the internal compiler (Burst) failed"), Burst failed to initialize in this Unity session. It cannot recover within the same session: restart Unity with the "Restart Unity" button in the panel. If it keeps happening, delete the project's `Library\BurstCache` and `Library\Bee` folders and start Unity again (everything under `Library` is regenerated automatically).

#### Credits / License

This feature uses [MobileSAM](https://github.com/ChaoningZhang/MobileSAM) (Apache License 2.0) converted to ONNX. The model is copyrighted by its original authors. The converted models are distributed under the Apache License 2.0 in the [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) repository; see its NOTICE file for details.

---

### Presets

Save and load color zones and processing settings as presets.

#### Save and load

1. Open the "Presets" section.
2. Choose a storage location.
3. Enter a preset name and click "Save".
4. Use "Load" in the list to load one, or "×" to delete it.

There are two storage locations.

- In Project: saved inside the project's `Assets` folder. Shareable via Git.
- Shared (User): saved in the OS user folder. Shared across every project on this machine.

#### Saving and loading masks

- Include masks when saving: when ON, the currently painted common and per-zone masks are saved into the preset.
- Apply masks when loading: when ON, masks stored in the preset are restored on load. When OFF, masks are ignored and only other parameters are loaded.

#### JSON export and import

Use "Export JSON" and "Import JSON" to exchange settings as external files.

---

### Export

Choose the save method with "Save as new file", then press "Apply & Save".

#### Save method

With "Save as new file" ON, the original texture is kept and the result is saved to a separate file. You can set the "File Name".

With it OFF, the original texture file is overwritten. A confirmation dialog appears first, but keeping a backup is recommended.

#### Other options

- Inherit Import Settings: when ON (default), the output inherits the source texture's import settings (texture type, compression, mipmaps, and so on). When OFF, Unity's default import settings are used.
- Open Folder: opens the save folder in the file manager.

---

### Troubleshooting

#### Faint colors or dots remain around shapes

Edges touched by anti-aliasing or blur become light-colored and can be left unrecolored. Try these in order.

1. Raise Saturation Strictness to around 0.7-0.9 to drop light colors from the selection.
2. Narrow the Tolerance.
3. Use Connected Region (Flood Fill) to cut off separated leftovers automatically.
4. Mask anything that still remains with the exclusion mask.

#### Color bleeds outside the intended area

Saturation Strictness is too low or Tolerance is too high. First raise Saturation Strictness to about 0.8-0.95 and narrow the Tolerance. If you picked a vivid color and it is pulling in white, black, or gray, raise Saturation Guard. Adjusting Edge Softness in the 0.0-0.5 range can also help.

#### Fine noise remains at boundaries

Saturation Strictness is too high or boundary processing is insufficient. Lowering Saturation Strictness to around 0.1-0.4 picks up more boundary pixels (with a bit more bleed). Along with that, raise AA Edge Cleanup to 3-5, add Edge Feather from 0.5-1.5, or set Edge Softness to 0.3-0.7.

#### Boundaries look jagged or hard

Edge processing is insufficient. Adding Edge Feather from 0.5-1.5 is the basic fix. You can also raise Edge Softness to 0.3-0.7 and, if needed, lower Saturation Strictness slightly to 0.3-0.45 to pick up boundary pixels.

#### The result looks flat (solid fill)

Fully saturated colors flatten the shading and look solid. Lowering Output Saturation to 0.7-0.9 keeps the hue while bringing the shading back. Raise Pattern Preserve if you want to keep the pattern.

#### The whole texture changes

Tolerance is too high. Lower it well, to around 0.05-0.15, and re-sample a more specific color. If separate areas still change, use Connected Region (Flood Fill) to narrow it to one region.

#### Cannot change to black

Black has almost no brightness information, so Pattern Preserve has little to work with. Lower Pattern Preserve to 0-0.3 and set Edge Softness to 0. Protect anything you want to keep with the exclusion mask first.

---

### FAQ

**Q: Can I combine multiple color zones?**

Yes. Add zones with "+ Add Zone". Where they overlap, priority follows the list order (drag the `☰` handle).

**Q: What does Connected Region (Flood Fill) do?**

It limits recoloring to the connected region that contains a high-confidence core, automatically preventing color from spreading to same-color parts elsewhere or to the background. When several regions exist, Shift+click the preview to keep just the one you want.

**Q: What does Auto-tune do?**

It analyzes the texture from the sample and target colors and sets the tolerance, saturation strictness, and related values for you. It works best right after sampling a color.

**Q: Does it support PSD layer structures?**

No. The tool works on flattened textures such as PNG files. If you have a PSD, editing it directly is more reliable.

**Q: Is Undo supported?**

Exclusion mask painting can be undone with Ctrl+Z (integrated with Unity's standard Undo). Applying the recolor to a texture cannot be undone, so back up the original before overwriting.

**Q: Can I use it across multiple projects?**

Yes. Just import the `.unitypackage` into each project. Choosing "Shared (User)" as the preset location lets you share settings between projects.

**Q: What file formats are supported?**

Input is PNG or JPG, and output is always PNG. TGA, EXR, and PSD are not supported. Textures must have Read/Write Enabled turned on in Unity.

**Q: Does it work with large textures?**

Yes. Processing happens in memory, so large textures temporarily increase memory usage.
