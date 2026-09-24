"""再着色の全体設定（RecolorSettings）が、それを持ち運ぶ全ての型と食い違っていないことを検査する。

全体設定（edgeFeather / antiAliasCleanup / ... / decontaminationRadius）は次の場所に同じ名前で現れる:

  Code/Core/RecolorSettings.cs          値スナップショット。ProcessPixelsArray の唯一の入口
  Code/Core/IrocaSessionState.cs        UI の編集状態（Undo / セッション保存）
  Code/Core/IrocaPresetData.cs          プリセット JSON
  Code/Automation/IrocaAutomation.cs    zones JSON の SettingsDto（MCP・batchmode）
  Code/Debug/ReproDump.cs               現場の再現データ書き出し（SettingsDto へ写す）

以前はプレビュー・エクスポート・Automation・ハーネスの各呼び出し元が 8 個の値を位置引数で
並べ直しており、既定値のリテラルも 4 箇所に散っていた。どれも float/int なので取り違えても
コンパイルが通る。RecolorSettings に寄せたうえで、ここで次を機械検査する（C# ソースを読むだけ）:

  1. 各型の設定フィールド集合が RecolorSettings と一致する（設定の片側追加を検出）
  2. 既定値はリテラルでなく RecolorSettings.Default* を参照する
  3. RecolorSettings のコンストラクタ・From・CopyTo が全フィールドを扱う
  4. PixelProcessor.ProcessPixelsArray の非 private な入口は RecolorSettings を取る 1 つだけ
  5. SettingsDto を組み立てる ReproDump が全フィールドを書く
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
CORE = REPO_ROOT / "Code" / "Core"
SETTINGS_CS = CORE / "RecolorSettings.cs"
SESSION_CS = CORE / "IrocaSessionState.cs"
PRESET_CS = CORE / "IrocaPresetData.cs"
JSON_DEFAULTS_CS = CORE / "ZonesJsonDefaults.cs"
AUTOMATION_CS = REPO_ROOT / "Code" / "Automation" / "IrocaAutomation.cs"
REPRO_CS = REPO_ROOT / "Code" / "Debug" / "ReproDump.cs"

# 設定ではないフィールド（ゾーン・マスク・表示モード・スキーマ情報）。ここに足すときは理由を書く。
SESSION_NON_SETTINGS = {"zones", "editMode", "maskState"}
PRESET_NON_SETTINGS = {
    "schemaVersion", "name", "zones", "advancedMode",
    "maskWidth", "maskHeight", "commonMaskBase64", "zoneMasks", "zoneIncludeMasks",
}

_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/", re.DOTALL)


def _src(path: Path) -> str:
    return _COMMENT.sub("", path.read_text(encoding="utf-8"))


def _class_body(src: str, name: str) -> str:
    m = re.search(rf"\b(?:class|struct)\s+{name}\b[^{{]*\{{", src)
    assert m, f"{name} が見つかりません"
    depth, i = 1, m.end()
    while depth:
        c = src[i]
        depth += (c == "{") - (c == "}")
        i += 1
    return src[m.end():i - 1]


def _top_level(body: str) -> str:
    """クラス本体のうちメンバ宣言の階層だけを残す（メソッド本体などネストした {} を落とす）。"""
    out, depth = [], 0
    for c in body:
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif depth == 0:
            out.append(c)
    return "".join(out)


# public なインスタンスフィールド宣言: 名前と初期化子（無ければ None）。
_FIELD = re.compile(
    r"public\s+(?!const\b|static\b)(?:readonly\s+)?[A-Za-z_][\w<>\[\], .]*?\s+"
    r"([a-z]\w*)\s*(?:=\s*([^;]+))?;")


def _fields(path: Path, cls: str) -> dict[str, str | None]:
    decl = _top_level(_class_body(_src(path), cls))
    return {m.group(1): (m.group(2).strip() if m.group(2) else None) for m in _FIELD.finditer(decl)}


def _settings_fields() -> list[str]:
    return list(_fields(SETTINGS_CS, "RecolorSettings"))


def _pascal(name: str) -> str:
    return name[0].upper() + name[1:]


def test_settings_type_has_fields():
    fields = _settings_fields()
    assert len(fields) >= 8, f"RecolorSettings のフィールドを読めていません: {fields}"


@pytest.mark.parametrize("path,cls,non_settings", [
    (SESSION_CS, "IrocaSessionState", SESSION_NON_SETTINGS),
    (PRESET_CS, "IrocaPresetData", PRESET_NON_SETTINGS),
    (AUTOMATION_CS, "SettingsDto", set()),
], ids=["session", "preset", "zones-json"])
def test_field_sets_match(path, cls, non_settings):
    """設定を片側だけに足すと、その型を経由した経路でだけ設定が消える。"""
    expected = set(_settings_fields())
    actual = set(_fields(path, cls)) - non_settings
    assert actual == expected, (
        f"{cls} の設定フィールドが RecolorSettings と一致しません。\n"
        f"  {cls} にだけある: {sorted(actual - expected)}\n"
        f"  RecolorSettings にだけある: {sorted(expected - actual)}\n"
        "  設定でないフィールドを足したなら、このテストの *_NON_SETTINGS に理由つきで加えること。")


@pytest.mark.parametrize("path,cls", [
    (SESSION_CS, "IrocaSessionState"),
    (PRESET_CS, "IrocaPresetData"),
], ids=["session", "preset"])
def test_defaults_reference_single_source(path, cls):
    """既定値のリテラルを書き戻すと、UI・プリセット・zones JSON で既定が黙ってずれ得る。"""
    fields = _fields(path, cls)
    bad = {f: fields.get(f) for f in _settings_fields()
           if fields.get(f) != f"RecolorSettings.Default{_pascal(f)}"}
    assert not bad, f"{cls} の既定値が RecolorSettings.Default* を参照していません: {bad}"


def test_json_defaults_reference_single_source():
    src = _src(JSON_DEFAULTS_CS)
    bad = []
    for f in _settings_fields():
        m = re.search(rf"public\s+const\s+\w+\s+{_pascal(f)}\s*=\s*([^;]+);", src)
        if not m or m.group(1).strip() != f"RecolorSettings.Default{_pascal(f)}":
            bad.append((f, m.group(1).strip() if m else None))
    assert not bad, f"ZonesJsonDefaults の既定値が RecolorSettings.Default* を参照していません: {bad}"


def test_default_constants_exist():
    src = _src(SETTINGS_CS)
    missing = [f for f in _settings_fields()
               if not re.search(rf"public\s+const\s+\w+\s+Default{_pascal(f)}\s*=", src)]
    assert not missing, f"RecolorSettings に Default 定数がありません: {missing}"


def _method_body(src: str, signature: str) -> str:
    m = re.search(signature, src)
    assert m, f"メソッドが見つかりません: {signature}"
    rest = src[m.end():]
    if rest.lstrip().startswith("=>"):
        return rest[:rest.index(";") + 1]
    start = rest.index("{")
    depth, i = 1, start + 1
    while depth:
        depth += (rest[i] == "{") - (rest[i] == "}")
        i += 1
    return rest[start:i]


@pytest.mark.parametrize("signature,pattern", [
    (r"public\s+RecolorSettings\s*\(", r"this\.{f}\s*=\s*{f}\s*;"),
    (r"static\s+RecolorSettings\s+From\s*\(\s*IrocaSessionState\s+s\s*\)", r"\bs\.{f}\b"),
    (r"static\s+RecolorSettings\s+From\s*\(\s*IrocaPresetData\s+p\s*\)", r"\bp\.{f}\b"),
    (r"void\s+CopyTo\s*\(\s*IrocaSessionState\s+s\s*\)", r"\bs\.{f}\s*=\s*{f}\s*;"),
    (r"void\s+CopyTo\s*\(\s*IrocaPresetData\s+p\s*\)", r"\bp\.{f}\s*=\s*{f}\s*;"),
], ids=["ctor", "from-session", "from-preset", "copyto-session", "copyto-preset"])
def test_conversions_cover_every_field(signature, pattern):
    body = _method_body(_src(SETTINGS_CS), signature)
    missing = [f for f in _settings_fields() if not re.search(pattern.format(f=f), body)]
    assert not missing, f"{signature} が扱っていないフィールド: {missing}"


def test_from_session_argument_order_matches_ctor():
    """From は位置引数で ctor を呼ぶ。順序を取り違えても型が同じならコンパイルが通るので並びを見る。"""
    src = _src(SETTINGS_CS)
    for sig, var in [(r"static\s+RecolorSettings\s+From\s*\(\s*IrocaSessionState\s+s\s*\)", "s"),
                     (r"static\s+RecolorSettings\s+From\s*\(\s*IrocaPresetData\s+p\s*\)", "p")]:
        body = _method_body(src, sig)
        order = re.findall(rf"\b{var}\.(\w+)", body)
        assert order == _settings_fields(), f"{sig} の引数順が ctor（フィールド宣言順）と違います: {order}"


def test_automation_dto_argument_order_matches_ctor():
    body = _method_body(_src(AUTOMATION_CS),
                        r"static\s+RecolorSettings\s+SettingsFromDto\s*\(\s*SettingsDto\s+d\s*\)")
    order = re.findall(r"\bd\.(\w+)", body)
    assert order == _settings_fields(), f"SettingsFromDto の引数順が ctor と違います: {order}"


def test_harness_cfg_argument_order_matches_ctor():
    harness = REPO_ROOT / "scripts" / "headless-run" / "Harness.cs"
    body = _method_body(_src(harness), r"public\s+RecolorSettings\s+ToRecolorSettings\s*\(\s*\)")
    order = re.findall(r"\b([a-z]\w*)\b", body.split("new RecolorSettings", 1)[1])
    assert order == _settings_fields(), f"SettingsCfg.ToRecolorSettings の引数順が ctor と違います: {order}"


def test_process_pixels_has_single_entry():
    """位置引数の旧入口が残る/復活すると、呼び出し元ごとの並べ直しがまた始まる。"""
    src = "".join(_src(p) for p in CORE.glob("PixelProcessor*.cs"))
    entries = re.findall(r"(\w+)\s+static\s+void\s+ProcessPixelsArray\w*\s*\(([^)]*)\)", src)
    public = [(acc, params) for acc, params in entries if acc != "private"]
    assert len(public) == 1, f"非 private な ProcessPixelsArray が 1 つではありません: {[a for a, _ in public]}"
    assert re.search(r"\bRecolorSettings\s+settings\b", public[0][1]), (
        "ProcessPixelsArray の入口が RecolorSettings を受け取っていません")


def test_repro_dump_writes_every_setting():
    body = _method_body(_src(REPRO_CS),
                        r"static\s+IrocaAutomation\.SettingsDto\s+ToSettingsDto\s*\(\s*IrocaSessionState\s+s\s*\)")
    missing = [f for f in _settings_fields() if not re.search(rf"\b{f}\s*=\s*s\.{f}\b", body)]
    assert not missing, f"ReproDump.ToSettingsDto が書いていない設定: {missing}"


def test_preset_radius_is_clamped_in_one_place():
    """プリセットは手で書かれ得る。UI だけが範囲へ丸めていた頃は、同じプリセットでも
    MCP（Automation）経由だと範囲外の半径で処理され、UI と出力が食い違った。"""
    body = _method_body(_src(SETTINGS_CS), r"static\s+RecolorSettings\s+From\s*\(\s*IrocaPresetData\s+p\s*\)")
    assert re.search(r"Clamp\s*\(\s*p\.decontaminationRadius\s*,\s*MinDecontaminationRadius\s*,"
                     r"\s*MaxDecontaminationRadius\s*\)", body), "From(IrocaPresetData) が半径を丸めていません"
    presets_view = _src(REPO_ROOT / "Code" / "UI" / "PresetsView.cs")
    assert "decontaminationRadius" not in presets_view, (
        "PresetsView が設定を個別に触っています。丸めや変換は RecolorSettings.From に寄せること")
