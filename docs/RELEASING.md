# リリース手順（Iroca / VPM + VPAI インストーラ）

本書がリリース手順の正。`.claude/instructions/git.md` の「リリース手順」は本書の要約。

## 全体像

```
Build-VpmPackage.ps1（ローカル）                 release.yml（CI）           手動                release-verify.yml（CI）
zip + SHA256 + listing更新 + インストーラ生成  →  整合検証 + draft作成  →  資産アップロード+publish  →  公開zipのSHA256照合
```

- 配布資産は 2 つ。
  - **VPM zip**（`com.yukkuri-aoba.iroca-<VERSION>.zip`）: git 追跡ファイルだけから作る。unitypackage は同梱しない。VCC / ALCOM と VPAI インストーラがこれを取得する。
  - **VPAI インストーラ**（`Iroca_Installer.unitypackage`）: unitypackage で導入したい利用者（BOOTH など）向け。後述。
- **CI が作るのは draft まで**。資産アップロードと publish は手動。
- publish すると `release-verify.yml` が公開 zip の SHA256 を `docs/index.json`（VPM listing）と照合し、不一致なら fail する。

## 手順

1. **CHANGELOG**: `CHANGELOG.md` に `## [<VERSION>]` 節を書く（空だと CI が fail する）。
2. **zip + インストーラ生成**:
   ```powershell
   .\scripts\Build-VpmPackage.ps1 -Version <VERSION>
   ```
   - package.json（version / url）、README 見出しのバージョン、`docs/index.json`（エントリ + zipSHA256）、zip、`Iroca_Installer.unitypackage` が更新される。
   - Code/ に未コミットの変更があると止まる（zip を HEAD と 1:1 にするため）。
3. **コミット**: `package.json` / `README.md` / `docs/index.json` / `CHANGELOG.md` をコミット。
4. **main へマージ + タグ**（要ユーザー確認の操作）:
   ```
   git checkout main && git merge develop
   git push origin main
   git tag v<VERSION> && git push origin v<VERSION>
   ```
   → `release.yml` が起動し、以下を検証してから draft を作成する（不一致は fail）:
   - tag ↔ package.json の version / url
   - CHANGELOG に非空の `## [<VERSION>]` 節
   - `docs/index.json` に該当バージョンのエントリ + zipSHA256
5. **資産アップロード + publish**: draft に zip と `Iroca_Installer.unitypackage` をアップロードし、本文のチェックリスト節を削除して publish。
6. **検証確認**: `release-verify.yml`（publish/edit で起動）が green になることを確認。fail した場合は zip の差し替え、または `Build-VpmPackage.ps1` 再実行 → listing 再コミットで解消する。

## 公開前の一度きり復旧タスク（初回 0.2.0 のタグ前に完了させる）

最初の公開は **0.2.0 として develop の内容を出す**（2026-09-23 ユーザー決定）。旧 main の 0.2.0（2026-06）は一度も公開されていないので、その出し直しになる。

> **機械ゲート化（2026-08-03）**: この節が残っている間、`release.yml` はタグを push しても
> fail する。ここには**タグより前にできる作業だけ**を置き、全部済んだら本節を丸ごと削除して
> develop にコミットしてから手順 3 以降へ進む（それでゲートが解除される）。
> タグより後の作業は次の節「初回公開の後に確認すること」にある。
> 旧 release.yml の draft は 2026-09-23 に `gh release list`（認証済みなので draft も出る）で 0 件と確認済み。

- [ ] Unity での実地確認（`docs/release-smoke-test.md`）を通す。
- [ ] `CHANGELOG.md` の見出し `## [0.2.0] - 未リリース` を公開日にする。
- [ ] 旧 `v0.2.0` タグ（旧 main の 7f175b7 を指す）をローカルとリモートから消す（要ユーザー確認）:
  ```
  git tag -d v0.2.0
  git push origin :refs/tags/v0.2.0
  ```
  残したままだと手順 4 の `git tag v0.2.0` が「already exists」で止まり、release.yml が新しいコミットで走らない。
- [ ] 手順 2 の `Build-VpmPackage.ps1 -Version 0.2.0` で `docs/index.json` の 0.2.0 エントリを作り直す（今は「存在しない資産の URL + リネーム前 zip の SHA256」のまま）。

**main へのマージで `docs/index.json` が衝突する。** main 側は旧 ID（`com.yukkuri-aoba.vrc-avatar-color-changer`）の listing のままなので、develop 側を採用して解決する:
```
git checkout main && git merge develop
git checkout --theirs docs/index.json && git add docs/index.json && git commit
```

## 初回公開の後に確認すること

- [ ] release-verify が green。
- [ ] GitHub Pages（`docs/` 公開）を有効化し、`https://yukkuri-aoba.github.io/Iroca/index.json` とオンラインマニュアル `https://yukkuri-aoba.github.io/Iroca/manual/` に届く。
- [ ] VCC / ALCOM にリポジトリ URL を追加してインストールできる。
- [ ] 公開版の `Iroca_Installer.unitypackage` を素の Unity 2022.3 プロジェクトと VRChat プロジェクトへインポートして導入できる。
- [ ] 済んだらこの節を削除する。

> 0.1.0 は listing から削除済み（公開資産が旧名 `vrc-avatar-color-changer` のみで、zip 内 package.json も旧 ID のため現行 URL では修復不能）。復活させたい場合は旧 zip を新 ID で作り直して v0.1.0 リリースへ追加アップロードする必要があるが、旧版を配布し直す価値は乏しい。

## インストーラ unitypackage（VPAI）

[VPMPackageAutoInstaller](https://github.com/anatawa12/VPMPackageAutoInstaller)（MIT）で、インポートすると VPM listing から Iroca の最新版を `Packages/` へ導入する unitypackage を作る。unitypackage 配布でも実体は VPM 管理になるので、`Assets/` と `Packages/` への二重導入を避けられ、以後の更新は VCC / ALCOM から行える。

```powershell
.\scripts\Build-Installer.ps1   # → Iroca_Installer.unitypackage（Build-VpmPackage.ps1 からも呼ばれる）
```

- 設定は `scripts/installer/vpai-config.json`（listing URL + `com.yukkuri-aoba.iroca: >=0.2.0`、コメント不可）。中身は設定と VPAI 本体 DLL だけで Iroca のコードを含まないため、**範囲指定を変えない限り出力は毎回同じ**（リリースごとに中身が変わるのは zip の方）。
- creator はバージョンと SHA256 をスクリプト内で固定。同じ設定なら出力はバイト単位で同一。
- **listing（GitHub Pages）が公開されていないと動かない。** 取得先は `https://yukkuri-aoba.github.io/Iroca/index.json`。利用者側はネット接続が必要。
- インポートすると「Confirm」ダイアログに導入するパッケージと追加されるリポジトリが出て、**Install** で導入、Cancel で何もせずインストーラだけ消える。
- AI マスク提案用の Unity Sentis は Unity 公式レジストリのパッケージで任意機能のため、VPAI では入らない（従来どおり MANUAL の手順）。
- 2026-09-23 検証: 素の Unity 2022.3.22f1 プロジェクト（VRChat SDK・VCC なし）で、ローカル配信した listing から取得 → `Packages/com.yukkuri-aoba.iroca` 導入・`vpm-manifest.json` 生成・コンパイル成功・インストーラ自己削除まで確認。導入済み（0.2.0）のプロジェクトへ再インポートすると、listing 上の新しい版（架空の 0.2.1）への更新を提案することも確認。バッチモードでは確認ダイアログが自動キャンセルされて何も入らないので、実際の導入の検証は GUI で行う（DLL 内の `VPM_PACKAGE_AUTO_INSTALLER_NO_PROMPT` は環境変数としては効かなかった）。

## 補足

- 配布 zip は `Code/` を収集するが、**`Code/Debug/`（開発専用の Debug ウィンドウ）は同梱しない**（`Build-VpmPackage.ps1` が明示的に除外）。IrocaEditor.Debug.asmdef は defineConstraints 無しのため、同梱すると全ユーザーで常時コンパイルされ Debug ウィンドウが見えてしまうのを避けるため。
- `Code/Infra/BuildHelper.cs`（`Assets/Iroca` を unitypackage へ書き出すヘルパー）は、VPAI インストーラへ切り替えた 2026-09-23 以降のリリース手順では使わない。
