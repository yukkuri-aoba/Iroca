// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// いろかの「レシピ」: 1 枚の元テクスチャに対する色替えの編集内容(ゾーン・処理設定・マスク)。
    /// Assets にアセットとして置き、<see cref="IrocaRecolor"/> から参照する。中身はいろかウィンドウで
    /// 編集し、編集状態そのもの(Editor 側の IrocaSessionState)を JSON で持つ。
    /// アバターや衣装の prefab と一緒に持ち運べるよう、ユーザー個人のフォルダ(UserSettings)ではなく
    /// Assets に置く。
    /// <para>
    /// スクリプトの GUID は .meta を追跡して固定している(アセットが GUID で参照するため)。
    /// </para>
    /// </summary>
    public sealed class IrocaRecipe : ScriptableObject
    {
        /// <summary>保存形式の版。読み書きの分岐が要るように形式を変えたら上げる。</summary>
        public const int CurrentFormatVersion = 1;

        [Tooltip("色替えの元になるテクスチャ。マテリアルがこのテクスチャを参照している所だけが差し替わる。")]
        public Texture2D sourceTexture;

        // 編集状態(IrocaSessionState)の JSON。中身は Editor 側だけが解釈する。
        [SerializeField, HideInInspector] internal string sessionJson = "";
        [SerializeField, HideInInspector] internal int formatVersion = CurrentFormatVersion;
    }
}
