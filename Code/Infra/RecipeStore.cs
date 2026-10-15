// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// レシピ(<see cref="IrocaRecipe"/>)の読み書きと検索。中身は編集状態
    /// (<see cref="IrocaSessionState"/>: ゾーン・処理設定・マスク)の JSON で、
    /// <see cref="SessionFileStore"/>(UserSettings、マスク抜き)と違いマスクまで丸ごと持つ。
    /// アバターと一緒に持ち運ぶ「正」はこちら。
    /// </summary>
    internal static class RecipeStore
    {
        /// <summary>新しく作るレシピの置き場所(既定)。元テクスチャのフォルダ(配布元の素材フォルダ)を汚さない。</summary>
        internal const string DefaultFolder = "Assets/Iroca/Recipes";

        /// <summary>
        /// レシピの中身を読む。空・壊れている・未対応の版なら null。
        /// 戻り値は毎回新しいオブジェクト(呼び出し側が自由に変更してよい)。
        /// </summary>
        public static IrocaSessionState Load(IrocaRecipe recipe)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.sessionJson)) return null;
            if (recipe.formatVersion > IrocaRecipe.CurrentFormatVersion)
            {
                Debug.LogWarning($"[Iroca] レシピ '{recipe.name}' はこの版のいろかより新しい形式です(形式 {recipe.formatVersion})。いろかを更新してください。");
                return null;
            }
            try
            {
                var state = JsonUtility.FromJson<IrocaSessionState>(recipe.sessionJson);
                if (state == null) return null;
                if (state.zones == null) state.zones = new List<ColorZone>();
                if (state.maskState == null) state.maskState = new MaskState();
                return state;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] レシピ '{recipe.name}' を読めませんでした: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 編集状態(マスクを含む全部)をレシピへ書く。内容が変わったときだけアセットを保存する。
        /// 変わったら true。マスクは呼び出し側で作業用バッファから state.maskState へ同期しておくこと。
        /// </summary>
        public static bool Save(IrocaRecipe recipe, IrocaSessionState state)
        {
            if (recipe == null || state == null) return false;
            string json = JsonUtility.ToJson(state);
            if (json == recipe.sessionJson && recipe.formatVersion == IrocaRecipe.CurrentFormatVersion)
                return false;
            // Undo には積まない(編集の Undo はウィンドウの編集状態側が持つ。自動保存のたびに
            // アセットの記録が積まれると、ウィンドウの Undo と二重になる)。
            recipe.sessionJson = json;
            recipe.formatVersion = IrocaRecipe.CurrentFormatVersion;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(recipe);
            return true;
        }

        /// <summary>指定テクスチャを元にするレシピを全部探す(パス順)。</summary>
        public static List<IrocaRecipe> FindForTexture(Texture2D texture)
        {
            var found = new List<IrocaRecipe>();
            if (texture == null) return found;
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(IrocaRecipe)))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var recipe = AssetDatabase.LoadAssetAtPath<IrocaRecipe>(path);
                if (recipe != null && recipe.sourceTexture == texture) found.Add(recipe);
            }
            return found;
        }

        /// <summary>
        /// 新しいレシピを作る(既定の置き場所は <see cref="DefaultFolder"/>)。名前は元テクスチャ名(重複したら連番)。
        /// </summary>
        public static IrocaRecipe Create(Texture2D texture, IrocaSessionState state, string folder = DefaultFolder)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            EnsureFolder(folder);
            string safe = PathUtils.SanitizeFileName(texture.name, "recipe");
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{safe}.asset");
            var recipe = ScriptableObject.CreateInstance<IrocaRecipe>();
            recipe.sourceTexture = texture;
            recipe.sessionJson = state != null ? JsonUtility.ToJson(state) : "";
            recipe.formatVersion = IrocaRecipe.CurrentFormatVersion;
            AssetDatabase.CreateAsset(recipe, path);
            AssetDatabase.SaveAssets();
            return recipe;
        }

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }
    }
}
