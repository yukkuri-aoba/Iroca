// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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
    /// 2 段目は Optimizing 段の最初(AAO の前)。VRCFury のように NDMF の外で途中に動くツールが入れた
    /// マテリアルも色替えするため(<see cref="NonDestructiveApplier.Stage"/>)。
    /// </para>
    /// <para>
    /// アニメーションで切り替わるマテリアル(衣装・表情のトグル。MA のマテリアル切り替えも MA の後なので
    /// アニメーションになっている)を書き換えるため、NDMF の AnimatorServicesContext を必要と宣言する。
    /// 宣言しておけば、アニメーターを直接触る後続のツールの前に NDMF が書き戻してくれる
    /// (パスの中で黙って有効にすると、後続のツールの変更を上書きしかねない)。
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
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                    seq.Run("Recolor textures", ctx =>
                            NonDestructiveApplier.Apply(ctx.AvatarRootObject, new BuildHost(ctx),
                                ctx.GetState<NonDestructiveApplier.BuildState>(), NonDestructiveApplier.Stage.First))
                        // 編集中(再生していないとき)は、シーン上のアバターに同じ色替えを映す。
                        .PreviewingWith(new IrocaPreviewFilter()));

            // VRCFury は NDMF の前半(Transforming まで)と最適化段の間に動き、トグルなどで元のマテリアルを
            // 入れ直す。最適化段の最初(AAO の前)にもう一度当てて、それも色替えする(1 段目の複製と
            // テクスチャを使い回す)。コンポーネントはここで外す。
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .WithRequiredExtension(typeof(AnimatorServicesContext), seq =>
                    seq.Run("Recolor materials added after Transforming", ctx =>
                        NonDestructiveApplier.Apply(ctx.AvatarRootObject, new BuildHost(ctx),
                            ctx.GetState<NonDestructiveApplier.BuildState>(), NonDestructiveApplier.Stage.Late)));
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

            public void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component, IrocaRecipe recipe,
                RecipeTextureBuilder.Failure failure)
                => NdmfMessages.Report(problem, component, recipe, failure);

            public void RewriteAnimatedMaterials(Transform scope, Func<Material, Material> mapping)
                => AnimatedMaterials.Rewrite(_ctx.Extension<AnimatorServicesContext>(), scope, mapping);
        }
    }

    /// <summary>
    /// アニメーションのマテリアル切り替え(Renderer の m_Materials.Array.data[i] のキーフレーム)の書き換え。
    /// クリップは NDMF が仮想化した複製なので、元のアニメーションアセットは変わらない。
    /// </summary>
    internal static class AnimatedMaterials
    {
        private const string MaterialSlotPrefix = "m_Materials.Array.data[";

        public static void Rewrite(AnimatorServicesContext animators, Transform scope, Func<Material, Material> mapping)
        {
            var paths = animators.ObjectPathRemapper;
            foreach (var clip in animators.AnimationIndex.ClipsWithObjectCurves.ToList())
            {
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    if (!IsMaterialSlot(binding)) continue;
                    var target = paths.GetObjectForPath(binding.path);
                    if (target == null || !target.transform.IsChildOf(scope)) continue;
                    var curve = clip.GetObjectCurve(binding);
                    if (curve == null) continue;

                    // GetObjectCurve はキャッシュそのものを返すので、複製してから書き換えて戻す。
                    ObjectReferenceKeyframe[] edited = null;
                    for (int i = 0; i < curve.Length; i++)
                    {
                        if (!(curve[i].value is Material from) || from == null) continue;
                        var to = mapping(from);
                        if (to == null || to == from) continue;
                        edited ??= (ObjectReferenceKeyframe[])curve.Clone();
                        edited[i].value = to;
                    }
                    if (edited != null) clip.SetObjectCurve(binding, edited);
                }
            }
        }

        internal static bool IsMaterialSlot(EditorCurveBinding binding)
            => binding.isPPtrCurve
               && typeof(Renderer).IsAssignableFrom(binding.type)
               && binding.propertyName.StartsWith(MaterialSlotPrefix, StringComparison.Ordinal);
    }
}
