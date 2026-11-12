// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// メッシュから UV0 と三角形を読む。読めないメッシュ(Read/Write 無効の扱いは Unity の版で違う)は例外で判定する。
    /// メッシュ探し(<see cref="MeshUvLocator"/>)と Scene のクリックの当たり判定(<see cref="SceneMeshPicker"/>)が
    /// 同じ読み方を別々に持っていたので、ここに 1 つ置く。
    /// </summary>
    internal static class MeshReading {
        internal enum Triangles {
            /// <summary>読めた。</summary>
            Ok,
            /// <summary>三角形でないサブメッシュ(線・点)。色を塗る面ではないので、読めなかった数には入れない。</summary>
            NotTriangles,
            /// <summary>三角形が 1 つも無い。</summary>
            Empty,
            /// <summary>読めない(Read/Write 無効など)。</summary>
            Unreadable,
        }

        /// <summary>UV0(頂点ごと)。読めない・UV が無いなら false。</summary>
        internal static bool TryReadUv0(Mesh mesh, out Vector2[] uv) {
            uv = null;
            try {
                var list = new List<Vector2>();
                mesh.GetUVs(0, list);
                if (list.Count == 0) {
                    return false;
                }
                uv = list.ToArray();
                return true;
            } catch (System.Exception) {
                return false;
            }
        }

        /// <summary>
        /// サブメッシュの三角形(頂点番号 ×3)。三角形のサブメッシュかを先に見る: 線・点のサブメッシュに GetTriangles を呼ぶと
        /// Unity が Console にエラーを出して空を返すので、以前のメッシュ探しは「読めない(Read/Write)」と誤って数えていた。
        /// </summary>
        internal static Triangles ReadTriangles(Mesh mesh, int submesh, out int[] triangles) {
            triangles = null;
            try {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) {
                    return Triangles.NotTriangles;
                }
                var tris = mesh.GetTriangles(submesh);
                if (tris == null || tris.Length < 3) {
                    return Triangles.Empty;
                }
                triangles = tris;
                return Triangles.Ok;
            } catch (System.Exception) {
                return Triangles.Unreadable;
            }
        }
    }
}
