// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Iroca.NdmfIntegration.IrocaNdmfPlugin))]

namespace Iroca.NdmfIntegration
{
    /// <summary>
    /// いろかの NDMF プラグイン。アバターのビルド(再生・アップロード)時に <see cref="IrocaRecolor"/> を処理し、
    /// 範囲内のマテリアルを複製して元テクスチャの参照を色替え済みテクスチャへ差し替える。
    /// 本体は <see cref="NonDestructiveApplier"/>(NDMF 非依存)で、ここはビルド環境への橋渡しだけ。
    /// 編集中のシーン上の表示は <see cref="IrocaPreviewFilter"/>。
    /// <para>
    /// 順番: Transforming 段で、Modular Avatar の後(衣装の統合やマテリアルの差し替えが済んだ状態の
    /// マテリアルを見る)、TexTransTool の前(デカール・アトラス化は色替え後のテクスチャに対して行う)。
    /// AAO の最適化は Optimizing 段なので必ず後になる。名前で指定した相手が入っていなければ制約は無視される。
    /// </para>
    /// <para>
    /// NDMF が入っているときだけコンパイルされる(asmdef の IROCA_NDMF_PRESENT)。
    /// テクスチャの色替えはプラットフォームに依存しないので全プラットフォームで動かす。
    /// </para>
    /// </summary>
    [RunsOnAllPlatforms]
    internal sealed class IrocaNdmfPlugin : Plugin<IrocaNdmfPlugin>
    {
        public override string QualifiedName => "com.yukkuri-aoba.iroca";
        public override string DisplayName => "Iroca";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .Run("Recolor textures", ctx =>
                    NonDestructiveApplier.Apply(ctx.AvatarRootObject, new BuildHost(ctx)))
                // 編集中(再生していないとき)は、シーン上のアバターに同じ色替えを映す。
                .PreviewingWith(new IrocaPreviewFilter());
        }

        private sealed class BuildHost : NonDestructiveApplier.IHost
        {
            private readonly BuildContext _ctx;

            public BuildHost(BuildContext ctx) { _ctx = ctx; }

            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure)
                => RecipeTextureBuilder.Build(recipe, out failure);

            public void SaveAsset(Object generated) => _ctx.AssetSaver.SaveAsset(generated);

            // ビルド中はこのビルドの登録簿が ObjectRegistry.ActiveRegistry になっている。
            public void RegisterReplaced(Object original, Object replacement)
                => ObjectRegistry.RegisterReplacedObject(original, replacement);

            public void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component,
                RecipeTextureBuilder.Failure failure)
                => NdmfMessages.Report(problem, component, failure);
        }
    }
}
