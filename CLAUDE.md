# Iroca（旧 VACC）— エージェント運用ガイド

VRChat アバター用テクスチャを直感的に色替えする Unity Editor 拡張。中核は再着色アルゴリズム（色抽出・マスク生成・領域判定）。

## 最重要ルール（常時適用）
- ユーザーへの応答は**日本語**。
- **製品 C#（`Code/Core/PixelProcessor.cs` ほか）が唯一の正。** 同等アルゴリズムを Python で再実装しない（再実装は存在しない）。テスト・計測・視覚レビューは必ず**実 C# の出力**を測る。背景と構成は `docs/testing-architecture.md`。
- **作業が完了したらコミットまで行う。** ただしテストが全パスした、採用確定の変更だけ（手順は git モジュール）。
- アルゴリズム改善は**自律サイクル**で回し、1 ステップごとに承認を求めない（改善サイクルモジュール）。

詳細は以下のモジュールを正とする。このファイルは要約であり、矛盾したらモジュール側を優先する。

## 詳細モジュール
@.claude/instructions/general.md
@.claude/instructions/improvement-cycle.md
@.claude/instructions/git.md
@.claude/instructions/notify.md
