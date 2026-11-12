// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// プリセット一覧 UI、保存 / 読込 / JSON 入出力を担当する。
    /// マスクペイント・プレビュー生成・エクスポートには関与しない。
    /// </summary>
    [System.Serializable]
    internal class PresetsView {
        public bool presetsFoldout;
        public string presetSaveName = "Preset";
        public bool presetStorageProject = true;
        public bool presetIncludeMasks = true;
        public bool presetApplyMasks = true;
        // マスクオプションと JSON 入出力をまとめる低頻度操作の折りたたみ（既定で閉じる）。
        public bool presetAdvancedFoldout;

        [System.NonSerialized] private IrocaWindow _host;

        // プリセットの一覧の控え。欄が開いている間、OnGUI のイベントごと(マウスが動くたび)にフォルダを列挙していた。
        // 読み直すのは、保存・削除・取り込み・保存先の切り替えの直後と、Layout で前に読んでから ListRefreshSeconds 経ったとき
        // (ほかの道具が置いたプリセットは、その分だけ遅れて見える)。Layout でだけ読み直すので、同じフレームの
        // Layout と Repaint で一覧の行の数が食い違わない。
        //
        // 一覧は保存先ごとに分けて両方出す(2026-11-12)。以前は保存先の切り替えが「一覧に出すもの」も兼ねていて、
        // 反対側に保存したプリセットが見えず、2 つが何の違いなのかも分かりにくかった。
        // 並びは、このテクスチャで作ったものが先(_projectForTexture などがその印。読み直すときに一緒に決める)。
        [System.NonSerialized] private string[] _projectFiles;
        [System.NonSerialized] private string[] _userFiles;
        [System.NonSerialized] private bool[] _projectForTexture;
        [System.NonSerialized] private bool[] _userForTexture;
        [System.NonSerialized] private string _listedForGuid;
        [System.NonSerialized] private double _listedAt;
        private const double ListRefreshSeconds = 2.0;

        // ファイルごとの「作ったテクスチャ」(頭だけ読む。更新日時が変わったら読み直す)。
        private struct SourceGuidEntry {
            internal System.DateTime writtenAt;
            internal string guid;
        }
        [System.NonSerialized] private System.Collections.Generic.Dictionary<string, SourceGuidEntry> _sourceGuids;

        private string ActivePresetFolder
            => presetStorageProject ? PresetStore.ProjectPresetFolder : PresetStore.UserPresetFolder;

        private void InvalidatePresetList() {
            _projectFiles = null;
            _userFiles = null;
        }

        /// <summary>
        /// 一覧を読み直す。保存・削除などの直後と、Layout のときに、テクスチャが替わっていたか前に読んでから
        /// ListRefreshSeconds 経っていたとき。時間とテクスチャでは Layout でだけ読み直すので、同じイベントの
        /// Layout とその後の描画で行の数が食い違わない(テクスチャは欄の操作のイベントの途中で替わりうる)。
        /// </summary>
        private void RefreshPresetLists() {
            double now = EditorApplication.timeSinceStartup;
            bool layout = Event.current != null && Event.current.type == EventType.Layout;
            string textureGuid = layout || _projectFiles == null
                ? SessionPersistence.GuidOf(_host != null ? _host.SourceTexture : null)
                : _listedForGuid;
            bool stale = _projectFiles == null || _userFiles == null
                || (layout && (_listedForGuid != textureGuid || now - _listedAt >= ListRefreshSeconds));
            if (!stale) {
                return;
            }
            _listedAt = now;
            _listedForGuid = textureGuid;
            SortForTexture(PresetStore.ListJson(PresetStore.ProjectPresetFolder), textureGuid,
                out _projectFiles, out _projectForTexture);
            SortForTexture(PresetStore.ListJson(PresetStore.UserPresetFolder), textureGuid,
                out _userFiles, out _userForTexture);
        }

        // このテクスチャで作ったものを先に、それぞれの中は名前の順に並べる。
        private void SortForTexture(string[] files, string textureGuid, out string[] sorted, out bool[] forTexture) {
            var keys = new bool[files.Length];
            for (int i = 0; i < files.Length; i++) {
                keys[i] = MadeForTexture(files[i], textureGuid);
            }
            var order = new int[files.Length];
            for (int i = 0; i < order.Length; i++) {
                order[i] = i;
            }
            System.Array.Sort(order, (a, b) => keys[a] != keys[b]
                ? (keys[a] ? -1 : 1)
                : string.Compare(Path.GetFileNameWithoutExtension(files[a]), Path.GetFileNameWithoutExtension(files[b]),
                    System.StringComparison.CurrentCultureIgnoreCase));
            sorted = new string[files.Length];
            forTexture = new bool[files.Length];
            for (int i = 0; i < order.Length; i++) {
                sorted[i] = files[order[i]];
                forTexture[i] = keys[order[i]];
            }
        }

        /// <summary>このプリセットを、いま開いているテクスチャで作ったか(マスク・起点の位置がそのまま合う)。</summary>
        private bool MadeForTexture(string file, string textureGuid) {
            if (string.IsNullOrEmpty(textureGuid)) {
                return false;
            }
            System.DateTime writtenAt;
            try {
                writtenAt = File.GetLastWriteTimeUtc(file);
            } catch (System.Exception) {
                return false;
            }
            _sourceGuids ??= new System.Collections.Generic.Dictionary<string, SourceGuidEntry>();
            if (!_sourceGuids.TryGetValue(file, out var entry) || entry.writtenAt != writtenAt) {
                entry = new SourceGuidEntry { writtenAt = writtenAt, guid = PresetStore.ReadSourceTextureGuid(file) };
                _sourceGuids[file] = entry;
            }
            return entry.guid == textureGuid;
        }

        public void Initialize(IrocaWindow host) {
            _host = host;
        }

        // ゾーンが 1 つも無いときは、プリセットを読むのがいちばん多い場面なので、閉じていても開いて見せる(2026-11-12
        // ユーザー要望)。ただし、保存したプリセットがあり、開いても設定列の見えている高さに収まるときだけ(縦幅は
        // プレビューと取り合うので、収まらないなら畳んだまま。ゾーンのカードと同じ考え方)。開閉の保存値 presetsFoldout は
        // 書き換えないので、ゾーンを作れば利用者が選んだ開閉に戻る。見せている間に閉じたら、このウィンドウを開いている
        // 間はもう自動では開かない。決めるのは Layout のときだけ(同じイベントの描画で行の数が食い違わないように)。
        [System.NonSerialized] private bool _autoOpen;
        [System.NonSerialized] private bool _autoOpenDismissed;
        // この欄を最後に描いた高さ(前の Repaint のもの)と、開いて描いたときの高さ。
        [System.NonSerialized] private float _shownHeight;
        [System.NonSerialized] private float _openHeight;

        private bool DecideAutoOpen() {
            if (presetsFoldout || _autoOpenDismissed || _host == null || _host.SourceTexture == null) {
                return false;
            }
            var zones = _host.Session?.zones;
            if (zones == null || zones.Count > 0) {
                return false;
            }
            RefreshPresetLists();
            if (_projectFiles.Length == 0 && _userFiles.Length == 0) {
                return false;   // 開いても「ありません」の枠が縦幅を取るだけ
            }
            float visible = _host.SettingsVisibleHeight;
            float contentEnd = _host.SettingsContentEnd;
            if (visible <= 0f || contentEnd <= 0f || _shownHeight <= 0f) {
                return false;   // まだ測っていない(次の描き直しで決める)
            }
            float open = _openHeight > 0f ? _openHeight : EstimateOpenHeight();
            return contentEnd - _shownHeight + open <= visible;
        }

        // まだ開いて描いていないときの見積もり(見出し・保存・保存先・詳細・一覧の見出し 2 つと行)。
        private float EstimateOpenHeight() {
            float line = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return line * (6 + _projectFiles.Length + _userFiles.Length) + 8f;
        }

        public void Draw() {
            if (Event.current.type == EventType.Layout) {
                _autoOpen = DecideAutoOpen();
            }
            float startY = GUILayoutUtility.GetRect(0f, 0f).y;

            bool shownOpen = presetsFoldout || _autoOpen;
            bool open = EditorGUILayout.BeginFoldoutHeaderGroup(shownOpen,
                TempContent.Of(Localization.Presets, Localization.PresetsTooltip));
            if (open != shownOpen) {
                presetsFoldout = open;
                if (!open && _autoOpen) {
                    _autoOpenDismissed = true;
                }
            }
            if (open && !shownOpen) {
                InvalidatePresetList();   // 閉じている間に増えた・消えたプリセットを、開いたときに読み直す
            }
            if (open) {
                DrawBody();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            if (open) {
                EditorGUILayout.Space(4);
            }

            float endY = GUILayoutUtility.GetRect(0f, 0f).y;
            if (Event.current.type == EventType.Repaint) {
                _shownHeight = endY - startY;
                if (open) {
                    _openHeight = _shownHeight;
                }
            }
        }

        private void DrawBody() {

            // 使い方の説明は各コントロールのツールチップに委ね、常時表示の HelpBox は置かない。

            EditorGUILayout.BeginHorizontal();
            presetSaveName = EditorGUILayout.TextField(
                TempContent.Of(Localization.PresetName, Localization.PresetNameTooltip),
                presetSaveName);
            if (GUILayout.Button(TempContent.Of(Localization.SavePreset, Localization.SavePresetTooltip), GUILayout.Width(IrocaUiConsts.Layout.SmallButtonWidth))) {
                SavePreset(presetSaveName);
            }
            EditorGUILayout.EndHorizontal();

            // 保存先は保存の行のすぐ下に置く(一覧は保存先ごとに分けて両方出すので、これは保存だけに効く)。
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(TempContent.Of(Localization.PresetSaveDestination, Localization.PresetSaveDestinationTooltip));
            if (GUILayout.Toggle(presetStorageProject, TempContent.Of(Localization.PresetStorageProject, Localization.PresetStorageProjectTooltip), EditorStyles.miniButtonLeft) && !presetStorageProject) {
                presetStorageProject = true;
            }
            if (GUILayout.Toggle(!presetStorageProject, TempContent.Of(Localization.PresetStorageUser, Localization.PresetStorageUserTooltip), EditorStyles.miniButtonRight) && presetStorageProject) {
                presetStorageProject = false;
            }
            EditorGUILayout.EndHorizontal();

            // 低頻度のオプション（マスク同梱/適用・JSON 入出力）は折りたたみへ。
            presetAdvancedFoldout = EditorGUILayout.Foldout(presetAdvancedFoldout,
                TempContent.Of(Localization.PresetAdvanced, Localization.PresetAdvancedTooltip), true);
            if (presetAdvancedFoldout) {
                using (new EditorGUI.IndentLevelScope()) {
                    presetIncludeMasks = EditorGUILayout.ToggleLeft(
                        TempContent.Of(Localization.PresetIncludeMasks, Localization.PresetIncludeMasksTooltip),
                        presetIncludeMasks);
                    presetApplyMasks = EditorGUILayout.ToggleLeft(
                        TempContent.Of(Localization.PresetApplyMasks, Localization.PresetApplyMasksTooltip),
                        presetApplyMasks);

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(TempContent.Of(Localization.ExportJson, Localization.ExportJsonTooltip))) {
                        ExportPresetJson();
                    }
                    if (GUILayout.Button(TempContent.Of(Localization.ImportJson, Localization.ImportJsonTooltip))) {
                        ImportPresetJson();
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            RefreshPresetLists();
            // 行の操作(削除)で一覧が読み直されても、このイベントの残りは読む前の配列で描く(行の数を変えない)。
            string[] projectFiles = _projectFiles, userFiles = _userFiles;
            bool[] projectForTexture = _projectForTexture, userForTexture = _userForTexture;

            if (projectFiles.Length == 0 && userFiles.Length == 0) {
                EditorGUILayout.HelpBox(Localization.NoPresets, MessageType.None);
            } else {
                DrawPresetGroup(Localization.PresetGroupProject, Localization.PresetGroupProjectTooltip,
                    projectFiles, projectForTexture);
                DrawPresetGroup(Localization.PresetGroupUser, Localization.PresetGroupUserTooltip,
                    userFiles, userForTexture);
            }
        }

        /// <summary>保存先 1 つ分の一覧(見出しと行)。空なら見出しの行に「なし」とだけ出す。</summary>
        private void DrawPresetGroup(string title, string tooltip, string[] files, bool[] forTexture) {
            EditorGUILayout.Space(2);
            if (files.Length == 0) {
                EditorGUILayout.LabelField(TempContent.Of(title, tooltip),
                    TempContent.Of(Localization.PresetGroupEmpty, tooltip), EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField(TempContent.Of(title, tooltip), EditorStyles.miniBoldLabel);
            for (int i = 0; i < files.Length; i++) {
                string file = files[i];
                string pname = Path.GetFileNameWithoutExtension(file);
                EditorGUILayout.BeginHorizontal();
                // MinWidth(0): 長い名前で行が設定列より広くなって、列全体の右端が切れないように(名前のほうを欠けさせる)。
                EditorGUILayout.LabelField(TempContent.Of(pname, pname), GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
                if (forTexture[i]) {
                    var mark = TempContent.Of(Localization.PresetForThisTexture, Localization.PresetForThisTextureTooltip);
                    GUILayout.Label(mark, EditorStyles.miniLabel,
                        GUILayout.Width(EditorStyles.miniLabel.CalcSize(mark).x));
                }
                if (GUILayout.Button(TempContent.Of(Localization.LoadPreset, Localization.LoadPresetTooltip),
                        GUILayout.Width(IrocaWindow.FitButtonWidth(40f, Localization.LoadPreset)))) {
                    LoadPreset(file);
                }
                if (GUILayout.Button(TempContent.Of("×", Localization.DeletePresetTooltip), GUILayout.Width(IrocaUiConsts.Layout.RemoveButtonWidth))) {
                    if (EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.DeletePresetConfirm(pname), Localization.OK, Localization.Cancel)) {
                        bool ok = PresetStore.Delete(file);
                        InvalidatePresetList();
                        Notify(ok ? Localization.PresetDeleted : $"{Localization.Error}: {Localization.PresetDeleteFailed}");
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void SavePreset(string name) {
            string existingPath = PresetStore.PresetFilePath(ActivePresetFolder, name);
            if (File.Exists(existingPath)) {
                if (!EditorUtility.DisplayDialog(
                        Localization.Confirm,
                        Localization.PresetOverwriteConfirm(Path.GetFileNameWithoutExtension(existingPath)),
                        Localization.Overwrite,
                        Localization.Cancel)) {
                    return;
                }
            }

            var data = BuildPresetData(name);
            bool ok = presetStorageProject
                ? PresetStore.SaveToProject(name, data)
                : PresetStore.SaveToUser(name, data);
            InvalidatePresetList();
            Notify(ok ? Localization.PresetSaved : $"{Localization.Error}: {Localization.PresetSaveFailed}");
        }

        // ウィンドウ右下に非モーダル通知を出す（EditorUtility.DisplayDialog は Editor 全体を
        // ブロックするため、結果通知には ShowNotification を使う。ExportView.NotifyError と同型）。
        private void Notify(string message) {
            _host?.ShowNotification(new GUIContent(message));
        }

        private IrocaPresetData BuildPresetData(string presetName) {
            _host.EnsureAllZoneIds();
            var session = _host.Session;

            // 複製して書く（id は引き継ぐ）。スポイト位置はテクスチャ固有なので複製からだけ外す。
            var zonesCopy = IrocaPresetData.WithoutSamplePositions(session.zones);

            var data = new IrocaPresetData {
                name = presetName,
                sourceTextureGuid = SessionPersistence.GuidOf(_host.SourceTexture),
                zones = zonesCopy,
            };
            // 優先度は並び順(古い layerIndex を残すと、読み込みの移行が保存した並びを戻す)。
            foreach (var z in zonesCopy) {
                if (z != null) {
                    z.layerIndex = 0;
                }
            }
            // 編集モード(かんたん/通常/上級)は UI 表示の好みであり色設定ではないため、
            // プリセットには保存しない（読込で表示モードが勝手に変わらないように）。
            RecolorSettings.From(session).CopyTo(data);

            if (presetIncludeMasks) {
                _host.WriteMaskToPreset(data);
            }

            return data;
        }

        // テストが確認の画面を出さずに答える(0 = マスクは読まない、1 = やめる、2 = マスクも読む。画面の戻り値と同じ)。
        internal static System.Func<int> ForeignMaskChoiceForTests;

        // internal は OldFormatLoadTests が古い版のプリセットを読み込むため。
        internal void LoadPreset(string filePath) {
            var data = PresetStore.Load(filePath);
            if (data == null) {
                Notify($"{Localization.Error}: {Localization.PresetLoadFailed}");
                return;
            }

            // 別のテクスチャで作ったプリセットのマスクは、このテクスチャの場所に合わない。今のマスクを置き換えてよいか確かめる。
            bool applyMasks = presetApplyMasks;
            if (applyMasks && PresetApplier.HasMasks(data)
                && PresetApplier.MadeForOtherTexture(data, SessionPersistence.GuidOf(_host.SourceTexture))) {
                int choice = ForeignMaskChoiceForTests?.Invoke() ?? EditorUtility.DisplayDialogComplex(Localization.Confirm,
                    Localization.PresetForeignMaskConfirm, Localization.PresetForeignMaskSkip, Localization.Cancel, Localization.PresetForeignMaskApply);
                if (choice == 1) {
                    return;
                }
                applyMasks = choice == 2;
            }

            // ゾーン・処理設定・(選んでいれば)マスクを 1 ステップの Undo で読み込む(EditingSession.ApplyPreset)。
            // 編集モードはプリセットで上書きしない（現在の表示モードを維持）。
            if (_host.Editing.ApplyPreset(data, applyMasks)) {
                _host.SaveMasksToFile();
            }

            _host.ResetActiveMaskTarget();
            Notify(Localization.PresetLoaded);
        }

        private void ExportPresetJson() {
            string path = EditorUtility.SaveFilePanel(
                Localization.ExportJson, "", "Iroca_preset", "json");
            if (string.IsNullOrEmpty(path)) {
                return;
            }
            var data = BuildPresetData(Path.GetFileNameWithoutExtension(path));
            bool ok = PresetStore.SaveToPath(path, data);
            InvalidatePresetList();   // 書き出し先がプリセットのフォルダのこともある
            Notify(ok ? Localization.PresetSaved : $"{Localization.Error}: {Localization.PresetSaveFailed}");
        }

        private void ImportPresetJson() {
            string path = EditorUtility.OpenFilePanel(
                Localization.ImportJson, "", "json");
            if (string.IsNullOrEmpty(path)) {
                return;
            }
            LoadPreset(path);
        }
    }
}
