# 新しい Unity への対応（将来必要な修正）

Unity 6.5 以降で、いろかはそのままではコンパイルできない。今の利用者には影響しないので**まだ直していない**が、
VRChat の制作環境が 6.5 以降へ進むときには必須になる。その修正の一覧・直し方・確かめ方をここに置く。

内容は 2026-10-31 時点（`dev-0.4.0` の 5c9adef）。修正を始める前に「4. もう一度調べる手順」で一覧を取り直す。
Unity 6（6000.0）の検証ホストは `docs/development-setup.md`、再読み込みと Undo の版の違いは `docs/architecture.md`。

## 1. いつ直すか

VRChat SDK は 3.10.6（2026-10-30）でも Unity 2022.3 のまま。Unity 6 版の SDK は QA 中で、公開日も Unity の版も未発表。
**版が発表された時点で、次の表で判断する。**

| VRChat の SDK が使う版 | 対応 |
|---|---|
| 2022.3・6000.0 | 不要（廃止の診断 0 を確認済み） |
| 6.1〜6.4 | 4 の手順でその版を確かめてから決める（未確認） |
| 6.5 以上 | 必須 |

`package.json` の `"unity": "2022.3"` は下限だけなので、VRChat 以外の 6.5 以上のプロジェクトには今でも入れられ、
入れるとコンパイルエラーになる。そうした環境を支える必要が出たら、VRChat を待たずに直す。

## 2. 修正が必要な箇所

Unity 6000.7.0b7 と 7000.0.0a12 で実際にコンパイルした結果。どちらも同じだった。Core と Runtime に該当は無い。

### コンパイルエラー（13 箇所）

| ファイル | 場所 | 使っている API | 用途 |
|---|---|---|---|
| `Code/Infra/LivePreview.cs` | `Target.GetHashCode`（2 箇所） | `GetInstanceID` | ハッシュ |
| `Code/Infra/PreviewTargeting.cs` | `Routes.IdOf` | `GetInstanceID` | ハッシュ |
| `Code/UI/MeshPartController.cs` | `SceneStamp` | `Scene.handle` の `int` への変換 | シーンの組の印 |
| `Code/Infra/PreviewTargeting.cs` | `Routes` のコンストラクタ（2 箇所） | `GetInstanceID` | 比べるための並べ替え |
| `Code/Application/SessionPersistence.cs` | `MarkRecipeSynced`・`RecipeChangedOutside` | `GetInstanceID` | 辞書のキー |
| `Code/Infra/MeshUvLocator.cs` | `Dedup` | `GetInstanceID` | 重複除去のキー |
| `Code/Infra/MaterialColorSettings.cs` | `Signature` | `GetInstanceID` | 変更検知の文字列 |
| `Code/UI/IrocaWindow.cs` | `EnabledSinceEditorStartKey` | `GetInstanceID` | `SessionState` のキー |
| `Code/UI/IrocaWindow.Document.cs` | `MaskHistorySpillPath` | `GetInstanceID` | 退避ファイルの名前 |
| `Code/Automation/IrocaAutomation.Inspect.cs` | `FindSceneObject` | `EditorUtility.InstanceIDToObject` | MCP の `#123` 指定 |

テストにも同じ API がある（コンパイルは未確認、検索で確認）: `scripts/editor-tests/AutomationTests.cs`・
`scripts/editor-tests/UndoReloadScenario.cs`。

### 警告（7 箇所）

| ファイル | 場所 | 使っている API |
|---|---|---|
| `Code/Infra/MaterialColorSettings.cs` | `FindForTexture` | `FindObjectsSortMode` |
| `Code/Infra/MeshUvLocator.cs` | `FindForTexture` | `FindObjectsSortMode` |
| `Code/Infra/RecipeRegistration.cs` | `SuggestTarget`・`OtherUsers`・`UsedOutside`・`CountSceneMaterialsUsing` | `FindObjectsSortMode` |
| `Code/Infra/BuildHelper.cs` | `Export` | `AssetDatabase.ExportPackage` |

## 3. 直し方

**2022.3 を支えたまま直す。** 置き換え先は 2022.3 にも 6000.0 にも無い（`GetEntityId`・`EditorUtility.EntityIdToObject`・
ソート引数の無い `FindObjectsByType`・`AssetPackage.Package.Export` は、どちらの版でもコンパイルできないと確認した）。
版の境目は公式の説明（6.4 で `EntityId` に置き換わり、6.5 でエラー）から `UNITY_6000_4_OR_NEWER` を候補にする。
6.4 の DLL では未確認なので、実装するときに確かめる。

1. **ハッシュと印は `GetHashCode()` に替える。** `LivePreview`・`Routes.IdOf` は `Object.GetHashCode()`、`SceneStamp` は
   `handle.GetHashCode()`。4 つの版すべてでコンパイルできるので `#if` が要らない。`Routes.IdOf` は消えたオブジェクトでも
   値が変わらないことを前提にしている。新しい版でも同じかは実行して確かめる（未確認）。
2. **識別に使う箇所は、版の違いを 1 か所へ閉じ込める。** `Code/Infra` に小さな型か関数を作り、2022.3 では `int`、
   新しい版では `EntityId` を持たせる。
   - `int` に詰めない。`EntityId` は 8 バイトで、`GetHashCode()` を識別子の代わりにしないよう公式が注意している。
   - 文字列にするとき（`Signature`・`SessionState` のキー・ファイル名）は `EntityId.ToULong` を使う。`ToString()` の形に頼らない。
   - `CompareTo` は 6.7 と 7.0 にあるので、`Routes` の並べ替えはそのまま書ける（順序に意味は無く、同じ順に揃えば足りる）。
3. **MCP の `#123` は仕様を決めてから直す。** ここだけは機械的に置き換えられない。`FindSceneObject` は `int.TryParse` で読むので、
   64 ビットの ID を受けられない。`ulong` で読んで `EntityId.FromULong` に渡すのが候補。MCP for Unity が新しい版で
   どの形の ID を返すかを先に確かめる。ツールの説明（`Code/McpIntegration/IrocaMcpTools.cs`）とテストも合わせる。
4. **`FindObjectsSortMode` は 1 つの関数にまとめる。** 6 箇所とも `None` で順序に頼っていないので、新しい版ではソート引数の
   無い多重定義を呼ぶだけでよい。
5. **`ExportPackage` は急がない。** 配布物を作るビルド用で警告だけ。ビルドする Unity を上げるときに `#if` で分ける。

確かめること:

- 2022.3.22f1・6000.0.67f1・対象の版のすべてで廃止の診断が 0。
- 2022.3 で出力が変わらない（ソース構造検査・golden・unit-run。`docs/testing-architecture.md`）。
- **ID が再読み込みをまたいで保たれること。** `EnabledSinceEditorStartKey` と `MaskHistorySpillPath` はこれを前提にしている。
  `WindowRestoreTests` と `UndoReloadScenario` を新しい版で回す。

## 4. もう一度調べる手順

Unity を入れなくても、DLL だけで確かめられる。

1. 版と revision を `https://services.api.unity.com/unity/editor/release/v1/releases?version=<major.minor>` で調べる。
2. `https://download.unity3d.com/download_unity/<revision>/LinuxEditorInstaller/Unity-<版>.tar.xz` から `Editor/Data/Managed` だけ
   取り出す（`scripts/cloud/setup.sh` と同じ方法。全体で約 4GB、取り出すのは 1 版 80MB ほど）。Python の `tarfile` の `r|xz` で
   流しながら読めば、書庫をディスクに置かずに済む。Windows のインストーラは 7-Zip では開けない。
3. 動いている作業ツリーと分けるため、`git archive HEAD Code scripts/build-check` を一時フォルダへ展開し、そこでコンパイルする:
   ```
   dotnet build scripts/build-check/IrocaEditor.csproj -p:UnityEditorPath=<取り出した Managed>
   dotnet build scripts/build-check/IrocaEditor.Debug.csproj -p:UnityEditorPath=<取り出した Managed>
   ```
4. 下の層にエラーがあると、上の層はコンパイルされない。**一時フォルダのコピーを機械的に直して、エラーが消えるまで繰り返す。**
   1 回の結果だけでは全部出ない（今回は 2 周目で UI と Automation の 4 箇所が出た）。`CS0612`・`CS0618`・`CS0619` を集める。
5. 検索だけでは足りない。`Scene.handle` の暗黙の変換のように、API の名前がコードに現れないものがある。

## 5. 今は直さないが、知っておくこと

| 項目 | 今の状況 | いつ考えるか |
|---|---|---|
| Mono の廃止（CoreCLR） | 7.0 で Editor が CoreCLR になり、再読み込みで static がリセットされなくなる。書き換え可能な static は約 71 個（正規表現での概数）。`BinaryFormatter`・`AppDomain`・`Assembly.Location` は使っていない。`[AutoStaticsCleanup]` などの新しい属性で後始末を書く | 7.0 に対応するとき。上の修正より大きい |
| Built-in Render Pipeline の削除 | 7.0 で削除予定。`Code/UI/PreviewDiff.shader` は `UnityCG.cginc` に依存している（7000.0.0a12 にファイルは残っている）。`AppearanceRenderer` は `Hidden/BlitCopy` と lilToon のベイカーを使う | VRChat の方針が出たら |
| IMGUI | 廃止予定は見つからず、7.0 でも警告なし。UI Toolkit が推奨されているだけ | 移行しない |
| Inference Engine | Unity 6 では 2.4.1 に固定（`MaskSuggestInstall`）。最新は 2.6.1 で、2.5 以降に CoreCLR 対応が入っている。2022.3 の Sentis 2.1.3 は最新のまま | Unity の版を上げるとき。モデルの動作も確かめ直す |
| NDMF の `WithData` | 1.13 で非推奨だが、下限の 1.8.0 にはこれしか無いので警告を抑止している（`IrocaPreviewFilter`） | 下限を 1.13 以上に上げるとき |
| CI | `ci.yml` は 2022.3.22f1 だけを見ていて、新しい版での退行に気づけない | 新しい版に対応した後 |

## 6. 確かめていないこと

- 6.7 と 7.0 はコンパイルだけ。ウィンドウの動作もシェーダーのコンパイルも試していない。
- 6.1〜6.6 は公式文書だけで、DLL では確かめていない。`Scene.handle` がどの版からエラーかは分からない。
- 6.7 と 7.0 のアップグレードガイドは未公開（2026-10-31 時点）。
- DLL は Linux 版を使った。
- Sentis・NDMF・MCP の統合は 7.0 に対してコンパイルしたが、組み合わせた DLL は手元のホストの旧版で、各パッケージの最新版ではない。

## 出典

- [InstanceID から EntityId への移行](https://docs.unity.com/en-us/engine/current/manual/scripting/get-started/instanceid-to-entityid-migration)
- アップグレードガイド: [6.3](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity63.md)・[6.5](https://docs.unity.com/en-us/engine/6000.5/manual/upgrade-guides/upgrade-guide-unity65.md)・[6.6](https://docs.unity.com/en-us/engine/6000.6/manual/upgrade-guides/upgrade-guide-unity66.md)
- [Path to CoreCLR, 2026: Upgrade Guide](https://discussions.unity.com/t/path-to-coreclr-2026-upgrade-guide/1714279)
- [The path to a single Render Pipeline in Unity 7](https://unity.com/topics/the-path-to-a-single-render-pipeline-in-unity-7)
- [Inference Engine の変更履歴](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.6/changelog/CHANGELOG.html)
- [VRChat SDK 3.10.6](https://creators.vrchat.com/releases/release-3-10-6/)・[VRChat 2026.3.3](https://docs.vrchat.com/docs/vrchat-202633)・[Developer Update 2026-08-27](https://ask.vrchat.com/t/developer-update-27-august-2026/48877)
