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
- [右クリックで直す](#右クリックで直す)
- [AI マスク提案（実験的機能）](#ai-マスク提案実験的機能)
- [プリセット](#プリセット)
- [非破壊で色替え（NDMF）](#非破壊で色替えndmf)
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

ウィンドウは ① 元テクスチャ → ② カラーゾーン → ③ プレビュー → ④ アバターに反映 の順に上から並んでいます。番号どおりに進めれば色替えできます。

#### ステップ 1: 元テクスチャを選ぶ

「① 元テクスチャ」の「テクスチャ」欄へ、色を変えたいテクスチャをドラッグします。

どのテクスチャか分からないときは、いろかのウィンドウを開いたまま Scene でモデルの変えたい所をクリックしても開けます（テクスチャを何も開いていないときだけ。→「[プレビュー機能](#プレビュー機能)」の「Scene でクリックした場所を表示」）。

<!-- スクリーンショット: テクスチャ選択後のウィンドウ全体 -->

#### ステップ 2: 変える色を選ぶ

「② カラーゾーン」の「スポイトで変えたい色を選ぶ」を押し、プレビュー上の変えたい色をクリックします。色替え 1 つぶんの「カラーゾーン」ができ、クリックした色が「サンプルカラー」に入ります。いちばん鮮やかな部分を選ぶとうまくいきます。

この時点では、選んだ色の部分が白っぽくなります（変更先カラーの初期値が白のため）。次のステップで色を決めます。

2 色目からは、ゾーンの下の「スポイトで別の色を追加」を押して、同じようにプレビュー上の色をクリックします（右の「+ 空のゾーン」は、色を決めずにゾーンだけを作ります）。

> 色は必ずスポイトのボタンで取ってください。欄をクリックして開くカラーピッカーのスポイトは、色がわずかにずれるうえクリック位置が残らず、自動調整が AI 提案を使えません。

#### ステップ 3: 変更後の色を決める

ゾーンの「変更先カラー」をクリックして色を選びます。プレビューにすぐ反映されます。

#### ステップ 4: 範囲と仕上がりを整える

1. ゾーンの「自動調整」を押します。そのパーツの暗部からハイライトまでを覆うように、許容範囲などを自動で合わせます（AI モデルが必要です。未導入ならウィンドウ上部に案内が出ます）。模様保持は元の色と変更先の明るさの差から決めるので、変更先カラーを決めてから押してください。
2. 範囲が広い・狭いときは「許容範囲」で微調整します。
3. 仕上がりは次の 2 つで整えます（必要なときだけ）。

自動調整を使わなくても、新しいゾーンは許容範囲 0.20 で始まるので、色を指定すればプレビューはすぐ変わります。

テクスチャ欄の下には「次の手順」が 1 行で出て、色を選ぶ → 変更先カラー → 自動調整の順に、いま何をすればよいかを示します（自動調整を押すか、許容範囲を動かすと消えます）。

| 設定 | 内容 |
|---|---|
| 模様保持 | 元の柄をどれだけ残すか（0 = ベタ塗り、1 = 柄を残す。既定 1.0） |
| 出力彩度 | 純色がベタ塗りに見えるとき 0.7〜0.9 に下げると陰影が戻る（既定 1.0） |

同じ色がほかの場所にもあって一か所だけ変えたいときは、その場所を右クリックして「この部分だけ塗る」を選びます。はみ出した所は「ここは塗らない」、塗れていない所は「ここも塗る」で直せます（→「[右クリックで直す](#右クリックで直す)」）。

さらに細かい調整は「[カラーゾーンの設定](#カラーゾーンの設定)」を参照してください。

#### ステップ 5: アバターに反映する

「④ アバターに反映」の「アバターに非破壊で登録」を押します（→「[非破壊で色替え（NDMF）](#非破壊で色替えndmf)」）。元のテクスチャは書き換えず、再生・アップロードのときだけ色替えされるので、あとから何度でも色を直せます。

色替えした画像ファイルが要るとき（衣装を配布・販売するときなど）は、その下の「テクスチャとして書き出す」を開いて保存します（→「[エクスポート](#エクスポート)」）。NDMF が入っていないプロジェクトでは、この欄は「④ エクスポート」になり、書き出しだけが使えます。

---

### カラーゾーンの設定

#### 基本の項目（常に表示）

**サンプルカラー**

色替え対象の基準色です。この色に近いピクセルが対象になります。右の「スポイト」ボタンで取ると、クリック位置も「自動調整」の手がかりとして記憶します。

**自動調整**

テクスチャを解析して、許容範囲・彩度制限などをまとめて決めます。スポイトした位置に AI マスク提案（MobileSAM）をかけ、そのパーツの暗部からハイライトまでを取りこぼさないように導出します。スポイトで色を取り、変更先カラーを決めてから押すのが最も効果的です（模様保持は、元の色と変更先の明るさの差から決めます）。

- 元テクスチャが未設定、画素を取り出せない、サンプルカラーが未指定（白のまま）のときは押せません。
- AI が未導入のときは導入の案内が出ます（→「[AI マスク提案](#ai-マスク提案実験的機能)」の「必要なもの」）。準備中のときは終わるまで待ちます（進捗バーと中止ボタンが出ます）。
- カラーピッカーで色を指定したゾーンは位置がないため、AI 提案なしで解析します（ゾーンに「スポイト位置がありません」と出ます）。
- 自分で値を変えているときだけ、上書きの確認が出ます。

**許容範囲**

色の一致をどこまで許すかです（0.0〜1.0）。低いほど厳密（選択が狭い）、高いほど広く拾います（ノイズも増えます）。目安は 0.15〜0.40 です。

**変更先カラー**

変えたあとの色です。

**模様保持**

元の明度をどれだけ残すかです。0 に近いほどベタ塗り風、1 に近いほど元の柄をそのまま残します（既定 1.0）。

**出力彩度**

再着色後の鮮やかさです（既定 1.0）。純赤など彩度 100% の色は明暗が潰れてベタ塗りに見えがちです。0.7〜0.9 に下げると、色相は保ったまま陰影が戻ります。

#### 詳細の項目（「詳細設定」を開くと表示）

上から処理の走る順（選択の範囲 → ハイライト → 暗部・無彩色 → 色の写り方 → マッチング距離の重み）に並んでいます。触りすぎたときは、ゾーン末尾の「詳細を既定値に戻す」で詳細だけ初期化できます（色・許容範囲・名前は残ります）。

**連続領域モード（Flood Fill）**（既定 ON）

色が一致した領域のうち、確信度の高い芯を含む「つながった塊」だけに変換を絞ります。離れた同色パーツや背景へのにじみが自動で外れます。既定は自動で、シードは要りません。

特定の塊だけ残したいときはシードを置きます。「シード (任意)」行の「指定」を押してからプレビューをクリックするか、プレビューを Shift+クリックします（詳細設定を閉じていても使えます。置いた位置はプレビューに十字で出ます）。「自動へ」で解除します。

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

開発者向けの項目です。ふだんは表示されず、ウィンドウのタブの「⋮」メニューの「開発者向けの設定を表示」で出ます（プリセットなどで既定から変わっているゾーンでは、常に表示されます）。

明部を白へ寄せて、鏡面ハイライトの白い反射を表現します。光沢・プラスチック向けです。ON のときに出る「ハイライト自動補正」（既定 OFF）は、パーツの地色を自動で見つけて白寄せを全体に効かせます。髪など細い房の多いテクスチャでは広がりすぎることがあるので、その場合は OFF にします。

**暗部・無彩色**

| 設定 | 説明 | 既定 |
|---|---|---|
| シャドウ彩度低下 | この明度より暗いピクセルの彩度を落とす。下げると暗い色も鮮やかに染まる | 0.35 |
| シャドウ巻き込み最低彩度 | 暗いピクセルを影として拾うのに必要な最低彩度。純粋なグレー・黒の色付けを防ぐ | 0.05 |
| 自動しきい値(無彩色判定) | サンプルの彩度がこの値以下なら、色相を無視して無彩色（黒・グレー）として抽出する | 0.05 |

**マッチング距離の重み**

色の距離式そのものの係数です。ふつうは触りません。開発者向けの項目です。ふだんは表示されず、ウィンドウのタブの「⋮」メニューの「開発者向けの設定を表示」で出ます（プリセットなどで既定から変わっているゾーンでは、常に表示されます）。

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

全ゾーン共通の、エッジとノイズの処理です。既定値のままで使えるので、最初は畳まれています。

#### エッジぼかし

選択境界をぼかして、エッジの色をなじませます（0〜5）。

| 値 | 効果 |
|---|---|
| 0 | オフ（エッジがシャープ） |
| 0.5〜1.5 | 標準的なぼかし |
| 2.0 以上 | 強いぼかし（なめらかなテクスチャ向け） |

#### AA境界クリーンアップ

アンチエイリアス境界に残るドットを回収するパス数です。0 でオフ、3 が標準（既定）、4〜5 で強めです。色のついたパーツを境界クリーンアップ（下）ON・エッジぼかし 0 で変えるときは使いません（縁は境界クリーンアップが塗ります）。白・黒・灰のパーツと、境界クリーンアップ OFF またはエッジぼかしを使うときに効きます。

#### 境界クリーンアップ（α分解）

境界の混ざった色（アンチエイリアス・にじみ）を、パーツの色が混ざっている割合だけ色替えします。縁に元の色が点々と残る、許容範囲を上げるとパーツの周りに輪（ゴースト）が出る、境界が薄汚れた中間色（ハロー）になる、を防ぎます。既定は ON で、通常はそのままで構いません。

#### 詳細設定（折りたたみ）

通常は既定のままで構いません。

| 設定 | 説明 | 既定 |
|---|---|---|
| 穴埋めパス数 | アンチエイリアス端の孤立ドットを埋めるパス数 | 5 |
| 穴埋め最小隣接数 | 穴を埋めるのに必要な一致隣接ピクセル数。低いほど積極的に埋める | 4 |
| 境界復元 彩度最小 | 境界復元時の彩度最小閾値 | 0.02 |
| 境界復元 彩度ランプ | 境界復元時の彩度ランプ幅 | 0.08 |
| α分解 近傍半径 | 境界の画素について、パーツの色と背景の色を探す近傍の半径 | 4 |

穴埋め・境界復元の 4 つは、AA境界クリーンアップと同じく、色のついたパーツを境界クリーンアップ ON・エッジぼかし 0 で変えるときは使いません。

---

### プレビュー機能

設定を変えると、約 0.2 秒後に自動で更新されます。

#### ズームとパン

- ズーム 100% は、テクスチャ全体がプレビュー枠にちょうど収まる大きさです。ウィンドウを広げるとプレビューも大きくなります
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

操作行のすぐ下に、プレビュー上のクリックがいま何をするか（スポイト／シード指定／マスクを塗る・消す）と、AI 提案の計算中かどうかが 1 行で出ます。**Esc でどのモードも解除できます**（AI 提案の計算待ちも取り消せます）。

モードが無いときは、同じ行に Scene でクリックした結果の案内（「ここは『○○』のテクスチャです」）が出ます。

#### プレビュー上の目印

- 十字: 連続領域モードのシード位置
- 菱形: スポイトで色を取った位置
- 黄色い線: メッシュの UV の島の輪郭（マスク欄の「UV の島をプレビューに表示」がオンのとき）
- 水色の輪と線: Scene でクリックした場所とその UV の島（下の「Scene でクリックした場所を表示」）

十字と菱形は、ゾーンごとの色（マスクの重ね表示と同じ色）で出ます。

#### Scene でクリックした場所を表示

いろかのウィンドウを開いたまま **Scene でモデルをクリック**すると、その部分がテクスチャのどこにあるかをプレビューに示します。モデルの作り（UV）を知らなくても、「袖はテクスチャのどこか」が分かります。ボタンやモードの切り替えはありません。

- クリックした部分の UV の島を水色で塗り（2 秒ほどで消えます）、島の輪郭と、クリックした点の輪を残します。島は右クリックの「パーツ: この島を…」で足される範囲と同じまとまりです
- 拡大表示中は、その場所が見えるところまでスクロールします
- テクスチャを何も開いていないときは、クリックした部分のテクスチャを開きます
- 別のテクスチャを使う部分をクリックしたときは、プレビューの上に「ここは『○○』のテクスチャです」と出ます。「開く」を押すとそのテクスチャに切り替わり、場所を示します（今のテクスチャの編集内容は保存されます）
- 何もない所をクリックすると表示が消えます

表示だけの機能です。Unity のふつうの選択もそのまま行われ、色替えの設定やマスクは変わりません。

- 左右で UV を共有しているパーツ（左右の袖など）は、どちらをクリックしても同じ場所が光ります。そこを変えると両方の色が変わります
- 当たるのは Scene に見えているメッシュです（非表示のオブジェクトや、Hierarchy の目のアイコンで隠したものには当たりません）。ポーズやシェイプキーを付けた形のままクリックできます
- 透過で抜けている部分（レースの隙間など）も、メッシュがあればその面に当たります

---

### マスク（除外・含める）

プレビューにブラシで塗って、色替えの範囲を手で直します。

- **除外マスク**: 塗った領域を色替えから外します。全ゾーンに効く共通マスクと、ゾーン別マスクがあります。
- **含めるマスク**: 塗った領域を必ず色替えします。強い光沢や、離れた場所の同じパーツなど、色の判定で拾えなかった部分を足すのに使います。ゾーン別のみです。

同じゾーンでは**後から塗ったほうが勝ちます**。含めるを塗るとその場所の除外が消え、除外を塗るとその場所の含めるが消えます（右クリックの「ここも塗る / ここは塗らない」も同じです）。消しゴムは、いま選んでいる種類のマスクだけを消します。全部のゾーンに共通の除外マスクは、ゾーンの含めるより優先されます。

以前の版で含めると除外を重ねて塗った所は、除外が優先されたままです。その場所を塗り直すと解消します。

<!-- スクリーンショット: 除外マスクを描いた状態のプレビュー -->

#### 使い方

操作は「Iroca マスク編集」ウィンドウにまとまっています。

1. マスク欄の「マスクを編集...」を押してウィンドウを開きます。
2. 「編集対象」で各ゾーンか共通マスクを、「マスクの種類」で除外／含めるを選びます（含めるはゾーン選択時のみ）。編集対象には、いま直しているゾーン（最後に触ったゾーン）が最初から選ばれています。
3. ツールの「塗る」「消す」を選び、プレビュー上をドラッグします。ブラシサイズは 1〜64 です。同じボタンをもう一度押すか Esc で抜けます。
4. プレビューを**右クリック**すると、「ここも塗る / ここは塗らない / この部分だけ塗る」を、AI が見分けた部分やメッシュの形に当てられます（→「[右クリックで直す](#右クリックで直す)」）。

重ね表示は、赤が共通の除外、ゾーンの色がゾーン別の除外、緑が含めるです。編集中の 1 枚は明るく、ほかは薄く出ますが、薄いマスクも色替えには効いています。

ウィンドウを閉じるとブラシのモードは解除されます（右クリックメニューはウィンドウを閉じていても使えます）。

#### 含めるマスクの色の写り方

含めるマスクで足した領域は、そのゾーンの色マッチした部分と同じ素材として色替えされます。足した領域の色や明るさは、ゾーンのほかの部分の仕上がりに影響しません。

#### 取り消しとリセット

- Ctrl+Z（または「直前の操作を元に戻す」ボタン）: 直前のストロークを取り消します。
- このマスクをクリア: いま選んでいる対象・種類のマスク 1 枚だけを消します。

---

### 右クリックで直す

プレビューを**右クリック**（mac は Control+クリック）すると、クリックした場所の部分に次の操作を当てるメニューが出ます。左ドラッグはプレビューの移動のままです。

| 項目 | すること | 使う場面 |
|---|---|---|
| ここも塗る | その部分も塗ります | 色の判定で拾えなかった光沢や、離れた場所にある同じパーツを足す |
| ここは塗らない | その部分を塗らないようにします | 同じ色の別のパーツまで変わったとき |
| この部分だけ塗る | その部分の外を塗らないようにします。部分の中は今まで通り色で選ぶので、中にある別の色（ロゴの文字など）は変わりません | 同じ色がほかの場所にもあるのに、一か所だけ変えたいとき |

- メニューの見出しが、直す相手のゾーン（いま直しているゾーン）です。最後に触ったゾーン（作った・カードを押した）が選ばれます。ゾーンが 2 つ以上あるときは、そのカードを枠で示します。「直すゾーンを変える」でも切り替えられます。
- 後から選んだ操作が勝ちます。「ここは塗らない」にした所で「ここも塗る」を選ぶと塗られます（逆も同じです）。
- 「この部分だけ塗る」のあとで別の場所も変えたいときは「ここも塗る」を足します。もう一度「この部分だけ塗る」を選ぶと、その部分だけに置き換わります。
- 「この部分だけ塗る」のあとで「自動調整」を押すと、その部分の中だけを見て範囲を合わせ直します。自動調整の前に使うと、部分の中を取りこぼすことがあります（そのときは知らせが出ます）。白い服などで部分の中に塗られない所が残るときは、そこに「ここも塗る」を当てます。
- 「直すゾーンを変える」で「全部のゾーンに共通」を選ぶと、「ここはどのゾーンでも塗らない」だけが使えます（顔など、どの色替えでも守りたい場所に）。全部のゾーンに共通の「塗らない」が重なる所は、「ここも塗る」でも塗られません（そのときは知らせが出ます。マスク編集で消せます）。
- 当てた結果は通常のマスクです。「ここも塗る」は含めるマスク、「ここは塗らない」と「この部分だけ塗る」は除外マスクとして入るので、Ctrl+Z で 1 つずつ戻せ、ブラシで整えられます。

#### 部分の見分け方

ふつうは AI（MobileSAM）がクリックした場所の部分を見分けます（→「[AI マスク提案](#ai-マスク提案実験的機能)」）。AI が使えないときは、メニューにそう出ます。

テクスチャを使っているメッシュが見つかっていれば、サブメニューの「メッシュの形で」から、メッシュの形（UV の島）にも同じ操作を当てられます。色では分けられない、同じ色の別パーツ（アトラスに並んだ同色のパンツとブーツなど）を 1 回で分けられます。

- **メッシュの形で → ここも塗る / ここは塗らない / この部分だけ塗る（○○ のこの島）**: クリックした場所の UV の島（とその周りのにじみ代）に当てます。
- **メッシュの形で → ○○ 以外のメッシュは塗らない**: クリックしたメッシュ以外をすべて塗らないようにします。「このゾーンはパンツだけ」にしたいときに 1 回で済みます。部位が複数のメッシュにまたがるときは使わず、島ごとに当ててください。

メッシュは、開いているシーンの中、なければテクスチャと同じ素材フォルダの Prefab から自動で探します（テクスチャを以前いろかで書き出した `_recolored.png` をマテリアルが使っている場合も見つかります）。メッシュが無くても、色の処理はこれまで通り動きます。

使っているメッシュは、本体ウィンドウのマスク欄の「メッシュ」に出ます。

- **メッシュ**: 見つけた FBX・Prefab と、使っているメッシュの数です（名前にカーソルを置くと、メッシュの一覧が出ます）。見つからないときや違うものを拾ったときは、Project か Hierarchy から FBX・Prefab をこの欄へドラッグするか、◎ で選びます。欄を空にする（選んで Delete）と自動に戻ります。
- **探し直す**: シーンを開き直したときや、FBX の Read/Write を変えたあとに押します。
- **UV の島をプレビューに表示**: メッシュの UV の島の輪郭を、黄色い線でプレビューに重ねます。「メッシュの形で」で当てる範囲が、この線で囲まれた島（と線のすぐ外のにじみ）です。表示だけで、色替えの結果は変わりません。

右クリックメニューの「メッシュの形で」→「選択中の○○のメッシュを使う」でも、Hierarchy か Project で選んでいる FBX・Prefab を指定できます。

- 「メッシュを読めません」と出たら、FBX のインポート設定で Read/Write を有効にして「探し直す」を押してください。

### AI マスク提案（実験的機能）

右クリックメニューの「ここも塗る / ここは塗らない / この部分だけ塗る」を選ぶと、AI（MobileSAM）がクリックしたパーツの領域を推定し、その部分に操作をその場で当てます。手描きで囲む手間を減らせます。

#### 必要なもの（自動調整にも必要）

未導入のときは、いろかのウィンドウ上部に案内の帯が出ます。そこから 2 つとも導入できます。

1. **Unity Sentis パッケージ**: 帯の「AI 機能を有効化（Sentis を導入）」を押します。手動の場合は Package Manager →「+」→「Add package by name...」→ `com.unity.sentis`（バージョン `2.1.3`）。
2. **AI モデル（2 ファイル・合計約 44 MB）**: 帯の「モデルをダウンロード」を押します。保存先はユーザー共通のフォルダ（Windows は `%LOCALAPPDATA%\Iroca\Models`）なので、別プロジェクトでの再ダウンロードは不要です。手動の場合は [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) から 2 つの `.onnx` を取得し、「モデルフォルダを開く」で開いたフォルダへ置きます。

#### 使い方

1. 選びたいパーツの内側を**右クリック**し、「ここも塗る」「ここは塗らない」「この部分だけ塗る」のどれかを選びます。初回は画像の解析とモデルの準備で少し待ちます（進み具合はマスク編集ウィンドウの「AI 提案」欄に出ます）。
2. 推定された部分に、メニューの見出しのゾーンへすぐ当たります（確定ボタンはありません）。計算中の場所にはプレビュー上に目印が出ます。パーツが複数の島に分かれているときは、島ごとに繰り返します。
3. 間違えたら Ctrl+Z で 1 つずつ戻します。粒度（自動・細かい・大きい）はマスク編集ウィンドウの「AI 提案」欄で変えられます。

1 回の右クリックで取れるのは、つながった 1 つの領域（UV アイランド）です。テクスチャ上でいくつにも分かれたパーツ（衣装 1 着ぶんなど）を全部選ぶには、数回〜十数回のクリックが要ります。取った領域がまれに隣のパーツへはみ出すこともあるので、重ね表示で確かめてください。

#### 苦手なケース

- 白背景に白いパーツなど、見た目の境界が無いもの。手描きマスクを使ってください。
- 数十個の小さなピースに分かれたパーツ。大きな塊だけ AI で選び、残りはブラシで足すのが早道です。
- 「背景まで広がった可能性があります」と警告が出たら、Ctrl+Z で戻し、粒度を「細かい」にするか、パーツのより内側を右クリックし直します。

#### うまく動かないとき

- **左クリックしても何も起きない**: 右クリックでメニューを開いて選びます。
- **メニューに「AI が使えません」「AI モデルが未取得です」と出る**: Sentis かモデルが未導入です。ウィンドウ上部の案内から導入してください（それまでもメッシュの形では使えます）。
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

### 非破壊で色替え（NDMF）

元のテクスチャとマテリアルを書き換えずに色替えできます。アバターに「Iroca Recolor」コンポーネントを付けておくと、再生（Play）・アップロードのときだけ色替え済みのテクスチャに差し替わります。コンポーネントを外せば元に戻ります。編集中は、再生しなくてもシーンのアバターに同じ色替えが映ります（下の「シーンでのプレビュー」）。

#### 必要なもの

- NDMF（Non-Destructive Modular Framework）1.8 以上。VCC / ALCOM でいろかを入れると一緒に入ります。Modular Avatar や Avatar Optimizer を使っているプロジェクトには、たいてい入っています。

#### 使い方

1. いつもどおりゾーンを作って色を決めます。
2. Hierarchy で、色替えしたい衣装（またはアバター）を選びます。選ばなければ、そのテクスチャを使っているオブジェクトの共通の親に付きます。
3. 「④ アバターに反映」の「アバターに非破壊で登録」を押し、確認画面で「登録」を押します。選んだオブジェクトに「Iroca Recolor」が付き、色替えの内容（レシピ）が `Assets/Iroca/Recipes` に保存されます。そのオブジェクトに「Iroca Recolor」が既にあれば、新しく付けずにそこへレシピを足します（衣装や髪など、テクスチャをいくつ登録してもコンポーネントは 1 つです）。同じテクスチャのレシピが既にあれば置き換えます。
4. 再生すると色替え後の姿になります。アップロードも同じです。

登録したあとも、いろかウィンドウで編集を続けられます。編集内容はレシピにも保存され、次の再生・アップロードに反映されます（「④ アバターに反映」の「保存先のレシピ」に保存先が出ます）。同じテクスチャのレシピが複数あるときは、「Iroca Recolor」かレシピのインスペクタの「いろかで開く」で、編集するレシピを選びます。

#### 色替えされる範囲

- 「Iroca Recolor」を付けたオブジェクトとその子のうち、レシピの元テクスチャを使っているマテリアルだけが差し替わります。範囲の外で同じマテリアルを使っている所は元のままです。
- 衣装や表情のトグルなど、アニメーションで後から切り替わるマテリアルも、切り替える先のオブジェクトが範囲内なら同じように色替えされます。元のアニメーションファイルは変わりません。Modular Avatar や VRCFury のトグルで切り替わるマテリアルも対象です。
- 1 つの「Iroca Recolor」は、テクスチャごとのレシピを一覧で持ちます。同じテクスチャのレシピが重なったときは、一覧の上のものが使われます。
- 範囲が入れ子になっているときは、近い（深い）方のコンポーネントが優先されます。
- 以前の版で同じオブジェクトに「Iroca Recolor」を複数付けていた場合は、インスペクタの「1 つにまとめる」で 1 つにできます（色替えの結果は変わりません）。いろかウィンドウからそのオブジェクトへ登録したときも、自動でまとめます。
- 差し替え後のテクスチャは、元テクスチャのインポート設定（最大サイズ・圧縮形式・ミップマップ）に合わせます。PC と Android（Quest）で圧縮形式が違っても、それぞれに合わせます。
- 「Iroca Recolor」のインスペクタに、対象になるマテリアルの数が出ます。0 個のときやレシピに問題があるときは警告が出ます。

#### シーンでのプレビュー

再生しなくても、シーンのアバターで色替え後の見た目を確かめられます。NDMF のプレビュー機能を使うので、元のテクスチャ・マテリアルは書き換えません。

- いろかウィンドウで開いているテクスチャは、ウィンドウのプレビューと同じ内容がアバターに映ります。スライダーを動かしている間は粗い版が追従し、手を止めると細かい版に切り替わります。
- 登録する前でも映ります。「Iroca Recolor」がまだ無いテクスチャは、それを使っているシーン上のすべての物に映るので、登録前に試せます。
- 登録したテクスチャは、「Iroca Recolor」の範囲（再生・アップロードで色替えされる範囲）にだけ映ります。いろかウィンドウで編集していないときは、レシピの内容が映ります。
- 有効なゾーンが 1 つも無いときは何も映しません。
- このプレビューのオン/オフは `Tools > NDM Framework > Configure Previews` の「Iroca」の「色替え」で切り替えます。NDMF のプレビュー全体のオン/オフは `Tools > NDM Framework > Enable Previews` です。

#### 配布・販売するとき

非破壊の色替えは、いろかと NDMF が入ったプロジェクトでビルドするときに行われます。色替えした衣装やアバターを unitypackage などで配布・販売すると、受け取る人のプロジェクトにいろかと NDMF が無い場合は元の色のままです。配るときは「[エクスポート](#エクスポート)」で画像に書き出し、その画像を使うマテリアルを同梱してください。

自分でアップロードしたアバターは、VRChat の中では色替え後の姿で見えます（アップロードしたデータに色替え済みのテクスチャが入るため）。

#### 注意

- 以前「適用して保存」で書き出した `_recolored.png` をマテリアルが使っていても、そのまま登録できます。登録のときに、範囲内でその画像を使っているマテリアルを元のテクスチャへ戻します（確認画面に出ます。Ctrl+Z で戻せます）。FBX の中やパッケージのマテリアルは書き換えられないので、元のテクスチャに差し替えた自分のマテリアルを使ってください。「④ アバターに反映」にも、書き出した画像を使っているマテリアルがあると案内が出ます。
- 最初の再生・アップロードは色替えの計算で数秒かかります。2 回目以降は `Library/Iroca` のキャッシュを使います。シーンでのプレビューも同じキャッシュを使います。登録済みのテクスチャを初めて映すときは、色替えができるまでの数秒は元の色のまま映ります（その間もエディタは止まりません）。
- 編集中のシーンでのプレビューは圧縮前のテクスチャで映すので、アップロード後の見た目とは圧縮のぶんだけわずかに違うことがあります。
- 問題があると NDMF のエラー画面に「いろか: …」と出ます。元テクスチャが読めないなど、色替えできなかったときはアップロードが止まります（元の色のまま上がるのを防ぐため）。

---

### エクスポート

色替えを書き込んだ画像ファイル（PNG）を作ります。自分のアバターには「[非破壊で色替え（NDMF）](#非破壊で色替えndmf)」をお勧めします。書き出しが向いているのは次のようなときです。

- 色替えした衣装やアバターを配布・販売する（受け取る人のプロジェクトにいろかが無くても、その色で見えます）
- NDMF を使わないプロジェクトで色替えする

NDMF が入っているときは「④ アバターに反映」の下の「テクスチャとして書き出す」を開くと出ます。NDMF が無いときは「④ エクスポート」として最初から出ています。

「適用して保存」で保存します。出力は常に PNG です。

- 新規ファイルとして保存: ON なら元のテクスチャを残し、「ファイル名」の別ファイルに保存します。OFF なら元のファイルを上書きします（確認が出ます。元に戻せないのでバックアップをお勧めします）。
- インポート設定を引き継ぐ（既定 ON）: 元テクスチャのインポート設定（タイプ・圧縮・ミップマップなど）を引き継ぎます。
- フォルダを開く: 保存先のフォルダを開きます。
- Project で表示: 保存したテクスチャを Project ウィンドウで選択します（マテリアルへの差し替え用）。

保存に失敗したときは、④ の欄の下にエラーが残ります。詳細は Console にも出ます。

---

### トラブルシューティング

#### 図形の周りに薄い色やドットが残る

アンチエイリアスやぼかしで薄くなった縁が、変換から漏れています。次の順に試してください。

1. 彩度制限（影の厳しさ）を 0.7〜0.9 に上げる
2. 許容範囲を狭める
3. 詳細設定の連続領域モードで離れた残りを切り離す
4. 残った部分を除外マスクで保護する

#### 色がはみ出す

彩度制限を 0.8〜0.95 に上げ、許容範囲を狭めます。白・黒・灰を巻き込んでいるなら彩度ガードを上げます。エッジ柔らかさを 0.0〜0.5 で調整するのも有効です。

アトラスに並んだ同じ色の別パーツ（パンツとブーツなど）は、色では分けられません。マスクで分けます。テクスチャを使うメッシュが見つかっていれば、右クリックの「メッシュの形で」→「ここは塗らない」で島ごとに外せます（→「[右クリックで直す](#右クリックで直す)」）。

#### 境界に細かいノイズが残る

まず加工設定の境界クリーンアップ（α分解）が ON か確かめます。それでも残るなら、彩度制限を 0.1〜0.4 に下げます（そのぶんはみ出しやすくなります）。あわせてエッジぼかしを 0.5〜1.5、エッジ柔らかさを 0.3〜0.7 のいずれかで試します。白・黒・灰のパーツでは AA境界クリーンアップを 3〜5 にするのも効きます。

パーツの中の淡い部分（金属の反射・淡い縁取りなど）が元の色のまま残るときは、許容範囲ではなく彩度制限を下げます。許容範囲を上げても、色の薄い部分は選ばれません。

#### 境界がギザギザしている・硬い

エッジぼかしを 0.5〜1.5 から入れます。足りなければエッジ柔らかさを 0.3〜0.7 に上げ、彩度制限を 0.3〜0.45 まで少し下げます。

#### 仕上がりがベタ塗りになる

出力彩度を 0.7〜0.9 に下げます。模様を残したいときは模様保持を上げます。

#### テクスチャ全体が変わってしまう

一か所だけ変えたいときは、その場所を右クリックして「この部分だけ塗る」を選びます（→「[右クリックで直す](#右クリックで直す)」）。

色の範囲が広すぎるときは、許容範囲を 0.05〜0.15 まで下げ、サンプルカラーをより限定的な色で取り直します。離れた領域まで変わるときは詳細設定の連続領域モードで絞ります。

#### 黒い色に変更できない

黒は明度の情報がほとんどなく、模様保持が効きにくくなります。模様保持を 0〜0.3、エッジ柔らかさを 0 にします。保護したい部分は先に除外マスクで囲っておきます。

#### Scene でモデルをクリックしても場所が出ない

- いろかのウィンドウを閉じていると反応しません（タブの裏に隠れているだけなら反応します）
- プレビューの上に「ここは『○○』のテクスチャです」と出ていれば、クリックした部分は別のテクスチャを使っています。「開く」で切り替えます
- 何も出ないときは、マテリアルのメインテクスチャ（`_MainTex` など）が開いているテクスチャではない可能性があります。Iroca で書き出した `_recolored` のテクスチャを使っているマテリアルは、元のテクスチャと同じものとして扱います

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

画面写真と実演画像には、かなリぁ「[ハオラン-HAOLAN](https://booth.pm/ja/items/3818504)」のテクスチャ（一部は色替え後）を、作者の規約に基づき掲載しています。

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
- [Fixing with right-click](#fixing-with-right-click)
- [AI Mask Suggestion (Experimental)](#ai-mask-suggestion-experimental)
- [Presets](#presets)
- [Non-destructive recoloring (NDMF)](#non-destructive-recoloring-ndmf)
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

The window runs top to bottom: 1. Source Texture, 2. Color Zones, 3. Preview, 4. Apply to Avatar. Follow the numbers to recolor a texture.

#### Step 1: Pick a source texture

Drag the texture you want to recolor onto the "Texture" field under "Source Texture".

If you are not sure which texture it is, keep the いろか window open and click the part of the model you want to change in the Scene view (only while no texture is open; see "Show the spot you click in the Scene" under [Preview](#preview)).

#### Step 2: Choose the color to change

Under "2. Color Zones", press "Pick the color to change", then click the color you want on the preview. A color zone (one recoloring) is created with the clicked color as its "Sample Color". The most vivid spot of the area works best.

At this point the picked area turns whitish (the Target Color starts as white). You choose the new color in the next step.

For a second color, press "Add another color with the eyedropper" below the zones and click the color on the preview in the same way ("+ Empty zone" on the right creates a zone without any colors).

> Always sample with the eyedropper buttons. The eyedropper inside the color picker (opened by clicking the field) reads a slightly different color and records no position, so Auto-tune cannot use the AI suggestion.

#### Step 3: Set the target color

Click the zone's "Target Color" and choose the new color. The preview updates right away.

#### Step 4: Fit the range and finish

1. Press the zone's "Auto-tune". It sets the tolerance and related values so the part is covered from its shadows to its highlights (the AI models are required; a notice at the top of the window offers to install them). Pattern Preserve is derived from the brightness difference between the original and target colors, so press it after choosing the Target Color.
2. If the selection is too wide or too narrow, fine-tune it with "Tolerance".
3. Finish the look with the two settings below (only when needed).

You do not have to use Auto-tune: a new zone starts with a Tolerance of 0.20, so the preview changes as soon as you set the colors.

Below the texture field, a one-line "Next" hint tells you what to do now, in the order: pick the color, choose the Target Color, Auto-tune (it disappears once you press Auto-tune or move Tolerance).

| Setting | What it does |
|---|---|
| Pattern Preserve | How much of the original pattern to keep (0 = flat recolor, 1 = keep pattern; default 1.0) |
| Output Saturation | Lower it to 0.7-0.9 when a pure color looks flat, and the shading comes back (default 1.0) |

If the same color appears elsewhere and you want to change only one spot, right-click it and choose "Paint only this part". Fix spills with "Don't paint here" and missed spots with "Paint here too" (see [Fixing with right-click](#fixing-with-right-click)).

For finer controls, see [Color Zone Settings](#color-zone-settings).

#### Step 5: Apply to the avatar

Press "Register to avatar (non-destructive)" under "Apply to Avatar" (see [Non-destructive recoloring (NDMF)](#non-destructive-recoloring-ndmf)). The original texture is not changed; the recoloring happens only when entering Play mode or uploading, so you can change the colors again at any time.

When you need an image file with the recolor (for example, for an outfit you distribute or sell), open "Export as a texture" below it and save (see [Export](#export)). In a project without NDMF, this section is "4. Export" and only exporting is available.

---

### Color Zone Settings

#### Core controls (always shown)

**Sample Color**

The reference color for the target. Pixels close to it are selected. When you take it with the "Eyedropper" button, the clicked position is also remembered as the hint for "Auto-tune".

**Auto-tune**

Analyzes the texture and sets the tolerance, saturation strictness, and related values together. It runs the AI mask suggestion (MobileSAM) at the sampled position so that the part is covered from its shadows to its bright highlights. It works best after you sample the color with the eyedropper and choose the Target Color (Pattern Preserve is derived from the brightness difference between the two).

- It is disabled when the source texture is not set, when no pixels can be obtained, or when the sample color is still unset (white).
- If the AI is not installed, you are asked to install it (see "Requirements" under [AI Mask Suggestion](#ai-mask-suggestion-experimental)). If it is still getting ready, Auto-tune waits (a progress bar and a Cancel button are shown).
- A zone whose color came from the color picker has no position, so it is analyzed without the AI suggestion (the zone shows "No sampled position").
- A confirmation before overwriting appears only when you have changed values by hand.

**Tolerance**

How far a color can be from the sample and still match (0.0-1.0). Lower is stricter (narrower selection); higher is looser (wider, with more noise). About 0.15-0.40 works for most cases.

**Target Color**

The color applied after recoloring.

**Pattern Preserve**

How much of the original brightness to keep. Near 0 gives a flat recolor; near 1 keeps the original pattern as is (default 1.0).

**Output Saturation**

The vividness after recoloring (default 1.0). Fully saturated colors such as pure red flatten the shading and look solid-filled. Lowering it to 0.7-0.9 keeps the hue while bringing the shading back.

#### Detail controls (open "Details" to show them)

They are laid out in the order the processing runs: Selection range, Highlights, Shadows and neutrals, Color mapping, Matching distance weights. If you over-tweak them, "Reset details to default" at the end of the zone restores only the detail values (color, tolerance, and name are kept).

**Connected Region (Flood Fill)** (default ON)

Restricts recoloring to the connected region that contains a high-confidence core. Same-color parts elsewhere and bleed into the background drop out automatically. It is automatic by default, with no seed needed.

To keep one particular region, set a seed: press "Set" on the "Seed (optional)" row and click the preview, or Shift+click the preview (this works with Details closed; the seed shows as a cross on the preview). "Auto" clears it.

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

A developer setting: hidden by default, shown with "Show developer settings" in the window tab's "⋮" menu (always shown for a zone where it differs from the default, for example after loading a preset).

Pushes bright areas toward white to reproduce the white reflection of specular highlights. For glossy or plastic materials. "Auto Highlight Sample" (default OFF), shown while it is ON, finds the part's base tone so the white blend covers the whole part. On hair-like textures with many thin strands it can spread too much; turn it OFF there.

**Shadows and neutrals**

| Setting | Description | Default |
|---|---|---|
| Shadow Desaturation | Pixels darker than this lose saturation. Lower it to let dark colors recolor more vividly | 0.35 |
| Shadow Forgiveness Sat Min | Minimum saturation to pick up a dark pixel as shadow. Prevents pure gray or black from being colorized | 0.05 |
| Auto Grayscale Threshold | If the sample's saturation is at or below this, hue is ignored and the area is treated as grayscale (black/gray) | 0.05 |

**Matching distance weights**

The coefficients of the color-distance formula itself. You normally leave these alone. A developer setting: hidden by default, shown with "Show developer settings" in the window tab's "⋮" menu (always shown for a zone where it differs from the default, for example after loading a preset).

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

Edge and noise handling, shared by all zones. The defaults work as is, so the section starts collapsed.

#### Edge Feather

Blurs the selection boundary so edge colors blend in (0-5).

| Value | Effect |
|---|---|
| 0 | Off (sharp edges) |
| 0.5-1.5 | Standard blur |
| 2.0+ | Strong blur (for smooth textures) |

#### AA Edge Cleanup

Number of passes that recover dots left at anti-aliased boundaries. 0 is off, 3 is standard (default), 4-5 is strong. It is not used when a colored part is recolored with Edge Decontamination (below) ON and Edge Feather at 0, which handles the edges instead. It applies to white, black, and gray parts, and when Edge Decontamination is OFF or Edge Feather is used.

#### Edge Decontamination

Recolors the mixed colors at the boundary (anti-aliasing, bleeding) only by the share of the part's color in them. This prevents dots of the original color along edges, rings (ghosts) around the part when you raise the Tolerance, and a muddy mid-color (halo) at edges. It is ON by default and usually fine to leave that way.

#### Details (collapsed)

The defaults are usually fine.

| Setting | Description | Default |
|---|---|---|
| Hole Fill Passes | Passes that fill isolated dots at anti-aliased edges | 5 |
| Hole Fill Min Neighbors | Matched neighbors needed to fill a hole. Lower fills more aggressively | 4 |
| Boundary Sat Min | Minimum saturation threshold for boundary recovery | 0.02 |
| Boundary Sat Ramp | Saturation ramp width for boundary recovery | 0.08 |
| Decontamination Radius | Radius searched around a boundary pixel for the part's color and the background color | 4 |

Like AA Edge Cleanup, the four hole-fill and boundary-recovery settings are not used when a colored part is recolored with Edge Decontamination ON and Edge Feather at 0.

---

### Preview

After you change a setting, the preview updates automatically in about 0.2 seconds.

#### Zoom and pan

- At 100% zoom the whole texture just fits in the preview frame. Widening the window makes the preview larger
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

Just below the toolbar row, a single line shows what a click on the preview does right now (Eyedropper / Seed / Painting or Erasing a mask) and whether AI Suggest is still working. **Press Esc to leave any of these modes** (it also cancels pending AI suggestions).

When no mode is active, the same line shows the result of a click in the Scene view ("This part uses '…'").

#### Markers on the preview

- Cross: the Connected Region seed position
- Diamond: the position you sampled with the Eyedropper
- Yellow lines: the outlines of the mesh's UV islands (when "Show UV islands on the preview" is on in the mask section)
- Light-blue ring and lines: the spot you clicked in the Scene view and its UV island (see "Show the spot you click in the Scene" below)

The cross and diamond are drawn in the zone's own color, the same color as its mask overlay.

#### Show the spot you click in the Scene

With the いろか window open, **click the model in the Scene view** and the preview shows where that part is on the texture. You can find "where the sleeve is on the texture" without knowing how the model's UVs are laid out. There is no button or mode to switch on.

- The UV island of the clicked part is filled light blue (the fill fades after about 2 seconds), and its outline and a ring at the clicked point stay. The island is the same unit that the "Part: … this island" right-click items add
- When the preview is zoomed in, it scrolls so the spot is visible
- If no texture is open, the texture of the clicked part is opened
- If you click a part that uses another texture, the line above the preview says "This part uses '…'". Press "Open" to switch to that texture and show the spot (your edits to the current texture are saved)
- Clicking empty space clears the display

It is display only. Unity still selects the object as usual, and none of your recolor settings or masks change.

- Parts that share UVs between left and right (both sleeves, for example) light up the same spot whichever side you click. Changing that spot changes both
- Clicks hit the meshes you can see in the Scene (not hidden objects, and not objects hidden with the eye icon in the Hierarchy). You can click the model as posed, with its shape keys applied
- See-through areas (gaps in lace and the like) still count as the surface if there is mesh there

---

### Masks (Exclude / Include)

Paint on the preview to fix the recolored area by hand.

- **Exclude mask**: keeps the painted area out of recoloring. There is a common mask for every zone, and per-zone masks.
- **Include mask**: always recolors the painted area. Use it to add what color matching could not reach, such as strong gloss or a piece of the same part that sits somewhere else. Per-zone only.

Within a zone, **the later paint wins**. Painting Include removes Exclude at that spot, and painting Exclude removes Include (the right-click "Paint here too / Don't paint here" work the same way). The eraser removes only the mask type you have selected. The common exclude mask for all zones wins over a zone's Include.

Spots where an earlier version left Include and Exclude painted on top of each other still give Exclude priority. Paint over the spot again to resolve it.

#### How to use

Mask editing lives in the "Iroca Masks" window.

1. Press "Edit Masks..." in the mask section to open it.
2. Choose a zone or the common mask under "Edit Target", and Exclude or Include under "Mask Type" (Include needs a zone). "Edit Target" starts at the zone you are working on (the zone you touched last).
3. Pick the Paint or Erase tool and drag on the preview. Brush size ranges from 1 to 64. Press the same button again, or Esc, to leave.
4. **Right-click** the preview to apply "Paint here too / Don't paint here / Paint only this part" to the part the AI finds or to a mesh shape (see [Fixing with right-click](#fixing-with-right-click)).

In the overlay, red is the common exclude mask, the zone's color is a per-zone exclude mask, and green is include. The mask being edited is bright and the rest are dim, but the dim ones are still in effect.

Closing the window leaves brush mode (the right-click menu works even while the window is closed).

#### How include-mask areas are colored

Regions added with the include mask are recolored as the same material as the color-matched part of that zone. Their own color and brightness do not affect how the rest of the zone comes out.

#### Undo and reset

- Ctrl+Z (or the "Undo Last Action" button): undoes the last stroke.
- Clear this mask: clears only the currently selected target and kind.

---

### Fixing with right-click

**Right-click** the preview (Control+click on macOS) to open a menu that applies one of the following to the part you clicked. A left drag still pans the preview.

| Item | What it does | When to use it |
|---|---|---|
| Paint here too | Paints that part as well | To add gloss the color matching missed, or the same part sitting somewhere else |
| Don't paint here | Stops painting that part | When another part with the same color changed too |
| Paint only this part | Stops painting everything outside that part. Inside the part, colors are still picked as before, so other colors inside it (such as logo lettering) stay unchanged | When the same color appears elsewhere but you want to change only one spot |

- The menu heading is the zone being fixed (the zone you are working on): the zone you touched last (created it or clicked its card). With two or more zones, its card is outlined. You can also switch it with "Change the zone to fix".
- The later choice wins. Choosing "Paint here too" where you chose "Don't paint here" paints it (and the other way around).
- To change another spot after "Paint only this part", add it with "Paint here too". Choosing "Paint only this part" again replaces the part.
- Pressing "Auto-tune" after "Paint only this part" refits the range by looking only inside that part. Used before Auto-tune, it can miss areas inside the part (a notice tells you so). If unpainted spots remain inside the part, as can happen with white clothes, apply "Paint here too" there.
- Choosing "All zones" under "Change the zone to fix" offers only "Don't paint here in any zone" (for places such as the face that no recolor should touch). Where an all-zones "don't paint" overlaps, "Paint here too" does not paint it (a notice tells you so; clear it in the mask editor).
- The results are ordinary masks: "Paint here too" goes into the include mask, and "Don't paint here" and "Paint only this part" go into the exclude mask. Ctrl+Z undoes them one at a time and you can touch them up with the brush.

#### How the part is found

Normally the AI (MobileSAM) finds the part at the clicked spot (see [AI Mask Suggestion](#ai-mask-suggestion-experimental)). If the AI is unavailable, the menu says so.

When a mesh that uses the texture is found, the "By mesh shape" submenu applies the same operations by the mesh's shape (UV islands). This separates parts that share the same color and cannot be split by color (such as same-colored pants and boots side by side on an atlas) in one step.

- **By mesh shape → Paint here too / Don't paint here / Paint only this part (this island of ...)**: applies to the clicked UV island (plus its padding around it).
- **By mesh shape → Don't paint meshes other than ...**: stops painting every mesh except the clicked one. Use it when a zone should cover only, say, the pants. When a part spans several meshes, apply it island by island instead.

Meshes are searched automatically in the open scenes, then in prefabs in the texture's asset folder (it also works when the material uses a `_recolored.png` previously exported by Iroca). Without a mesh, color processing works as before.

The meshes in use are shown in the "Mesh" field of the mask section in the main window.

- **Mesh**: the FBX or prefab that was found and how many meshes are in use (hover over it for the list of meshes). If nothing is found or the wrong one is picked, drag an FBX or prefab onto this field from the Project or Hierarchy, or pick one with ◎. Clearing the field (select it and press Delete) goes back to automatic.
- **Search again**: use it after reopening the scene or changing Read/Write on the FBX.
- **Show UV islands on the preview**: overlays the outlines of the mesh's UV islands on the preview as yellow lines. The "By mesh shape" items apply to the island enclosed by these lines (plus the bleed just outside them). Display only; the recolor result does not change.

"By mesh shape" → "Use the meshes of the selected ..." in the right-click menu also takes the FBX or prefab selected in the Hierarchy or Project.

- If you see "cannot read the meshes", enable Read/Write in the FBX import settings and press "Search again".

### AI Mask Suggestion (Experimental)

Choose "Paint here too / Don't paint here / Paint only this part" in the right-click menu and the AI (MobileSAM) estimates the clicked part's region and applies the operation to it right away. It saves you from outlining parts by hand.

#### Requirements (also required by Auto-tune)

When they are missing, a notice appears at the top of the Iroca window. Both items can be set up from there.

1. **Unity Sentis package**: press "Enable AI feature (install Sentis)" in the notice. To install manually: Package Manager → "+" → "Add package by name..." → `com.unity.sentis` (version `2.1.3`).
2. **AI models (2 files, ~44 MB total)**: press "Download models" in the notice. They are stored in a per-user shared folder (`%LOCALAPPDATA%\Iroca\Models` on Windows), so other projects do not need to download them again. To install manually, get the two `.onnx` files from [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models) and place them into the folder opened by "Open model folder".

#### How to use

1. **Right-click** inside the part you want and choose "Paint here too", "Don't paint here", or "Paint only this part". The first time, wait a moment for image analysis and model loading (progress is shown in the "AI Suggest" section of the mask window).
2. The operation is applied at once to the estimated part, for the zone shown in the menu heading (there is no confirm button). A marker shows spots still being computed. If the part is split into several islands, repeat for each.
3. Undo mistakes one at a time with Ctrl+Z. Change the granularity (Auto / Fine / Coarse) in the "AI Suggest" section of the mask window.

One right-click picks one connected region (UV island). Selecting a part that is split into many pieces on the texture (such as a whole outfit) takes several to a dozen or more clicks. A picked region occasionally spills into a neighboring part, so check the overlay.

#### Known limitations

- Parts with no visible boundary, such as white pieces on a white background. Use hand-painted masks there.
- Parts split into dozens of tiny pieces. Select the large chunks with AI and fill the rest with the brush.
- If you see the "may have spread into the background" warning, undo with Ctrl+Z, then set the granularity to "Fine" or right-click further inside the part.

#### If it does not work

- **Nothing happens on a left click**: open the menu with a right-click and choose an item.
- **The menu says "AI unavailable" or "AI model not downloaded"**: Sentis or the models are not installed. Set them up from the notice at the top of the window (mesh shapes work in the meantime).
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

### Non-destructive recoloring (NDMF)

You can recolor without changing the original texture or materials. With the "Iroca Recolor" component on the avatar, the recolored texture is swapped in only when entering Play mode or uploading. Remove the component to go back. While you edit, the same recoloring is also shown on the avatar in the Scene without entering Play mode (see "Preview in the Scene" below).

#### Requirements

- NDMF (Non-Destructive Modular Framework) 1.8 or later. It is installed together with Iroca when you add Iroca with VCC / ALCOM. Projects that use Modular Avatar or Avatar Optimizer usually have it already.

#### How to use

1. Create zones and pick colors as usual.
2. In the Hierarchy, select the outfit (or avatar) to recolor. If nothing is selected, the component goes on the common parent of the objects that use the texture.
3. Press "Register to avatar (non-destructive)" under "Apply to Avatar", then "Register" in the confirmation. "Iroca Recolor" is added to the selected object, and the recoloring settings (the recipe) are saved under `Assets/Iroca/Recipes`. If the object already has "Iroca Recolor", the recipe is added to it instead of adding another component (one component no matter how many textures, such as the outfit and hair, you register). A recipe for the same texture is replaced.
4. Enter Play mode to see the recolored avatar. Uploading works the same way.

You can keep editing in the Iroca window after registering. Edits are also saved to the recipe and used by the next Play mode or upload (the "Recipe" field under "Apply to Avatar" shows where). When a texture has several recipes, choose the one to edit with "Open in Iroca" in the inspector of "Iroca Recolor" or of the recipe.

#### What gets recolored

- Only materials that use the recipe's source texture, on the object with "Iroca Recolor" and its children, are swapped. Objects outside that scope keep the original even if they use the same material.
- Materials that an animation switches to later (outfit or expression toggles) are recolored the same way, as long as the switched object is in the scope. The original animation files are not changed. This includes materials switched by Modular Avatar or VRCFury toggles.
- One "Iroca Recolor" holds a list of recipes, one per texture. If two recipes use the same texture, the upper one in the list is used.
- When scopes are nested, the closer (deeper) component wins.
- If an earlier version added several "Iroca Recolor" components to the same object, "Merge into one" in the inspector combines them (the result does not change). Registering to that object from the Iroca window merges them automatically too.
- The recolored texture follows the source texture's import settings (max size, compression format, mipmaps), for PC and Android (Quest) alike.
- The inspector of "Iroca Recolor" shows how many materials are targeted, and warns when there are none or the recipe has a problem.

#### Preview in the Scene

You can check the recolored look on the avatar in the Scene without entering Play mode. It uses NDMF's preview, so the original textures and materials are not changed.

- The texture open in the Iroca window is shown on the avatar exactly as in the window's preview. While you drag a slider, a coarse version follows; when you stop, it switches to the detailed one.
- It works even before registering. A texture that has no "Iroca Recolor" yet is shown on everything in the scene that uses it, so you can try it out first.
- A registered texture is shown only within the scope of "Iroca Recolor" (the part that is recolored on Play or upload). When the Iroca window is not editing it, the recipe is shown.
- Nothing is shown while there is no enabled zone.
- Turn this preview on or off with "Recolor" under "Iroca" in `Tools > NDM Framework > Configure Previews`. `Tools > NDM Framework > Enable Previews` turns all NDMF previews on or off.

#### Distributing or selling

Non-destructive recoloring runs when a project with Iroca and NDMF builds the avatar. If you distribute or sell the recolored outfit or avatar as a unitypackage or similar, it stays in its original colors in a recipient's project that does not have Iroca and NDMF. To distribute it, export an image with [Export](#export) and include materials that use that image.

An avatar you upload yourself appears recolored inside VRChat (the uploaded data contains the recolored texture).

#### Notes

- Even if a material uses a `_recolored.png` exported earlier with "Apply and save", you can register as is. When registering, materials in the scope that use that image are switched back to the original texture (shown in the confirmation; Ctrl+Z restores them). Materials inside an FBX or a package cannot be changed, so use your own material that points to the original texture. "Apply to Avatar" also tells you when materials use an exported image.
- The first Play mode or upload takes a few seconds to compute the colors. Later runs use the cache in `Library/Iroca`. The Scene preview shares this cache. The first time a registered texture is shown, it stays in its original colors for a few seconds until the recoloring is ready (the Editor keeps responding meanwhile).
- While you edit, the Scene preview shows the uncompressed texture, so it can differ very slightly from the uploaded avatar (by the compression).
- Problems are shown in the NDMF error window as "Iroca: ...". If recoloring fails (for example, the source texture cannot be read), the upload is stopped so that the avatar does not go up in its original colors by mistake.

---

### Export

Creates an image file (PNG) with the recolor written into it. For your own avatar, [Non-destructive recoloring (NDMF)](#non-destructive-recoloring-ndmf) is recommended. Exporting suits cases like these:

- You distribute or sell the recolored outfit or avatar (it shows in those colors even if the recipient's project has no Iroca)
- You recolor in a project that does not use NDMF

When NDMF is installed, open "Export as a texture" under "Apply to Avatar". Without NDMF, it is shown from the start as "4. Export".

Press "Apply & Save" to save. The output is always PNG.

- Save as new file: when ON, the original texture is kept and the result goes to a separate file named by "File Name". When OFF, the original file is overwritten (a confirmation appears first; it cannot be undone, so keep a backup).
- Inherit Import Settings (default ON): the output inherits the source texture's import settings (texture type, compression, mipmaps, and so on).
- Open Folder: opens the save folder.
- Show in Project: selects the saved texture in the Project window (handy when assigning it to a material).

If saving fails, the error stays at the bottom of section 4. The details are also written to the Console.

---

### Troubleshooting

#### Faint colors or dots remain around shapes

Edges lightened by anti-aliasing or blur are being left unrecolored. Try these in order.

1. Raise Saturation Strictness to 0.7-0.9
2. Narrow the Tolerance
3. Use Connected Region (Flood Fill) in Details to cut off separated leftovers
4. Protect what remains with the exclude mask

#### Color bleeds outside the intended area

Raise Saturation Strictness to 0.8-0.95 and narrow the Tolerance. If white, black, or gray is being pulled in, raise Saturation Guard. Adjusting Edge Softness within 0.0-0.5 can also help.

Separate parts that share the same color on an atlas (such as pants and boots) cannot be split by color; split them with a mask. When a mesh using the texture is found, the right-click item "By mesh shape" → "Don't paint here" removes them island by island (see [Fixing with right-click](#fixing-with-right-click)).

#### Fine noise remains at boundaries

First check that Edge Decontamination is ON in the Processing settings. If noise still remains, lower Saturation Strictness to 0.1-0.4 (with a bit more bleed). Along with that, try Edge Feather at 0.5-1.5 or Edge Softness at 0.3-0.7. For white, black, and gray parts, AA Edge Cleanup at 3-5 also helps.

If pale areas inside the part (metal reflections, pale trims, and so on) keep their original color, lower Saturation Strictness instead of raising the Tolerance. Raising the Tolerance does not select faintly colored areas.

#### Boundaries look jagged or hard

Add Edge Feather from 0.5-1.5. If that is not enough, raise Edge Softness to 0.3-0.7 and lower Saturation Strictness slightly to 0.3-0.45.

#### The result looks flat (solid fill)

Lower Output Saturation to 0.7-0.9. Raise Pattern Preserve if you want to keep the pattern.

#### The whole texture changes

To change only one spot, right-click it and choose "Paint only this part" (see [Fixing with right-click](#fixing-with-right-click)).

If the color range is too wide, lower the Tolerance to 0.05-0.15 and re-sample a more specific color. If separate areas still change, narrow it down with Connected Region (Flood Fill) in Details.

#### Cannot change to black

Black has almost no brightness information, so Pattern Preserve has little to work with. Set Pattern Preserve to 0-0.3 and Edge Softness to 0. Protect anything you want to keep with the exclude mask first.

#### Clicking the model in the Scene shows nothing

- Nothing happens while the いろか window is closed (it still works if the window is just behind another tab)
- If the line above the preview says "This part uses '…'", the clicked part uses another texture. Press "Open" to switch
- If nothing appears at all, the material's main texture (`_MainTex` and the like) may not be the open texture. Materials that use a `_recolored` texture exported by Iroca count as the original texture

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

The screenshots and demo images use the texture of "[HAOLAN](https://booth.pm/ja/items/3818504)" by かなリぁ (some recolored), shown under the creator's terms.
