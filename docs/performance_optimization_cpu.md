# パフォーマンス改善案（CPU・メモリ最適化）

現在のコードベース（特に `VACCWindow.Processing.cs` と `ColorZone.cs`）において、GPUを利用しない前提でパフォーマンスを劇的に向上できる改善点をまとめました。主に**メモリ確保（GCスパイクの抑制）**と**ピクセル単位の重い演算の削減**が中心となります。

## 1. ループ内での冗長な計算の排除（最優先・即効性あり）
`VACCWindow.Processing.cs` の `RecolorPixel` メソッドでは、引数として渡される `target` と `sample` の色をピクセルごとに `Color.RGBToHSV` で変換しています。

```csharp
// 現在のコード (RecolorPixel内)
Color.RGBToHSV(target,   out tH, out tS, out tV);
Color.RGBToHSV(sample,   out sH, out sS, out sV);
```

**【改善案】**
これらは現在のゾーン処理において**常に一定の定数**です。数百万ピクセルすべてでこの重いHSV変換を毎回行うのは非常に無駄が大きいため、ピクセル処理ループ（`Parallel.For`）の外側で事前に一度だけ計算し、結果のHSV値を引数として渡すように変更します。これにより計算量が大幅に削減されます。

## 2. メモリアロケーションの削減（ArrayPoolの活用）
処理が走るたびに、巨大な配列が大量に新規確保（`new`）されています。

- `ProcessPixelsArray` 内: `new Color32[len]`, `new float[len]`
- `DecontaminateAaBoundary` 内: `new float[len]` が 4つ、`new bool[len]`、`new Color32[len]`
- `BoxFilterSum` 内: `new float[w * h]` が 2つ

4Kテクスチャ（約1600万要素）などを処理する場合、毎回数百MBのメモリ確保と破棄が発生し、深刻なガベージコレクション（GC）スパイク（プチフリーズ）を引き起こします。

**【改善案】**
`new []` の代わりに **`System.Buffers.ArrayPool<T>.Shared.Rent(len)`** を使ってバッファをプールから借り、最後に `Return` で返すようにします。これにより、メモリ確保のコストとGCの発生をほぼゼロに抑えることができます。

## 3. C# Job System と Burst コンパイラの導入
GPUを使わない場合における最高の最適化手段です。

**【改善案】**
現在 `Parallel.For` を使ってマルチスレッド化されていますが、これを Unity の **C# Job System (`IJobParallelFor`)** と **Burst Compiler** に置き換えることを強く推奨します。
Burstコンパイラを有効にすると、コードがC++並みのネイティブコードに最適化され、**SIMD（ベクトル化）** が自動的に効くため、現在の `Parallel.For` からさらに数倍〜十数倍の高速化が期待できます。

## 4. 重い数学演算と型変換の最適化
ピクセルごとの処理ループ内には、CPUコストの高い処理がいくつか含まれています。

**【改善案】**
- **暗黙のキャストの排除:** `Color original = originalPixels[i];` のような代入では、0〜255のバイト値（`Color32`）を `0.0f〜1.0f` の浮動小数点（`Color`）に変換する割り算が各チャンネルで発生します。この変換を最小限にするか、必要なチャンネルのみを取り出すようにします。
- **`Mathf.Sqrt` の回避:** `ColorZone.cs` の `CalculateHybridDistance` などでピクセルごとに平方根計算が走っています。厳密な距離計算が不要であれば、平方根をとる前の「距離の二乗値（Squared Distance）」のままで閾値（閾値も二乗しておく）と比較するようにし、処理を軽量化します。
- **軽量なHSV変換:** Unity標準の `Color.RGBToHSV` 呼び出しは内部処理が重めです。独自の軽量なインライン関数（分岐を減らした簡易計算）を定義するか、テクスチャ全体をループの最初に一度だけ HSV の配列に一括変換してしまうアプローチが効果的です。

## 5. 不要な `Array.Copy` の削減
`FillSmallHoles` や `RecoverBoundaryEdges` の処理で、パス（Pass）ごとに毎回 `System.Array.Copy(read, write, read.Length);` が呼ばれています。

**【改善案】**
巨大な配列のフルコピーはメモリ帯域を圧迫します。「前回から変更があったインデックス」だけを記録して差分のみを上書きコピーする仕組みにするか、そもそも書き換え対象以外の領域をコピーしなくて済むようにダブルバッファのポインタ操作（スワップ）のみで完結させるロジックに工夫することで、メモリアクセスのオーバーヘッドを削減できます。
