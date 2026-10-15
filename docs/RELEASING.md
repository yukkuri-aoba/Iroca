# リリース手順（Iroca / VPM + VPAI インストーラ）

本書がリリース手順の正。`.claude/instructions/git.md` の「リリース手順」は本書の要約。

## 全体像

```
Build-VpmPackage.ps1（ローカル）                          release.yml（CI）           手動                release-verify.yml（CI）
zip + SHA256 + listing更新 + インストーラ・配布zip生成  →  整合検証 + draft作成  →  資産アップロード+publish  →  公開zipのSHA256照合
```

- 配布資産は 3 つ。
  - **配布 zip**（`Iroca_v<VERSION>.zip`）: 利用者がダウンロードするもの（リリースページ・BOOTH）。中身は VPAI インストーラ・`README.txt`（導入方法・動作環境・利用規約の要約・連絡先。原稿は `scripts/download/README.txt`）・`LICENSE.txt`。`scripts/Build-DownloadZip.ps1` が作る。
  - **VPM zip**（`com.yukkuri-aoba.iroca-<VERSION>.zip`）: git 追跡ファイルだけから作るパッケージ本体。VCC / ALCOM と VPAI インストーラがこれを取得する。VPM の決まりで `package.json` を頂点に置く形なので、利用者が開くものではなく、unitypackage も入れない。
  - **VPAI インストーラ**（`Iroca_Installer.unitypackage`）: 配布 zip の中身と同じもの。インストーラだけを取りたい人と、0.2.0 時点の文書（「Releases から `Iroca_Installer.unitypackage` をダウンロード」）のために単体でも添付する。後述。
- **CI が作るのは draft まで**。資産アップロードと publish は手動。
- publish すると `release-verify.yml` が公開 zip の SHA256 を `docs/index.json`（VPM listing）と照合し、不一致なら fail する。

## 手順

1. **CHANGELOG**: `CHANGELOG.md` に `## [<VERSION>]` 節を書く（空だと CI が fail する）。
2. **zip + インストーラ生成**:
   ```powershell
   .\scripts\Build-VpmPackage.ps1 -Version <VERSION>
   ```
   - package.json（version / url）、README 見出しのバージョン、`docs/index.json`（エントリ + zipSHA256）、VPM zip、`Iroca_Installer.unitypackage`、配布 zip（`Iroca_v<VERSION>.zip`）が更新される。
   - VPM zip のエントリ日時は実行時の HEAD のコミット日時なので、listing を作った後にコミットが増えてから再実行すると SHA256 が変わる。資産だけ作り直したいときは `Build-DownloadZip.ps1` / `Build-Installer.ps1` を単独で実行する。
   - Code/ に未コミットの変更があると止まる（zip を HEAD と 1:1 にするため）。
3. **コミット**: `package.json` / `README.md` / `docs/index.json` / `CHANGELOG.md` をコミット。
4. **main へマージ + タグ**（要ユーザー確認の操作）:
   ```
   git checkout main && git merge develop
   git push origin main
   git tag v<VERSION> && git push origin v<VERSION>
   ```
   → main への push で `pages.yml` が listing とオンラインマニュアルを配信する。手順 5 の publish より前に、
   `https://yukkuri-aoba.github.io/Iroca/index.json` と `https://yukkuri-aoba.github.io/Iroca/manual/` が開くことを確かめる
   （いろかの「マニュアル」ボタン・インストーラ・VCC へのリポジトリ追加は、どれも Pages の配信が前提）。
   → `release.yml` が起動し、以下を検証してから draft を作成する（不一致は fail）:
   - tag ↔ package.json の version / url
   - CHANGELOG に非空の `## [<VERSION>]` 節
   - `docs/index.json` に該当バージョンのエントリ + zipSHA256
5. **資産アップロード + publish**: draft に配布 zip・VPM zip・`Iroca_Installer.unitypackage` をアップロードし、本文のチェックリスト節を削除して publish。BOOTH には配布 zip を上げる。
6. **検証確認**: `release-verify.yml`（publish/edit で起動）が green になることを確認。fail した場合は zip の差し替え、または `Build-VpmPackage.ps1` 再実行 → listing 再コミットで解消する。

## 初回公開の後に確認すること

- [ ] release-verify が green。
- [ ] `https://yukkuri-aoba.github.io/Iroca/index.json` に NDMF の項目も載っている（→「GitHub Pages の配信」）。いろかウィンドウの「マニュアル」でオンラインマニュアルが開く。
- [ ] VCC / ALCOM にリポジトリ URL を追加してインストールできる。いろかのリポジトリだけで NDMF も選べる。
- [ ] 公開版の配布 zip（`Iroca_v<VERSION>.zip`）をダウンロードして展開し、中の `Iroca_Installer.unitypackage` を素の Unity 2022.3 プロジェクトと VRChat プロジェクトへインポートして導入できる（NDMF も入る）。
- [ ] 済んだらこの節を削除する。

> 0.1.0 は listing から削除済み（公開資産が旧名 `vrc-avatar-color-changer` のみで、zip 内 package.json も旧 ID のため現行 URL では修復不能）。復活させたい場合は旧 zip を新 ID で作り直して v0.1.0 リリースへ追加アップロードする必要があるが、旧版を配布し直す価値は乏しい。

## インストーラ unitypackage（VPAI）

[VPMPackageAutoInstaller](https://github.com/anatawa12/VPMPackageAutoInstaller)（MIT）で、インポートすると VPM listing から Iroca の最新版を `Packages/` へ導入する unitypackage を作る。unitypackage 配布でも実体は VPM 管理になるので、`Assets/` と `Packages/` への二重導入を避けられ、以後の更新は VCC / ALCOM から行える。

```powershell
.\scripts\Build-Installer.ps1   # → Iroca_Installer.unitypackage（Build-VpmPackage.ps1 からも呼ばれる）
```

- 設定は `scripts/installer/vpai-config.json`（listing URL と、入れるパッケージの範囲。コメント不可）。いろか（`com.yukkuri-aoba.iroca: >=0.2.0`）と一緒に NDMF（`nadena.dev.ndmf: >=1.8.0`）も入れる。いろかのパッケージは NDMF を必須の依存にしていない（無ければ書き出しだけで動く）ので、インストーラで入れた人には非破壊の色替えまで届くようにするため。上限を付けないのは、ほかのツールのために新しい NDMF が入っているプロジェクトで、版を下げにいかないため。NDMF の listing（`https://vpm.nadena.dev/vpm.json`）も並べている。中身は設定と VPAI 本体 DLL だけで Iroca のコードを含まないため、**範囲指定を変えない限り出力は毎回同じ**（リリースごとに中身が変わるのは zip の方）。
- creator はバージョンと SHA256 をスクリプト内で固定。同じ設定なら出力はバイト単位で同一。
- **listing（GitHub Pages）が公開されていないと動かない。** 取得先は `https://yukkuri-aoba.github.io/Iroca/index.json`。利用者側はネット接続が必要。
- インポートすると「Confirm」ダイアログに導入するパッケージと追加されるリポジトリが出て、**Install** で導入、Cancel で何もせずインストーラだけ消える。
- AI マスク提案用の Unity Sentis は Unity 公式レジストリのパッケージで任意機能のため、VPAI では入らない（従来どおりマニュアルの手順）。
- 2026-09-23 検証: 素の Unity 2022.3.22f1 プロジェクト（VRChat SDK・VCC なし）で、ローカル配信した listing から取得 → `Packages/com.yukkuri-aoba.iroca` 導入・`vpm-manifest.json` 生成・コンパイル成功・インストーラ自己削除まで確認。導入済み（0.2.0）のプロジェクトへ再インポートすると、listing 上の新しい版（架空の 0.2.1）への更新を提案することも確認。バッチモードでは確認ダイアログが自動キャンセルされて何も入らないので、実際の導入の検証は GUI で行う（DLL 内の `VPM_PACKAGE_AUTO_INSTALLER_NO_PROMPT` は環境変数としては効かなかった）。

## GitHub Pages の配信

`.github/workflows/pages.yml` が main から配信する（Pages の Source は GitHub Actions）。載せるのは `docs/index.json` と、`docs/manual/build_manual.py` が原稿から作ったマニュアル（`manual/` にページと CSS・JS・書体・画像・動画）。マニュアルはパッケージには入れず、ここだけで配る。

- 配信のたびに `scripts/pages/build_site.py` が、NDMF の公式 listing（`https://vpm.nadena.dev/vpm.json`）から、いろかが対応する範囲の安定版を index.json に写す。VCC / ALCOM で、いろかのリポジトリだけを追加した人でも NDMF を選べるようにするため。写す項目は公式とまったく同じ（URL・zipSHA256 とも）なので、NDMF の公式リポジトリも追加している環境でもぶつからない（Avatar Optimizer の listing も同じやり方で NDMF を載せている）。
- 対応する範囲は `Code/NdmfIntegration/Iroca.NdmfIntegration.asmdef` の versionDefines（`nadena.dev.ndmf` の式）から読む。NDMF 2.0 が出て対応を広げるときは、asmdef の範囲を変えれば写す範囲も変わる。
- いろかのリリースは頻繁でないので、写した項目はリポジトリへ書き戻さず、毎日配信し直して新しい NDMF を拾う（main への push と手動実行でも配信する）。公式 listing を読めなかった日は組み立てが失敗して配信されず、前回の配信が残る。
- release-verify.yml は `docs/index.json` の、いろかの項目だけを照合するので、写した項目の影響を受けない。
- 手元で確かめるときは `python scripts/pages/build_site.py --out _site`（`_site/` は git 管理外）。

## 補足

- 配布 zip は `Code/` を収集するが、**`Code/Debug/`（開発専用の Debug ウィンドウ）は同梱しない**（`Build-VpmPackage.ps1` が明示的に除外）。IrocaEditor.Debug.asmdef は defineConstraints 無しのため、同梱すると全ユーザーで常時コンパイルされ Debug ウィンドウが見えてしまうのを避けるため。
- `Code/Infra/BuildHelper.cs`（`Assets/Iroca` を unitypackage へ書き出すヘルパー）は、VPAI インストーラへ切り替えた 2026-09-23 以降のリリース手順では使わない。
