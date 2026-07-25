# BOOTH 販促画像キット（2026-07 版）

Apple 調・実写主義の販促画像一式。掲載写真はすべて **HAOLAN（フリーアバター）の元テクスチャを
いろかの実 C# エンジンで色替えし、Unity 内スタジオで撮影した実物**（模式図・手塗りなし）。

## ファイル構成

| ファイル | 役割 |
|---|---|
| `build_html.py` | コンプ HTML を生成（`img/*.jpg` を data URI で埋め込み） |
| `img/*.jpg` | 撮影済み写真（Web 用縮小版）。原板は下記 PromoShots/ |
| `booth-promo-comps.html` | 生成されたコンプ（ブラウザで開けばプレビュー） |
| `export_slides.py` | 各ボードを **1280×1280 PNG** に書き出し → `out/slide-N.png` |

## ワークフロー

```
# 1. 文言・レイアウトを変える → build_html.py を編集して
.venv\Scripts\python.exe docs/booth_new/promo/build_html.py

# 2. BOOTH 掲載用 PNG に書き出し
.venv\Scripts\python.exe docs/booth_new/promo/export_slides.py
#    → out/slide-1.png … slide-5.png（1280×1280）
```

写真を差し替える場合は `img/` の同名 jpg を上書きして 1 → 2 の順に再実行。

## 撮影素材の所在（HAOLAN プロジェクト側）

ホスト: `D:\user\ドキュメント\Avatar_Projects\HAOLAN`

- **撮影原板**: `PromoShots/`（hero_pair4.png = 表紙採用 / lineup_wide3.png / macro_sneaker_*2.png、2048〜2560px）
- **色替えテクスチャ**: `Assets/_Promo/Textures/`（衣装・髪・靴 × 5色、iroca_recolor の実出力。`*_preview.png` は検証パネル）
- **マテリアル**: `Assets/_Promo/Materials/`
- **ピースポーズ**: `Assets/_Promo/PeacePose.anim` + `PeaceCtrl.controller`
- シーン上の撮影リグ `PromoLineup` / `PromoStudio` は**未保存**（保存しなければ閉じて消える）

## 再撮影の要点（Unity MCP）

1. 色替えは `iroca_recolor`（マスク不要 zones。パラメータは `Assets/Iroca/Presets/HAOLAN_*_Red.json` 由来）
2. 並べ撮りは**平行投影**（`orthographic=true`）にすると全員が完全正面になる
   （透視投影では中央から離れた個体が斜めに写る）
3. ポーズ焼き込みは筋肉カーブ入り AnimationClip を AnimatorController 経由で割り当て、
   **プレイモードで撮影**するのが確実（編集モードは Animator 無効化時にスキニングが凍結し、
   ボーンだけ動いて見た目が変わらない罠がある）
4. ピースの筋肉値は PeacePose.anim 内（主要: Right Arm Down-Up 0.08 / Front-Back 0.38 /
   Forearm Stretch -1.0 / RightHand.Index・Middle Stretched 1 / Ring・Little -1 / Head Tilt 0.40）

## 検収

- 書き出した PNG は 150px に縮小してメインコピーが読めるか確認（BOOTH 一覧サムネイル対策）
- 権利メモ: HAOLAN（かなﾘぁさんち）はフリーアバター。公開前に最新の利用規約を一読推奨
