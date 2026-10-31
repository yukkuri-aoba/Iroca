// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Runtime.CompilerServices;

// Core の internal を使ってよい層。下の層は上の層を知らない(docs/architecture.md)。
[assembly: InternalsVisibleTo("com.yukkuri-aoba.iroca.Editor.Services")]
[assembly: InternalsVisibleTo("com.yukkuri-aoba.iroca.Editor")]
[assembly: InternalsVisibleTo("com.yukkuri-aoba.iroca.Editor.Debug")]
[assembly: InternalsVisibleTo("Iroca.SentisIntegration")]
[assembly: InternalsVisibleTo("Iroca.NdmfIntegration")]
[assembly: InternalsVisibleTo("Iroca.EditorTests")]
