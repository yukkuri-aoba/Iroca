// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.Collections;
using UnityEngine;

namespace Iroca.SentisIntegration {
    /// <summary>
    /// GPU の推論カーネル(Sentis のコンピュートシェーダ)を、1 回の MoveNext で 1 つずつコンパイルさせる。
    /// <see cref="EditorIteratorPump"/> で、GPU のエンコーダの前に回す。
    ///
    /// なぜ要るか: Sentis は GPU で最初の層を実行するときに、全カーネルの表(ComputeFunctions の静的初期化)を作り、
    /// そこで全カーネル(2.1.3 で 329 個)の GetKernelThreadGroupSizes を呼ぶ。エディタではこれがカーネルのコンパイルを
    /// その場で起こす(1 つ 25〜130ms)。カーネルのコンパイルの結果はプロジェクトの Library/ShaderCache に残るので、
    /// 重いのはプロジェクトで初めて GPU の推論を使うとき(Sentis・Unity を入れ替えたとき、Library を消したときも)だけだが、
    /// そのときは 1 回の MoveNext(=エディタの更新 1 回)が 8〜15 秒止まっていた。先に 1 つずつコンパイルさせておけば、
    /// 静的初期化はコンパイル済みのものを引くだけになり、止まりはカーネル 1 つ分に割れる。
    ///
    /// カーネルの名前は使わない(Sentis の内部の一覧を写さない)。Resources の "Sentis" の下のコンピュートシェーダを、
    /// カーネルの番号 0, 1, 2, … の順に、範囲の外で例外が出るまで回す。Sentis が使わないカーネルも少し含む(2.1.3 で 3 個)。
    /// コンパイル済みなら 1 つ数μs なので、2 回目からは(ドメインの再読み込みの後も)更新 1 回で終わる。
    /// </summary>
    internal sealed class SentisKernelPrewarm : IEnumerator {
        // Sentis(com.unity.sentis 2.x・com.unity.ai.inference 2.x)がコンピュートシェーダを読む Resources の下の場所。
        const string ResourcesFolder = "Sentis";
        // 1 つのシェーダの中のカーネルの数の上限(範囲の外の例外が出ないときの安全弁。Sentis の最大は 60 前後)。
        const int MaxKernelsPerShader = 4096;

        ComputeShader[] _shaders;
        int _shader;
        int _kernel;
        readonly Action<float> _onProgress;

        /// <param name="onProgress">シェーダを 1 つ終えるごとの進み(0〜1。終えたシェーダの割合。null = 知らせない)。</param>
        public SentisKernelPrewarm(Action<float> onProgress = null) {
            _onProgress = onProgress;
        }

        /// <summary>コンパイルさせた(または引いた)カーネルの数。</summary>
        public int Kernels { get; private set; }

        public object Current => null;

        public void Reset() => throw new NotSupportedException();

        public bool MoveNext() {
            if (_shaders == null) {
                _shaders = Resources.LoadAll<ComputeShader>(ResourcesFolder) ?? Array.Empty<ComputeShader>();
                return _shaders.Length > 0;
            }
            while (_shader < _shaders.Length) {
                var cs = _shaders[_shader];
                if (cs != null && _kernel < MaxKernelsPerShader) {
                    try {
                        // エディタでは、まだならここでカーネルをコンパイルする。範囲の外は IndexOutOfRangeException。
                        cs.GetKernelThreadGroupSizes(_kernel, out _, out _, out _);
                        _kernel++;
                        Kernels++;
                        return true;
                    } catch (Exception) {
                        // 範囲の外(このシェーダは終わり)。ほかの理由で失敗したカーネルも、ここでは飛ばす
                        // (推論で使えば、推論の側が普段どおりの失敗の道で扱う)。
                    }
                }
                _shader++;
                _kernel = 0;
                _onProgress?.Invoke(_shader / (float)_shaders.Length);
            }
            return false;
        }
    }
}
#endif
