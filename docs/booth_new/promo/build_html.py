# -*- coding: utf-8 -*-
import base64, os

D = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(D, 'img')

def b64(name):
    with open(os.path.join(IMG, name), 'rb') as f:
        return 'data:image/jpeg;base64,' + base64.b64encode(f.read()).decode()

html = """<meta charset="utf-8">
<title>いろか — BOOTH販促画像コンプ</title>
<style>
  :root{
    --bg:#f7f6f4; --ink:#1d1d1f; --muted:#7d7a75; --hairline:#e4e1dc; --plate-frame:#e9e6e1;
  }
  @media (prefers-color-scheme: dark){
    :root{ --bg:#111114; --ink:#f0efec; --muted:#96938e; --hairline:#28282c; --plate-frame:#232327; }
  }
  :root[data-theme="dark"]{ --bg:#111114; --ink:#f0efec; --muted:#96938e; --hairline:#28282c; --plate-frame:#232327; }
  :root[data-theme="light"]{ --bg:#f7f6f4; --ink:#1d1d1f; --muted:#7d7a75; --hairline:#e4e1dc; --plate-frame:#e9e6e1; }

  html,body{ background:var(--bg); color:var(--ink); }
  body{
    font-family:"Hiragino Sans","Hiragino Kaku Gothic ProN","Yu Gothic UI","Yu Gothic","Noto Sans JP",Meiryo,system-ui,sans-serif;
    font-feature-settings:"palt";
    line-height:1.6;
    -webkit-font-smoothing:antialiased;
  }
  .wrap{ max-width:1080px; margin:0 auto; padding:56px 24px 96px; }

  .eyebrow{ font-size:12px; letter-spacing:.22em; color:var(--muted); text-transform:uppercase; }
  h1{ font-size:clamp(28px,4.5vw,44px); font-weight:800; letter-spacing:.01em; line-height:1.25; margin:10px 0 14px; text-wrap:balance; }
  .lede{ color:var(--muted); max-width:38em; font-size:15px; }
  .rules{ margin:28px 0 0; padding:20px 0 0; border-top:1px solid var(--hairline); display:grid; grid-template-columns:repeat(3,1fr); gap:20px; }
  .rules div strong{ display:block; font-size:14px; margin-bottom:4px; }
  .rules div span{ font-size:13px; color:var(--muted); }
  @media (max-width:720px){ .rules{ grid-template-columns:1fr; } }

  .slide-head{ margin:72px 0 14px; display:flex; align-items:baseline; gap:14px; flex-wrap:wrap; }
  .slide-head .no{ font-size:13px; font-weight:800; letter-spacing:.14em; }
  .slide-head .use{ font-size:13px; color:var(--muted); }
  .note{ font-size:13px; color:var(--muted); margin-top:10px; max-width:44em; }
  .note b{ color:var(--ink); font-weight:600; }

  .stage{ position:relative; width:100%; overflow:hidden; border-radius:4px; outline:1px solid var(--plate-frame); }
  .board{ position:absolute; top:0; left:0; width:1280px; height:1280px; transform-origin:top left; overflow:hidden; font-feature-settings:"palt"; }
  .board.light{ background:#f7f6f4; color:#1d1d1f; }
  .board.dark{ background:#0c0c0f; color:#f5f4f1; }
  .board .b-eyebrow{ font-size:26px; letter-spacing:.30em; font-weight:600; }
  .board.light .b-eyebrow{ color:#8a8781; }
  .board.dark  .b-eyebrow{ color:#7c7c84; }
  .board .b-sub{ font-size:40px; font-weight:500; }
  .board.light .b-sub{ color:#6f6c66; }
  .board.dark  .b-sub{ color:#a9a8b0; }
  .board .b-foot{ position:absolute; left:0; right:0; bottom:56px; text-align:center; font-size:26px; letter-spacing:.06em; }
  .board.light .b-foot{ color:#a09d97; }
  .board.dark  .b-foot{ color:#6b6b73; }
  .board h2{ margin:0; line-height:1.2; }

  /* S1 hero */
  .s1{ display:flex; flex-direction:column; align-items:center; text-align:center; }
  .s1 .b-eyebrow{ margin-top:88px; }
  .s1 h2{ font-size:124px; font-weight:800; letter-spacing:.02em; margin-top:22px; }
  .s1 .b-sub{ margin-top:16px; }
  .s1 .photo{ margin-top:48px; width:1020px; height:730px; overflow:hidden; }
  .s1 .photo img{ width:100%; display:block; margin-top:-56px; }

  /* S2 lineup */
  .s2{ display:flex; flex-direction:column; align-items:center; text-align:center; }
  .s2 h2{ font-size:104px; font-weight:800; letter-spacing:.02em; margin-top:150px; }
  .s2 .b-sub{ margin-top:16px; }
  .s2 .photo{ margin-top:56px; width:1280px; position:relative; }
  .s2 .photo img{ width:100%; display:block; }
  .s2 .divider{ position:absolute; left:23.2%; top:9%; bottom:6%; width:2px; background:#d9d6d0; }
  .s2 .glabel{ position:absolute; top:3%; transform:translateX(-50%); font-size:27px; color:#8a8781; letter-spacing:.14em; white-space:nowrap; }
  .s2 .glabel.g1{ left:10.7%; }
  .s2 .glabel.g2{ left:62.6%; }

  /* S3 macro */
  .s3{ display:flex; flex-direction:column; align-items:center; text-align:center; }
  .s3 h2{ font-size:100px; font-weight:800; letter-spacing:.02em; margin-top:150px; }
  .s3 .b-sub{ margin-top:16px; }
  .s3 .pair{ display:flex; align-items:center; gap:40px; margin-top:96px; }
  .s3 .cell{ display:flex; flex-direction:column; gap:20px; }
  .s3 .cell img{ width:560px; height:560px; display:block; border-radius:8px; }
  .s3 .cell .lbl{ font-size:28px; color:#8a8781; letter-spacing:.08em; }
  .s3 .arrow{ font-size:76px; color:#b9b6b0; font-weight:300; }

  /* S4 verbs */
  .s4{ display:flex; flex-direction:column; }
  .s4 .top{ padding:210px 110px 0; }
  .s4 h2{ font-size:96px; font-weight:800; letter-spacing:.02em; line-height:1.45; }
  .s4 h2 .comma{ color:#c9c6c0; }
  .s4 .b-sub{ margin-top:20px; }
  .s4 .steps{ display:grid; grid-template-columns:repeat(3,1fr); gap:2px; margin:150px 110px 0; }
  .s4 .step{ padding:44px 0 0; border-top:2px solid #1d1d1f; }
  .s4 .step .n{ font-size:26px; letter-spacing:.2em; color:#a09d97; font-variant-numeric:tabular-nums; }
  .s4 .step .v{ font-size:56px; font-weight:800; margin-top:10px; }
  .s4 .step .d{ font-size:27px; color:#8a8781; margin-top:10px; line-height:1.55; padding-right:36px; }
  .s4 .drop{ display:inline-block; width:40px; height:40px; border-radius:50%; background:#2b4bd8; vertical-align:-4px; margin-left:6px; }

  /* S5 closer */
  .s5{ display:flex; flex-direction:column; align-items:center; text-align:center; }
  .s5 h2{ font-size:110px; font-weight:800; letter-spacing:.02em; line-height:1.5; margin-top:380px; }
  .s5 h2 .name{ -webkit-background-clip:text; background-clip:text; color:transparent;
    background-image:linear-gradient(100deg,#f0699b 0%,#4a4ae0 34%,#2fae7e 68%,#e8b32e 100%); }
  .s5 .b-sub{ margin-top:36px; font-size:56px; font-weight:500; color:#8f8f98; }
  .s5 .free{ color:#f5f4f1; font-weight:800; }

  .alts{ margin-top:88px; padding-top:28px; border-top:1px solid var(--hairline); }
  .alts h3{ font-size:20px; font-weight:800; margin-bottom:14px; }
  .alts ul{ list-style:none; display:grid; gap:10px; padding:0; }
  .alts li{ font-size:15px; }
  .alts li span{ color:var(--muted); font-size:13px; margin-left:10px; }
  .prod{ margin-top:56px; padding-top:28px; border-top:1px solid var(--hairline); font-size:14px; color:var(--muted); }
  .prod b{ color:var(--ink); font-weight:700; }
  .prod ul{ margin:10px 0 0 1.2em; display:grid; gap:6px; }
</style>

<div class="wrap">
  <header>
    <div class="eyebrow">Iroca — BOOTH Promotion Comp v2</div>
    <h1>色だけ、変える。<br>いろか BOOTH 販促画像コンプ（全5枚・実写版）</h1>
    <p class="lede">掲載写真はすべて本物です：HAOLAN（フリーアバター）の元テクスチャを、いろかの実C#エンジンで色替えし（髪・衣装・スニーカーの3点、MCP 経由のヘッドレス実行）、Unity 内に組んだ撮影スタジオで同一ポーズ・同一ライティングのまま撮影しました。模式図・手塗りはありません。</p>
    <div class="rules">
      <div><strong>実写だけで語る。</strong><span>並んだ色は全部、実際にいろかが書き出したテクスチャ。</span></div>
      <div><strong>文字は少なく、大きく。</strong><span>150px サムネイルでもメインコピーが読める級数。</span></div>
      <div><strong>色の名前で遊ばない。</strong><span>色は目で見れば分かる。コピーは製品の約束だけを言う。</span></div>
    </div>
  </header>

  <div class="slide-head"><span class="no">SLIDE 1</span><span class="use">メイン画像（表紙）・1280×1280</span></div>
  <div class="stage">
    <div class="board light s1">
      <div class="b-eyebrow">いろか ─ IROCA</div>
      <h2>色だけ、変える。</h2>
      <div class="b-sub">PSDがなくても。スポイトだけで。</div>
      <div class="photo"><img src="%%HERO%%" alt="左が元の青、右がレッドに色替えした衣装の比較"></div>
      <div class="b-foot">アバターテクスチャ色改変ツール ・ 無料</div>
    </div>
  </div>
  <p class="note"><b>構成：</b>左＝元の青（素立ち）、右＝いろかでレッドに色替え（ウィンク＋ピース）。「変わった側が嬉しそう」という物語を 1 枚に。ポーズは <code>Assets/_Promo/PeacePose.anim</code> の筋肉カーブ、表情は Body の BlendShape（ウィンク／にっこり／にこり眉）で調整できます。</p>

  <div class="slide-head"><span class="no">SLIDE 2</span><span class="use">カラーラインナップ・1280×1280</span></div>
  <div class="stage">
    <div class="board light s2">
      <h2>その衣装、ぜんぶの色で。</h2>
      <div class="b-sub">気分で。推し色で。何度でも。</div>
      <div class="photo">
        <img src="%%LINEUP%%" alt="左端が元のテクスチャ、右に5色の生成バリエーション">
        <div class="divider" aria-hidden="true"></div>
        <div class="glabel g1">元のテクスチャ</div>
        <div class="glabel g2">いろかで生成</div>
      </div>
      <div class="b-foot">掲載画像はすべて、いろかの実際の出力です。</div>
    </div>
  </div>
  <p class="note"><b>構成：</b>iPhone のカラーラインナップの文法。左端に元のテクスチャを 1 体だけ分離し、縦線の右側が生成物（レッド／アンバー／ミント／ティール／ピンク）。同一ポーズ・同一光・色だけが違うことが 150px サムネイルでも伝わります。</p>

  <div class="slide-head"><span class="no">SLIDE 3</span><span class="use">品質の証明（マクロ）・1280×1280</span></div>
  <div class="stage">
    <div class="board light s3">
      <h2>陰影も、質感も、そのまま。</h2>
      <div class="b-sub">変わったのは、色だけ。</div>
      <div class="pair">
        <div class="cell"><img src="%%MACROB%%" alt="元の青のスニーカー"><div class="lbl">元のテクスチャ</div></div>
        <div class="arrow">→</div>
        <div class="cell"><img src="%%MACROA%%" alt="赤に色替えしたスニーカー"><div class="lbl">いろかで色替え</div></div>
      </div>
    </div>
  </div>
  <p class="note"><b>構成：</b>組紐・宝石・生地の網目が残ったまま色だけ変わる、いちばん証明力のある寄り。ハイライトの艶や影の落ち方が左右で同じことに注目。</p>

  <div class="slide-head"><span class="no">SLIDE 4</span><span class="use">使い方・1280×1280</span></div>
  <div class="stage">
    <div class="board light s4">
      <div class="top">
        <h2>よみこむ<span class="comma">、</span>すいとる<span class="drop" aria-hidden="true"></span><span class="comma">、</span><br>かきだす<span class="comma">。</span></h2>
        <div class="b-sub">覚えることは、この3つだけ。</div>
      </div>
      <div class="steps">
        <div class="step"><div class="n">1</div><div class="v">よみこむ</div><div class="d">PNG / JPG を<br>そのまま開く。</div></div>
        <div class="step"><div class="n">2</div><div class="v">すいとる</div><div class="d">変えたい色を<br>スポイトでクリック。</div></div>
        <div class="step"><div class="n">3</div><div class="v">かきだす</div><div class="d">変更先を決めて、<br>PNG で書き出し。</div></div>
      </div>
    </div>
  </div>
  <p class="note"><b>備考：</b>実 UI のスクリーンショット版（スポイト・マスク・プレビュー）を後続スライドとして足す場合も、この 3 動詞が目次になります。</p>

  <div class="slide-head"><span class="no">SLIDE 5</span><span class="use">締め・1280×1280</span></div>
  <div class="stage">
    <div class="board dark s5">
      <h2>この色か、あの色か、<br><span class="name">── いろか。</span></h2>
      <div class="b-sub">しかも、<span class="free">無料。</span></div>
      <div class="b-foot">Unity にドラッグ＆ドロップするだけ ・ Tools › いろか</div>
    </div>
  </div>
  <p class="note"><b>コピー意図：</b>製品名の由来をそのままクロージングに。文字グラデーションはラインナップの実色（ピンク→青→ミント→アンバー）から取っています。</p>

  <section class="alts">
    <h3>メインコピー代替案</h3>
    <ul>
      <li><b>「その衣装、ぜんぶの色で。」</b><span>SLIDE 2 のコピーを表紙に昇格させる案</span></li>
      <li><b>「色ちがいは、自分でつくる。」</b><span>行動提案型。BOOTH 説明文の冒頭と連動</span></li>
      <li><b>「PSD？ いりません。」</b><span>最短で差別化。強め</span></li>
      <li><b>「推しの色、着せよう。」</b><span>いちばん遊び心寄り。VRChat 文脈に最適</span></li>
    </ul>
  </section>

  <section class="prod">
    <b>制作メモ（再撮影・書き出し）</b>
    <ul>
      <li>撮影リグは HAOLAN プロジェクトのシーンに未保存で残っています（<b>PromoLineup</b>＝6体の複製、<b>PromoStudio</b>＝3灯＋PromoCamera）。シーンを保存しなければ閉じるだけで元に戻ります。</li>
      <li>色替えテクスチャとマテリアルは <b>Assets/_Promo/</b>、撮影原板（2048〜2560px PNG）は <b>プロジェクト直下 PromoShots/</b> に保存済み。各色は iroca_recolor（MCP）で既存プリセットと同じパラメータから生成。</li>
      <li>ボードは 1280×1280 等倍設計。PNG 書き出し後は 150px サムネイルでの可読性確認を推奨。</li>
      <li>書体：本番書き出しは太ウェイトのある Noto Sans JP（Black/Bold）かヒラギノ角ゴ W8 推奨。</li>
      <li>権利：HAOLAN（かなﾘぁさんち）はフリーアバター。念のため公開前に利用規約の最新版を確認してください。</li>
    </ul>
  </section>
</div>

<script>
  function fit(){
    document.querySelectorAll('.stage').forEach(function(s){
      var b = s.firstElementChild;
      var sc = Math.min(1, s.clientWidth / 1280);
      b.style.transform = 'scale(' + sc + ')';
      s.style.height = (1280 * sc) + 'px';
    });
  }
  addEventListener('resize', fit);
  fit();
</script>
"""

html = html.replace('%%HERO%%', b64('hero_pair.jpg'))
html = html.replace('%%LINEUP%%', b64('lineup_wide.jpg'))
html = html.replace('%%MACROB%%', b64('macro_before.jpg'))
html = html.replace('%%MACROA%%', b64('macro_after.jpg'))

out = os.path.join(D, 'booth-promo-comps.html')
with open(out, 'w', encoding='utf-8') as f:
    f.write(html)
print('written', out, len(html))
