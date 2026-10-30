// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>編集の種類(<see cref="EditingSession.Changed"/>。購読する側が自分に関係するかを見分ける)。</summary>
    internal enum EditChange
    {
        /// <summary>ゾーン(色・許容範囲・並び・起点など)と全体の設定。</summary>
        Content,
        /// <summary>マスクの層(ブラシ・右クリックの範囲・AI 提案・マスクを消す)。</summary>
        Masks,
        /// <summary>編集状態をまるごと差し替えた・書き戻した(読み込み・リセット・Undo/Redo・プリセット・レシピの読み直し)。</summary>
        Replaced,
    }

    /// <summary>
    /// 編集中の文書(0.4.0 の計画の 4-2)。いろかのウィンドウ 1 つにつき 1 つ。
    /// 文書の中身(<see cref="IrocaEditingDocument"/>。ゾーン・設定・テクスチャ・レシピ。Undo の対象)を包み、原本の画素
    /// (<see cref="SourcePixelCache"/>)と編集中のマスク(<see cref="MaskWorkspace"/>)を持ち、ゾーンの操作を行い、
    /// 編集を <see cref="Changed"/> で知らせる。
    /// ウィンドウは読み込み・リセット・レシピの読み直しで編集状態をまるごと差し替え、Unity は Undo とドメインリロードで
    /// 書き戻すので、編集状態は覚えずに毎回文書の中身から読む。メインスレッド専用。
    /// </summary>
    internal sealed class EditingSession
    {
        internal EditingSession(IrocaEditingDocument document) => Document = document;

        /// <summary>文書の中身(操作を Undo に記録する先)。作り直されたらウィンドウが指し直す。</summary>
        internal IrocaEditingDocument Document { get; set; }

        /// <summary>今の編集状態。</summary>
        internal IrocaSessionState State => Document.session;

        /// <summary>
        /// 編集された(保存される中身が変わった)。購読するのは、非同期に動くもの(プレビューの作り直し・マスクの重ね表示・
        /// 自動保存)だけ。画面は IMGUI が毎フレーム状態を読む。表示だけの変更(ソロ表示・デバッグ表示・原本の読み直し)は
        /// 編集ではないので、ここを通さずにプレビューへ直接頼む。
        /// </summary>
        internal event Action<EditChange> Changed;

        /// <summary>編集を知らせる。ここの操作は自分で呼ぶ。IMGUI がその場で書いた値などは、書いた側が呼ぶ。</summary>
        internal void NotifyChanged(EditChange kind) => Changed?.Invoke(kind);

        /// <summary>
        /// 原本の画素(作業解像度)。読み直すかどうか(別のテクスチャ・取り込み後の寸法が変わった)と、原本から導いた
        /// 状態を捨てるのはプレビューが決める(PreviewView.EnsureTrueSource)。
        /// </summary>
        internal SourcePixelCache Sources { get; } = new SourcePixelCache();

        /// <summary>
        /// 編集中のマスク(ヒープの外)。ブラシ・範囲の操作はマスクの編集(MaskPaintView)が書く。Undo に載るのは版の番号だけで、
        /// 中身は <see cref="MaskHistory"/> が持つ。ドメインリロードの後は保存したファイルから読み直す。
        /// 手放すのはウィンドウを破棄するときだけ(保存より前に手放すと、空のマスクを保存してファイルを消す)。
        /// </summary>
        internal MaskWorkspace Masks { get; } = new MaskWorkspace();

        /// <summary>編集中のマスクの履歴。手放すのはウィンドウを破棄するとき。</summary>
        internal MaskHistory MaskHistory { get; } = new MaskHistory();

        // 編集中のマスクが、どの版の中身か。
        private long _appliedMaskRevision;

        /// <summary>
        /// マスクを書き換える操作の前に呼ぶ(Undo に記録する)。書き換えたら <see cref="CommitMaskEdit"/> を呼ぶ。
        /// 今の版が履歴に無ければ(マスクをまだ版にしていない・上限で手放した)、今のマスクをここで版にする。
        /// </summary>
        internal void RecordMaskEdit(string undoName)
        {
            if (!MaskHistory.Contains(Document.maskRevision)) CommitMaskEdit();
            Undo.RegisterCompleteObjectUndo(Document, undoName);
        }

        /// <summary>今のマスクを新しい版にして、文書の番号を進める。読み込みなど、マスクを置き換えたときも呼ぶ。</summary>
        internal void CommitMaskEdit()
        {
            _appliedMaskRevision = MaskHistory.Capture(Masks, Document.maskRevision);
            Document.maskRevision = _appliedMaskRevision;
        }

        /// <summary>
        /// Undo/Redo の後に呼ぶ。文書の番号が変わっていれば、編集中のマスクをその版へ戻す(番号が同じなら何もしない。スライダーの
        /// Undo でマスクに触らない)。その版を上限で手放していて戻せなければ false(マスクはそのまま)。
        /// </summary>
        internal bool RestoreMasksAfterUndo()
        {
            long revision = Document.maskRevision;
            if (revision == _appliedMaskRevision) return true;
            _appliedMaskRevision = revision;
            return MaskHistory.TryRestore(revision, Masks);
        }

        /// <summary>
        /// 保存に渡す編集状態(ゾーンと設定は今のものを指す写しで、マスクを保存形式にして付けたもの)。書き込み規則(全 false は
        /// 書かない)は Core の MaskStateCodec と同じ(MaskWorkspace.EncodeTo)。
        /// </summary>
        internal IrocaSessionState ToSaveState()
        {
            var masks = new MaskState();
            Masks.EncodeTo(masks);
            return State.WithMasks(masks);
        }

        internal ColorZone FindZoneById(string id)
        {
            var zones = State?.zones;
            if (string.IsNullOrEmpty(id) || zones == null) return null;
            foreach (var z in zones)
                if (z != null && z.id == id) return z;
            return null;
        }

        /// <summary>id 未設定のゾーンへ GUID を振る。</summary>
        internal void EnsureAllZoneIds()
        {
            var zones = State?.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
                zones[i]?.EnsureId();
        }

        // ─────────────── ゾーンの操作 ───────────────
        // どれも Undo に記録してから書き換え(1 回の操作 = Undo 1 ステップ)、変えたら Changed(Content) を出す。
        // 画面の追従(いま直しているゾーン・開閉)は呼ぶ側が行う。

        // 新しく作ったゾーンの許容範囲。
        //
        // ColorZone のフィールド既定は 0 で、これは「まだ何も指定していない」状態を表す値
        // （距離 0 の画素しか一致しない＝プレビューが一切変わらない）。自動調整で埋める前提の
        // 設計だったが、自動調整には AI モデルが要るため、未導入のユーザーはスポイトで色を
        // 取っても何も起きず「壊れている」ようにしか見えなかった（2026-09-11 の UX 見直し）。
        //
        // zones JSON 経路（MCP・batchmode）の既定と同じ値で、回帰テストのシナリオ既定（fixtures.ZoneSpec.tolerance）
        // とも一致する実績のある動作点。
        // ★ColorZone.cs 側の既定は変えない★ — あちらは JSON／プリセットから値が入る前提の
        // データ既定であり、ハーネス（回帰テスト・視覚ゲート）が読む唯一の正でもある。
        // ここで入れるのは「新しく作ったゾーンの初期値」だけ。
        internal const float NewZoneInitialTolerance = ZonesJsonDefaults.Tolerance;

        /// <summary>
        /// 新しいゾーン(リストには足さない)。名前を渡さなければ、既存と重ならない最小の番号の名前を振る
        /// (全ゾーンが同名だと、マスクの編集対象のプルダウンやドラッグ中のゴーストで見分けがつかなかった)。
        /// UI と自動化が同じ規則で作る。
        /// </summary>
        internal static ColorZone NewZone(List<ColorZone> zones, string name = null)
        {
            var zone = new ColorZone
            {
                name = string.IsNullOrEmpty(name) ? NextZoneName(zones) : name,
                // 「色を選べば何か変わる」状態から始められるようにする（定数のコメント参照）。
                tolerance = NewZoneInitialTolerance,
            };
            zone.EnsureId();
            return zone;
        }

        private static string NextZoneName(List<ColorZone> zones)
        {
            var used = new HashSet<string>();
            if (zones != null)
                foreach (var z in zones)
                    if (z != null && !string.IsNullOrEmpty(z.name)) used.Add(z.name);
            // ゾーンが n 個なら、1〜n+1 のどれかは空いている。
            for (int n = 1; ; n++)
            {
                string candidate = string.Format(Localization.NewZoneNameFormat, n);
                if (!used.Contains(candidate)) return candidate;
            }
        }

        /// <summary>新しいゾーンを末尾に足す。足した位置を返す。</summary>
        internal int AddZone()
        {
            var zones = State.zones;
            Undo.RegisterCompleteObjectUndo(Document, "Add Zone");
            zones.Add(NewZone(zones));
            NotifyChanged(EditChange.Content);
            return zones.Count - 1;
        }

        /// <summary>
        /// スポイトで取った色と位置から新しいゾーンを作って末尾に足す(Undo 1 ステップ。戻すとゾーンごと消える)。
        /// 足した位置を返す。
        /// </summary>
        internal int AddZoneFromSample(Color picked, Vector2 uv)
        {
            var zones = State.zones;
            Undo.RegisterCompleteObjectUndo(Document, "Add Zone");
            var zone = NewZone(zones);
            zone.sampleColor = picked;
            zone.sampleColorSet = true;
            // スポイトで取ったので位置も持つ（自動調整が AI 提案の証拠に使う。ApplySample と同じ）。
            zone.sampleUV = uv;
            zones.Add(zone);
            NotifyChanged(EditChange.Content);
            return zones.Count - 1;
        }

        /// <summary>
        /// ゾーンを消す。そのゾーンのマスク(除外・含める)も消す。Undo 1 ステップでゾーンとマスクがそろって戻る
        /// (戻したあとはウィンドウが履歴から戻す)。
        /// </summary>
        internal bool RemoveZoneAt(int index)
        {
            var zones = State?.zones;
            if (zones == null || index < 0 || index >= zones.Count) return false;
            RecordMaskEdit("Remove Zone");
            string id = zones[index]?.id;
            if (!string.IsNullOrEmpty(id)) Masks.RemoveZone(id);
            zones.RemoveAt(index);
            CommitMaskEdit();
            NotifyChanged(EditChange.Content);
            return true;
        }

        /// <summary>
        /// ゾーンを from から to(取り除いた後の挿入位置)へ移す。並び順が優先度。マスクはゾーンの id で引くので
        /// 動かさない。動かなければ(同じ位置・範囲外)false。
        /// </summary>
        internal bool MoveZone(int from, int to)
        {
            var zones = State?.zones;
            if (zones == null || from == to || from < 0 || from >= zones.Count || to < 0 || to >= zones.Count)
                return false;
            Undo.RegisterCompleteObjectUndo(Document, "Reorder Zone");
            var moved = zones[from];
            zones.RemoveAt(from);
            zones.Insert(to, moved);
            NotifyChanged(EditChange.Content);
            return true;
        }

        /// <summary>
        /// スポイトで取った色と位置をゾーンへ書く。色か位置のどちらかが変わるなら、書く前に
        /// Undo へ記録する（sampleUV もシリアライズ対象なので、Undo/Redo で色と位置が対で戻る）。
        /// 同じ色を別の場所で拾い直したときも位置は更新する（別の島をクリックしたかもしれない。
        /// 位置は自動調整が AI 提案の証拠を取るアンカー）。
        /// 色が変わったとき、または位置だけが変わって、その位置が選択に効く状態のとき（ColorZone.SelectionUsesSampleUV:
        /// 位置を含む連結成分は包絡ゲートで落とさない）だけ、編集を知らせて true を返す(出力が変わらない位置だけの
        /// 書き換えでプレビューを作り直さない。位置は次の保存で一緒に書かれる)。
        /// </summary>
        internal bool ApplySample(ColorZone zone, Color picked, Vector2 uv)
        {
            bool colorChanged = zone.sampleColor != picked || !zone.sampleColorSet;
            if (!colorChanged && zone.sampleUV == uv) return false;
            Undo.RecordObject(Document, "Sample Color");
            if (colorChanged)
            {
                zone.sampleColor = picked;
                zone.sampleColorSet = true;
                // 主サンプル変更で陳腐化する内部サンプルを破棄（ColorField と同じ挙動）。
                if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                    zone.extraSamples.Clear();
            }
            zone.sampleUV = uv;
            if (!colorChanged && !zone.SelectionUsesSampleUV) return false;
            NotifyChanged(EditChange.Content);
            return true;
        }

        /// <summary>連続領域の起点を置く。変わったときだけ記録して true。</summary>
        internal bool SetFloodFillSeed(ColorZone zone, Vector2 uv)
        {
            if (zone.seedUV == uv) return false;
            Undo.RecordObject(Document, "Set Flood Fill Seed");
            zone.seedUV = uv;
            NotifyChanged(EditChange.Content);
            return true;
        }

        /// <summary>連続領域の起点を外す。</summary>
        internal void ClearFloodFillSeed(ColorZone zone)
        {
            Undo.RecordObject(Document, "Clear Flood Fill Seed");
            zone.seedUV = new Vector2(-1f, -1f);
            NotifyChanged(EditChange.Content);
        }

        /// <summary>
        /// プリセットを読み込む(ゾーンと処理設定。<paramref name="applyMasks"/> ならマスクも。規則は <see cref="PresetApplier"/>)。
        /// Undo 1 ステップでゾーン・設定・マスクがそろって戻る。
        /// マスクを読み込まないときは、今のマスクを残す。マスクを読み込んだら true。
        /// </summary>
        internal bool ApplyPreset(IrocaPresetData preset, bool applyMasks)
        {
            RecordMaskEdit("Load Preset");
            PresetApplier.ApplyZonesAndSettings(preset, State);
            bool masks = applyMasks && PresetApplier.HasMasks(preset);
            if (masks)
            {
                Masks.DecodeFrom(PresetApplier.MaskStateOf(preset));
                CommitMaskEdit();
            }
            NotifyChanged(EditChange.Replaced);
            return masks;
        }

        /// <summary>ゾーンの詳細の調整値を既定へ戻す(色・許容範囲・名前などの利用者の選択は残す。ColorZone.ResetTuningToDefault)。</summary>
        internal void ResetZoneTuning(ColorZone zone)
        {
            Undo.RegisterCompleteObjectUndo(Document, "Reset Zone Tuning");
            zone.ResetTuningToDefault();
            NotifyChanged(EditChange.Content);
        }
    }
}
