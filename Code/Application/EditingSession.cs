// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca
{
    /// <summary>
    /// 編集中の文書(0.4.0 の計画の 4-2 の 4)。いろかのウィンドウ 1 つにつき 1 つ。
    /// 今は、ウィンドウが保存する編集状態(ゾーン・設定・マスクの <see cref="IrocaSessionState"/>)を包むものと、
    /// 原本の画素(<see cref="SourcePixelCache"/>)の持ち主。
    /// 編集状態の持ち主はウィンドウのまま(保存されるフィールドの名前と置き場所を変えると、利用者の作業状態が
    /// レイアウトと Undo から戻らなくなる)。ウィンドウは読み込み・リセット・レシピの読み直しで編集状態をまるごと
    /// 差し替え、Unity は Undo とドメインリロードで書き戻すので、ここは編集状態を覚えずに毎回ウィンドウから受け取る。
    /// 永続化(4-2 の 5)・マスク(6)・ゾーンの操作(7)・変更の通知(8)は、この後の手順でここへ移す。
    /// メインスレッド専用。
    /// </summary>
    internal sealed class EditingSession
    {
        private readonly Func<IrocaSessionState> _state;

        internal EditingSession(Func<IrocaSessionState> state) => _state = state;

        /// <summary>今の編集状態(ウィンドウが保存しているもの)。</summary>
        internal IrocaSessionState State => _state();

        /// <summary>
        /// 原本の画素(作業解像度)。読み直すかどうか(別のテクスチャ・取り込み後の寸法が変わった)と、原本から導いた
        /// 状態を捨てるのはプレビューが決める(PreviewView.EnsureTrueSource)。
        /// </summary>
        internal SourcePixelCache Sources { get; } = new SourcePixelCache();

        internal ColorZone FindZoneById(string id)
        {
            var zones = State?.zones;
            if (string.IsNullOrEmpty(id) || zones == null) return null;
            foreach (var z in zones)
                if (z != null && z.id == id) return z;
            return null;
        }

        /// <summary>id 未設定のゾーンへ GUID を振る。</summary>
        internal void EnsureAllZoneIds()
        {
            var zones = State?.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
                zones[i]?.EnsureId();
        }
    }
}
