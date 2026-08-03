# リリース手順（Iroca / VPM + unitypackage）

本書がリリース手順の正。`.claude/instructions/git.md` の「リリース手順」は本書の要約。

## 全体像

```
Build-VpmPackage.ps1（ローカル）      release.yml（CI）           手動                release-verify.yml（CI）
zip生成 + SHA256 + listing更新  →  整合検証 + draft作成  →  資産アップロード+publish  →  公開zipのSHA256照合
```

- **zip はローカル生成**（unitypackage を同梱するため CI では完結できない）。
- **CI が作るのは draft まで**。資産アップロードと publish は手動。
- publish すると `release-verify.yml` が公開 zip の SHA256 を `docs/index.json`（VPM listing）と照合し、不一致なら fail する。

## 手順

1. **CHANGELOG**: `CHANGELOG.md` に `## [<VERSION>]` 節を書く（空だと CI が fail する）。
2. **unitypackage**: Unity の開発プロジェクトで `BuildHelper` からエクスポートし `Iroca_Ver<VERSION>.unitypackage` を得る。
3. **zip 生成**:
   ```powershell
   .\scripts\Build-VpmPackage.ps1 -Version <VERSION> -UnityPackagePath <unitypackageのパス>
   ```
   - package.json（version / url）、`docs/index.json`（エントリ + zipSHA256）、zip が更新される。
   - **`-UnityPackagePath` を省略しない。** 省略した zip は unitypackage 非同梱で SHA256 が最終版と一致せず、listing が壊れる。
4. **コミット**: `package.json` / `docs/index.json` / `CHANGELOG.md` をコミット。
5. **main へマージ + タグ**（要ユーザー確認の操作）:
   ```
   git checkout main && git merge develop
   git push origin main
   git tag v<VERSION> && git push origin v<VERSION>
   ```
   → `release.yml` が起動し、以下を検証してから draft を作成する（不一致は fail）:
   - tag ↔ package.json の version / url
   - CHANGELOG に非空の `## [<VERSION>]` 節
   - `docs/index.json` に該当バージョンのエントリ + zipSHA256
6. **資産アップロード + publish**: draft に手順 3 の zip と手順 2 の unitypackage をアップロードし、本文のチェックリスト節を削除して publish。
7. **検証確認**: `release-verify.yml`（publish/edit で起動）が green になることを確認。fail した場合は zip の差し替え、または `Build-VpmPackage.ps1` 再実行 → listing 再コミットで解消する。

## 公開前の一度きり復旧タスク（2026-07-02 時点の残件）

レビュー（`architecture_review_2026-07-02.md` §1.1-1.2）で判明した既存の破損状態。**GitHub Pages を有効化（= listing 公開）する前に必ず完了させること。**

> **機械ゲート化（2026-08-03）**: この節が残っている間、`release.yml` はタグを push しても
> fail する。完了したら本節を丸ごと削除すること（それでゲートが解除される）。
>
> **2026-08-03 時点の実測**: 公開済みリリースは 0 件（draft の有無は要認証のため未確認）、
> Pages は 404（未有効化）＝ 実害はまだ出ていない。ただし `docs/index.json` の 0.2.0 は
> 「存在しない資産の URL + リネーム前 zip の SHA256」のまま。zip 再生成には unitypackage
> （リポジトリ外の Unity 開発プロジェクトでエクスポート）が必要。

- [ ] `v0.2.0` の draft リリース（旧 release.yml が作った不完全な draft が 2 つ存在しうる）を整理し、1 つに統一する。
- [ ] `com.yukkuri-aoba.iroca-0.2.0.zip` を `Build-VpmPackage.ps1 -UnityPackagePath ...` で再生成し、`docs/index.json` の zipSHA256 更新を main へ反映する。
- [ ] zip / unitypackage を v0.2.0 リリースへアップロードして publish、release-verify green を確認する。
- [ ] GitHub Pages（`docs/` 公開）を有効化し、`https://yukkuri-aoba.github.io/Iroca/index.json` の到達性を確認する。
- [ ] VCC にリポジトリ URL を追加して 0.2.0 がインストールできることを実機確認する。

> 0.1.0 は listing から削除済み（公開資産が旧名 `vrc-avatar-color-changer` のみで、zip 内 package.json も旧 ID のため現行 URL では修復不能）。復活させたい場合は旧 zip を新 ID で作り直して v0.1.0 リリースへ追加アップロードする必要があるが、旧版を配布し直す価値は乏しい。

## 補足

- 配布 zip・unitypackage とも `Code/` を収集するが、**`Code/Debug/`（開発専用の Debug ウィンドウ）は両方とも同梱しない**。zip は `Build-VpmPackage.ps1` が、unitypackage は `BuildHelper.cs` が明示的に Code/Debug を除外する。IrocaEditor.Debug.asmdef は defineConstraints 無しのため、同梱すると全ユーザーで常時コンパイルされ Debug ウィンドウが見えてしまうのを避けるため。
- `BuildHelper.cs` が参照する Unity 開発プロジェクトはこのリポジトリの外にある。エクスポート前に `Assets/Iroca` 配下へ旧コード（`Code_Archive/` 等）が紛れていないか確認すること。
