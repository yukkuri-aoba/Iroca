using System.Runtime.CompilerServices;

// パイプライン透明化（デバッグ）機能 asmdef だけに internal API を公開する。
// Code.Debug/ フォルダがプロジェクトに存在しない場合、このターゲットアセンブリは
// 単に存在しないだけで、本体のコンパイルには何の影響もない。
[assembly: InternalsVisibleTo("com.yukkuri-aoba.vrc-avatar-color-changer.Editor.Debug")]
