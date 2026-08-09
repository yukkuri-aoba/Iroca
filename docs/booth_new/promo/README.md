# BOOTH 販促画像キット（2026-07 版）

Apple 調・実写主義の販促画像一式。掲載写真はすべて **HAOLAN（フリーアバター）の元テクスチャを
いろかの実 C# エンジンで色替えし、Unity 内スタジオで撮影した実物**（模式図・手塗りなし）。

## ファイル構成

**生成物は公開リポに置かない。** コンプ HTML と書き出し PNG は
`dev_safe/promo/booth_new/`（プライベート側）へ出力される。素材とスクリプトだけが
この公開ディレクトリに残る（`docs/booth/out/` を除外している既存方針と揃えた）。

| ファイル | 役割 |
|---|---|
| `build_html.py` | コンプ HTML を生成（`img/*.jpg` を data URI で埋め込み）→ `dev_safe/promo/booth_new/booth-promo-comps.html` |
| `img/*.jpg` | 撮影済み写真（Web 用縮小版）。原板は下記 PromoShots/ |
| `export_slides.py` | 各ボードを **1280×1280 PNG** に書き出し → `dev_safe/promo/booth_new/out/slide-N.png` |

## ワークフロー

```
# 1. 文言・レイアウトを変える → build_html.py を編集して
.venv\Scripts\python.exe docs/booth_new/promo/build_html.py

# 2. BOOTH 掲載用 PNG に書き出し
.venv\Scripts\python.exe docs/booth_new/promo/export_slides.py
#    → dev_safe/promo/booth_new/out/slide-1.png … slide-5.png（1280×1280）
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

1. 色替えは `iroca_recolor`。**衣装は必ず `HAOLAN_Costume_Red_2`（tolerance 0.4）系を使う** —
   無印 Red（tol 0.27）はロゴの影と AA 境界を取り残しジャギー/ゴーストが出る。
   各色は `Assets/_Promo/presets/Promo_Costume_<色>.json`（Red_2 の target 差し替え版）を preset パス指定で実行。
   髪=tol 0.16（広げると薄紫の地毛を巻き込むので厳守）/靴=ユーザープリセット準拠（tol 0.25）。
2. 並べ撮りは**平行投影**（`orthographic=true`）にすると全員が完全正面になる
   （透視投影では中央から離れた個体が斜めに写る）
3. ポーズ焼き込みは筋肉カーブ入り AnimationClip を AnimatorController 経由で割り当て、
   **プレイモードで撮影**するのが確実（編集モードは Animator 無効化時にスキニングが凍結し、
   ボーンだけ動いて見た目が変わらない罠がある）
4. **腕ポーズの最終方式（表紙採用）**: 筋肉値の座標系は直感と合わないため、プレイ中に
   `animator.enabled=false` にして（プレイ中はスキニングが動き続けるので安全）、
   `Quaternion.FromToRotation` でボーンをワールド座標で直接照準する。
   採用値: 上腕→(0.32,-0.22,1.0) / 前腕→(0.22,0.30,1.0) / 手(人差し指方向)→(0.08,0.85,0.5)。
   **腕をカメラ方向へ伸ばし肘を伸展させる**のが服との貫通を防ぐ要（曲げた肘は袖布と干渉する）。
   指のピース・表情（Body の BlendShape 69ウィンク/46にっこり/60にこり眉）は PeacePose.anim ＋
   編集モードの事前設定で入れる。**検品は正面ズームと側面ビューの両方を毎回撮る**こと。
   注意: 手動照準はシーン非保存のため、撮り直し時はこの手順を再実行する。

## 検収

- 書き出した PNG は 150px に縮小してメインコピーが読めるか確認（BOOTH 一覧サムネイル対策）
- 権利メモ: HAOLAN（かなﾘぁさんち）はフリーアバター。公開前に最新の利用規約を一読推奨
