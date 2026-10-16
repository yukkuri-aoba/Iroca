// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

namespace Iroca.NdmfIntegration
{
    /// <summary>
    /// NDMF のアニメーターの拡張(<see cref="AnimatorServicesContext"/> と、その土台の
    /// <see cref="VirtualControllerContext"/>)を、要るときだけ開く。
    /// <para>
    /// パスでは「開いていれば使ってよい」(Compatible)とだけ宣言する(<see cref="Types"/>)。Required にすると、
    /// いろかの部品が無いアバターでも NDMF が開き、閉じるときに全部のアニメーターコントローラーを仮想化して
    /// 作り直す。いろかを使っていない人のビルドが、そのぶん重くなる。
    /// </para>
    /// <para>
    /// 前のパスから開いていれば(MA の後など)それを使い、閉じない(後続のパスのために NDMF が管理している)。
    /// 開いていなければここで開き、<see cref="Dispose"/> で閉じる(書き戻す)。NDMF に宣言していない拡張を
    /// 開いたままパスを抜けると、後続のツールが直接いじったアニメーターを、ビルドの最後の書き戻しで上書きしてしまう。
    /// </para>
    /// </summary>
    internal sealed class AnimatorServicesOnDemand : IDisposable
    {
        /// <summary>パスに Compatible と宣言する拡張。</summary>
        internal static readonly Type[] Types = { typeof(VirtualControllerContext), typeof(AnimatorServicesContext) };

        private readonly BuildContext _ctx;
        // ここで開いたもの(開いた順)。
        private readonly List<Type> _opened = new List<Type>();
        private AnimatorServicesContext _services;

        public AnimatorServicesOnDemand(BuildContext ctx) { _ctx = ctx; }

        /// <summary>拡張を取り出す(開いていなければ開く)。</summary>
        public AnimatorServicesContext Get()
        {
            if (_services != null) return _services;
            Open<VirtualControllerContext>();   // 依存される側から
            Open<AnimatorServicesContext>();
            return _services = _ctx.Extension<AnimatorServicesContext>();
        }

        /// <summary>ここで開いたものだけを、依存する側から閉じる。</summary>
        public void Dispose()
        {
            _services = null;
            for (int i = _opened.Count - 1; i >= 0; i--) _ctx.DeactivateExtensionContext(_opened[i]);
            _opened.Clear();
        }

        private void Open<T>() where T : IExtensionContext
        {
            if (IsActive<T>(_ctx)) return;
            _ctx.ActivateExtensionContext<T>();
            _opened.Add(typeof(T));
        }

        // NDMF には「開いているか」を聞く API が無いので、取り出せるかで見る(開いていなければ例外になる)。
        internal static bool IsActive<T>(BuildContext ctx) where T : IExtensionContext
        {
            try
            {
                ctx.Extension<T>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
