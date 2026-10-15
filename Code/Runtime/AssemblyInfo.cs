// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Runtime.CompilerServices;

// レシピの中身(IrocaRecipe.sessionJson)は Editor 側だけが読み書きする。
[assembly: InternalsVisibleTo("com.yukkuri-aoba.iroca.Editor")]
[assembly: InternalsVisibleTo("Iroca.NdmfIntegration")]
// EditMode テスト（リポジトリの scripts/editor-tests。配布物には含まれない）。
[assembly: InternalsVisibleTo("Iroca.EditorTests")]
