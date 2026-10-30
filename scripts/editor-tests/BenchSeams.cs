// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Reflection;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 計測（MemoryBenchTests・HiResSmokeTests）が名前で探す製品のメンバーの一覧。
    /// 計測は同じテストを基準のコミット（メンバーが private のまま・まだ無い版）にも当てて比べるので、直接は呼ばず
    /// 名前で探す。名前を変えると計測を回したときに初めて落ちるので、ふだんの EditMode テスト（<see cref="BenchSeamTests"/>）
    /// で、今の製品にその名前があることを確かめる。製品のメンバーを移す・名前を変えるときは、ここも同じコミットで直す。
    /// </summary>
    internal static class BenchSeams
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // zones JSON を製品（IrocaAutomation.RecolorWithZones）と同じ変換でゾーンと設定にする。
        internal static MethodInfo BuildZone => typeof(IrocaAutomation).GetMethod("BuildZone", Static);
        internal static MethodInfo SettingsFromDto => typeof(IrocaAutomation).GetMethod("SettingsFromDto", Static);

        // ウィンドウの場面: プレビューを作り直し、概要段と確定段のジョブが終わるのを待ち、確定段の出力を読む。
        internal static MethodInfo GeneratePreviewAsync => typeof(PreviewView).GetMethod("GeneratePreviewAsync", Instance);
        // ジョブと確定段の出力の持ち主。0.4.0 からはプレビューの計算(PreviewView._pipeline)、それより前の版は PreviewView。
        internal static FieldInfo Pipeline => typeof(PreviewView).GetField("_pipeline", Instance);
        internal static object JobsOwnerOf(object preview) => Pipeline != null ? Pipeline.GetValue(preview) : preview;
        private static Type JobsOwnerType => Pipeline != null ? Pipeline.FieldType : typeof(PreviewView);
        internal static FieldInfo ProxyJob => JobsOwnerType.GetField("_proxyJob", Instance);
        internal static FieldInfo FullJob => JobsOwnerType.GetField("_previewJob", Instance);
        internal static FieldInfo FullOutput => JobsOwnerType.GetField("_fullOutput", Instance);
        internal static bool IsRunning(object job) => (bool)job.GetType().GetProperty("IsRunning").GetValue(job);

        // 0.3.0 から。基準の版に無ければ、計測は代わりの手で測る（無いことを許す）。
        internal static PropertyInfo NativeLiveBytes =>
            typeof(PixelProcessor).Assembly.GetType("Iroca.IrocaNativeMemory")?.GetProperty("LiveBytes", Static);
        internal static MethodInfo ReleaseWorkBuffers =>
            typeof(PixelProcessor).GetMethod("ReleaseWorkBuffers", Static, null, Type.EmptyTypes, null);
        // 確定段の出力（0.3.0 から NativePixels）の確保した領域。Mono の反射はポインタ型のプロパティを読めないので IntPtr を読む。
        internal static FieldInfo NativePixelsMemory(Type outputType) => outputType.GetField("_mem", Instance);
    }

    /// <summary>
    /// <see cref="BenchSeams"/> の名前が今の製品にあるか。計測は環境変数が無いと回らないので、ふだんのテストで先に気づく。
    /// </summary>
    public class BenchSeamTests
    {
        [Test]
        public void BenchSeams_ExistInCurrentProduct()
        {
            Assert.IsNotNull(BenchSeams.BuildZone, "IrocaAutomation.BuildZone");
            Assert.IsNotNull(BenchSeams.SettingsFromDto, "IrocaAutomation.SettingsFromDto");
            Assert.IsNotNull(BenchSeams.GeneratePreviewAsync, "PreviewView.GeneratePreviewAsync");
            Assert.IsNotNull(BenchSeams.Pipeline, "PreviewView._pipeline");
            Assert.IsNotNull(BenchSeams.ProxyJob, "PreviewPipeline._proxyJob");
            Assert.IsNotNull(BenchSeams.FullJob, "PreviewPipeline._previewJob");
            Assert.IsNotNull(BenchSeams.FullOutput, "PreviewPipeline._fullOutput");
            Assert.IsNotNull(BenchSeams.ProxyJob.FieldType.GetProperty("IsRunning"), "PreviewPipeline._proxyJob の IsRunning");
            Assert.IsNotNull(BenchSeams.FullJob.FieldType.GetProperty("IsRunning"), "PreviewPipeline._previewJob の IsRunning");
            Assert.IsNotNull(BenchSeams.NativeLiveBytes, "IrocaNativeMemory.LiveBytes");
            Assert.IsNotNull(BenchSeams.ReleaseWorkBuffers, "PixelProcessor.ReleaseWorkBuffers");
            Assert.IsNotNull(BenchSeams.NativePixelsMemory(BenchSeams.FullOutput.FieldType), "確定段の出力の _mem");
            Assert.IsNotNull(BenchSeams.FullOutput.FieldType.GetProperty("Length"), "確定段の出力の Length");
        }
    }
}
