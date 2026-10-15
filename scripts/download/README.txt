いろか（Iroca） Ver {VERSION}
VRChat アバターなどのテクスチャを、Unity Editor 上で色替えするツールです。

English follows the Japanese text.

■ この zip の中身
・Iroca_Installer.unitypackage … インストーラです。読み込むと、いろかの最新版をダウンロードして入れます
・README.txt … このファイル
・LICENSE.txt … 利用規約（PolyForm Shield License 1.0.0）

■ 導入方法
1. Unity でプロジェクトを開きます
2. 「Iroca_Installer.unitypackage」を Unity のプロジェクトウィンドウにドラッグ＆ドロップし、「Import」を押します
3. 確認画面で「Install」を押すと、いろかの最新版と NDMF がダウンロードされ、プロジェクトの Packages に入ります
   （インターネット接続が必要です。インストーラ自体は自動で消えます）
4. メニューの「Tools > いろか」からウィンドウを開きます

VCC・ALCOM をお使いの場合は、次のリポジトリを追加して、プロジェクトに「いろか」と
「Non-Destructive Modular Framework」（NDMF）を追加する方法でも導入できます（NDMF もこのリポジトリに載っています）。
https://yukkuri-aoba.github.io/Iroca/index.json

新しい版が出たら、VCC・ALCOM から更新するか、新しいインストーラをもう一度読み込んでください。

旧版（0.1.0、旧名「VRC Avatar Color Changer」）をお使いだった場合は、Assets/VACC フォルダを削除してください。

■ 使い方
オンラインマニュアル: https://yukkuri-aoba.github.io/Iroca/manual/
いろかのウィンドウ上部の「マニュアル」からも開けます。

■ 動作環境
・Unity 2022.3（2022.3.22f1・Windows で動作を確認しています）。VCC や VRChat SDK がなくても動きます
・アバターへの登録（非破壊の色替え）には NDMF 1.8 以上を使います。インストーラで導入した場合は一緒に入ります。
  NDMF がなくても、画像（PNG）の書き出しは使えます
・AI を使う機能（自動調整と、右クリックでの範囲の見分け）には、Unity Sentis 2.1.3（Unity 2022.3.11f1 以降）と
  AI モデル（約 44MB）が必要です。どちらもウィンドウ上部の案内から導入できます
・4096×4096 までのテクスチャで確認しています

■ 利用規約
PolyForm Shield License 1.0.0（全文は LICENSE.txt）
https://polyformproject.org/licenses/shield/1.0.0
・個人・商用を問わず無料でお使いいただけます
・改変・再配布もできます。ただし、本ツールと競合する製品・サービスの提供には使えません
・アバターや衣装の改変・公開は、それぞれの利用規約に従い、ご自身の責任で行ってください

■ クレジット
開発に使用させていただいたモデル・AI モデル・ソフトウェアの一覧は、いろかのウィンドウ右上の「クレジット」と
https://github.com/yukkuri-aoba/Iroca/blob/main/CREDITS.md にあります。
インストーラに同梱しているソフトウェア: VPMPackageAutoInstaller／anatawa12（MIT License）
https://github.com/anatawa12/VPMPackageAutoInstaller

■ 配布元・連絡先
https://github.com/yukkuri-aoba/Iroca
Misskey.io: @yukkuri__aoba@misskey.io

Copyright (c) 2026 yukkuri__aoba


------------------------------------------------------------
Iroca Ver {VERSION} (English)
------------------------------------------------------------
A Unity Editor extension for recoloring textures, mainly for VRChat avatars.
The Japanese text above is the official version.

[Contents]
- Iroca_Installer.unitypackage: the installer. Importing it downloads and installs the latest Iroca
- README.txt: this file
- LICENSE.txt: the license (PolyForm Shield License 1.0.0)

[Installation]
1. Open your project in Unity
2. Drag and drop "Iroca_Installer.unitypackage" into the Project window and click "Import"
3. Click "Install" in the confirmation dialog. The latest Iroca and NDMF are downloaded into your
   project's Packages (an internet connection is required; the installer removes itself)
4. Open the window via "Tools > いろか"

With VCC / ALCOM, you can instead add the repository below and add "いろか" and
"Non-Destructive Modular Framework" (NDMF) to your project. NDMF is also listed in this repository.
https://yukkuri-aoba.github.io/Iroca/index.json

[Manual]
https://yukkuri-aoba.github.io/Iroca/manual/ (Japanese for now)

[Requirements]
- Unity 2022.3 (tested with 2022.3.22f1 on Windows). Works without VCC or the VRChat SDK
- Non-destructive recoloring uses NDMF 1.8 or later (installed together by the installer).
  Without NDMF, exporting images still works
- Auto-tune and the AI suggestion need Unity Sentis 2.1.3 (Unity 2022.3.11f1 or later) and the
  AI models (about 44 MB). You can install them from the notice at the top of the window

[License]
PolyForm Shield License 1.0.0 (see LICENSE.txt)
Free for personal and commercial use. Modification and redistribution are permitted,
except for providing a product that competes with this software.

[Credits]
See the "Credit" button in the Iroca window, or
https://github.com/yukkuri-aoba/Iroca/blob/main/CREDITS.md
Bundled in the installer: VPMPackageAutoInstaller by anatawa12 (MIT License)
