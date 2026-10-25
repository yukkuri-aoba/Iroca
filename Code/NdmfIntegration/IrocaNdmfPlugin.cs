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
    /// 2 段目は Optimizing 段の最初(AAO と TexTransTool の前)。VRCFury のように NDMF の外で途中に動くツールが
    /// 入れたマテリアルも色替えするため(<see cref="NonDestructiveApplier.Stage"/>)。TexTransTool はアトラス化と
    /// テクスチャの設定(TextureConfigurator)を Optimizing 段で行うので、1 段目と同じく色替え後のテクスチャを渡す。
    /// その前の Resolving 段(Modular Avatar より前)では、各コンポーネントの範囲にある Renderer を記録するだけ
    /// (骨の統合などで範囲の外へ移される小物も色替えするため。<see cref="NonDestructiveApplier.RecordScopes"/>)。
    /// </para>
    /// <para>
    /// アニメーションで切り替わるマテリアル(衣装・表情のトグル。MA のマテリアル切り替えも MA の後なので
    /// アニメーションになっている)を書き換えるため、NDMF の AnimatorServicesContext を使う。Required とは
    /// 宣言せず、いろかの部品があるアバターでだけ開く(<see cref="AnimatorServicesOnDemand"/>。いろかを
    /// 使っていないアバターのビルドに、アニメーターの作り直しを足さないため)。
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
            // Modular Avatar の Merge Armature・Bone Proxy(Transforming 段)や VRCFury の Armature Link は、
            // 衣装の骨の下の小物をアバター本体の骨の下へ移す。色替えの範囲は編集中の親子関係(シーンのプレビュー・
            // インスペクタと同じ)で決めたいので、それより前の Resolving 段で各コンポーネントの範囲を記録しておく。
            InPhase(BuildPhase.Resolving)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Record recolor scopes", ctx =>
                {
                    if (ctx.AvatarRootObject.GetComponentInChildren<IrocaRecolor>(true) == null) return;
                    NonDestructiveApplier.RecordScopes(ctx.AvatarRootObject,
                        ctx.GetState<NonDestructiveApplier.BuildState>());
                });

            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .WithCompatibleExtensions(AnimatorServicesOnDemand.Types, seq =>
                    seq.Run("Recolor textures", ctx => Run(ctx, NonDestructiveApplier.Stage.First))
                        // 編集中(再生していないとき)は、シーン上のアバターに同じ色替えを映す。
                        .PreviewingWith(new IrocaPreviewFilter()));

            // VRCFury は NDMF の前半(Transforming まで)と最適化段の間に動き、トグルなどで元のマテリアルを
            // 入れ直す。最適化段の最初(AAO・TexTransTool の前)にもう一度当てて、それも色替えする(1 段目の複製と
            // テクスチャを使い回す)。コンポーネントはここで外す。
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .WithCompatibleExtensions(AnimatorServicesOnDemand.Types, seq =>
                    seq.Run("Recolor materials added after Transforming",
                        ctx => Run(ctx, NonDestructiveApplier.Stage.Late)));
        }

        private static void Run(BuildContext ctx, NonDestructiveApplier.Stage stage)
        {
            // いろかの部品が無いアバターでは何もしない(アニメーターの拡張も開かない)。
            if (ctx.AvatarRootObject.GetComponentInChildren<IrocaRecolor>(true) == null) return;
            using (var animators = new AnimatorServicesOnDemand(ctx))
                NonDestructiveApplier.Apply(ctx.AvatarRootObject, new BuildHost(ctx, animators),
                    ctx.GetState<NonDestructiveApplier.BuildState>(), stage);
        }

        private sealed class BuildHost : NonDestructiveApplier.IHost
        {
            private readonly BuildContext _ctx;
            private readonly AnimatorServicesOnDemand _animators;

            public BuildHost(BuildContext ctx, AnimatorServicesOnDemand animators)
            {
                _ctx = ctx;
                _animators = animators;
            }

            public Texture2D BuildTexture(IrocaRecipe recipe, out RecipeTextureBuilder.Failure failure)
                => RecipeTextureBuilder.Build(recipe, out failure);

            public void SaveAsset(Object generated) => _ctx.AssetSaver.SaveAsset(generated);

            // ビルド中はこのビルドの登録簿が ObjectRegistry.ActiveRegistry になっている。
            public void RegisterReplaced(Object original, Object replacement)
                => ObjectRegistry.RegisterReplacedObject(original, replacement);

            public void Report(NonDestructiveApplier.Problem problem, IrocaRecolor component, IrocaRecipe recipe,
                RecipeTextureBuilder.Failure failure)
                => NdmfMessages.Report(problem, component, recipe, failure);

            public void RewriteAnimatedMaterials(Func<Transform, bool> inScope, Func<Material, Material> mapping)
                => AnimatedMaterials.Rewrite(_animators.Get(), inScope, mapping);
        }
    }

    /// <summary>
    /// アニメーションのマテリアル切り替え(Renderer の m_Materials.Array.data[i] のキーフレーム)の書き換え。
    /// クリップは NDMF が仮想化した複製なので、元のアニメーションアセットは変わらない。
    /// </summary>
    internal static class AnimatedMaterials
    {
        private const string MaterialSlotPrefix = "m_Materials.Array.data[";

        public static void Rewrite(AnimatorServicesContext animators, Func<Transform, bool> inScope,
            Func<Material, Material> mapping)
        {
            var paths = animators.ObjectPathRemapper;
            foreach (var clip in animators.AnimationIndex.ClipsWithObjectCurves.ToList())
            {
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    if (!IsMaterialSlot(binding)) continue;
                    var target = paths.GetObjectForPath(binding.path);
                    if (target == null || !inScope(target.transform)) continue;
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
