"""Read-only extraction of the installed game's UI assets. Never redistribute the cache."""
import argparse
import base64
import hashlib
import io
import json
import re
import time
from pathlib import Path

from PIL import Image


def identity(kind, source, name):
    return hashlib.sha256(f"{kind}:{source}:{name}".encode()).hexdigest()[:24]


def build(game, cache, scratch):
    import UnityPy
    from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

    cache.mkdir(parents=True, exist_ok=True)
    entries, warnings = [], []
    sources = [game / "7DaysToDie_Data/data.unity3d",
               game / "Data/Addressables/Standalone/textures_assets_textures/ui.bundle",
               game / "Data/Addressables/Standalone/fonts_assets_all.bundle"]

    def register(kind, name, source, image=None, raw=None, file=None, **metadata):
        asset_id = identity(kind, source, name)
        if image is not None:
            file = cache / (asset_id + ".png")
            image.save(file)
            metadata["size"] = list(image.size)
        if raw is not None:
            file = cache / (asset_id + ".ttf")
            file.write_bytes(bytes(raw))
        if file:
            entries.append({"id": asset_id, "kind": kind, "name": name, "source": str(source),
                            "file": str(file), "url": "/api/assets/file/" + asset_id, **metadata})

    for source in sources:
        print("Reading " + source.name, flush=True)
        env = UnityPy.load(str(source))
        native_paths = {pointer.path_id: name for name, pointer in env.container.items()}
        textures = {}
        for obj in env.objects:
            key = (obj.assets_file.name, obj.path_id)
            if obj.type.name == "Texture2D":
                data = obj.read()
                if data.m_Width > 0 and data.m_Height > 0:
                    textures[key] = obj
                    try:
                        native = native_paths.get(obj.path_id, "")
                        native = "@:" + native.split("Assets/AssetBundles/", 1)[1] if "Assets/AssetBundles/" in native else ""
                        register("texture", data.m_Name, str(source) + ":" + str(key), image=data.image, nativePath=native)
                    except Exception as error:
                        warnings.append(data.m_Name + ": " + str(error))
            elif obj.type.name == "Font":
                data = obj.read()
                if data.m_FontData:
                    register("font", data.m_Name, str(source) + ":" + str(key), raw=data.m_FontData)

        if source.name != "data.unity3d":
            continue
        version = next(iter(env.objects)).assets_file.unity_version
        generator = TypeTreeGenerator(version)
        generator.load_local_dll_folder(str(game / "7DaysToDie_Data/Managed"))
        env.typetree_generator = generator
        for obj in env.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            # Only generate trees for atlas/font types, never for arbitrary gameplay scripts.
            data = obj.read(check_read=False)
            if data.m_Name not in ("UIAtlas", "SymbolAtlas", "UI_Atlas_Controller_Art", "ReferenceFont"):
                continue
            tree = obj.read_typetree()
            if tree["m_Name"] == "ReferenceFont":
                font_id = tree["mDynamicFont"]["m_PathID"]
                font = obj.assets_file.objects[font_id].read()
                register("font", "ReferenceFont", str(source) + ":ReferenceFont", raw=font.m_FontData,
                         nativeName=font.m_Name, nativeSize=tree["mDynamicFontSize"])
                continue
            material = obj.assets_file.objects[tree["material"]["m_PathID"]].read_typetree()
            tex_pointer = dict(material["m_SavedProperties"]["m_TexEnvs"])["_MainTex"]["m_Texture"]
            texture = obj.assets_file.objects[tex_pointer["m_PathID"]].read().image.convert("RGBA")
            for sprite in tree["mSprites"]:
                x, y, w, h = [sprite[k] for k in ("x", "y", "width", "height")]
                if not (0 <= x < texture.width and 0 <= y < texture.height and w > 0 and h > 0 and x + w <= texture.width and y + h <= texture.height):
                    warnings.append("Invalid atlas rectangle: " + sprite["name"])
                    continue
                cropped = texture.crop((x, y, x + w, y + h))
                padding = [max(0, sprite[k]) for k in ("paddingLeft", "paddingTop", "paddingRight", "paddingBottom")]
                if any(padding):
                    padded = Image.new("RGBA", (w + padding[0] + padding[2], h + padding[1] + padding[3]))
                    padded.paste(cropped, (padding[0], padding[1]))
                    cropped = padded
                register("sprite", sprite["name"], str(source) + ":" + tree["m_Name"], image=cropped,
                         atlas=tree["m_Name"], rect=[x, y, w, h], padding=padding,
                         border=[sprite[k] for k in ("borderLeft", "borderTop", "borderRight", "borderBottom")])
            print(tree["m_Name"] + ": " + str(len(tree["mSprites"])) + " sprites", flush=True)

    for file in sorted((game / "Data/ItemIcons").glob("*.png")):
        register("item", file.stem, str(file), file=file, atlas="ItemIconAtlas")
    for file in sorted((game / "Data/Prefabs").glob("**/*.jpg")):
        register("prefab", file.stem, str(file), file=file)
    # A genuine previously captured portrait, not a made-up replacement for a runtime render.
    shot = scratch / "ingame/shots/a-21-following-walk.png"
    if shot.exists():
        with Image.open(shot) as image:
            register("capture", "LabB portrait — recorded game frame", str(shot), image=image.crop((8, 100, 55, 147)),
                     runtimeSlot="jonPortrait", crop=[8, 100, 55, 147], note="47×47 crop of an actual game screenshot; not a live 3D portrait")

    # Preserve user-imported runtime images across library rebuilds.
    imports = cache / "imports.json"
    if imports.exists():
        entries += json.loads(imports.read_text(encoding="utf-8"))
    manifest = {"version": 1, "built": time.time(), "game": str(game), "assets": entries, "warnings": warnings,
                "sources": [{"path": str(p), "size": p.stat().st_size, "modified": p.stat().st_mtime} for p in sources],
                "license": "Extracted from this local game install for private mod development; do not redistribute."}
    path = cache / "index.json"
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)
    counts = {kind: sum(a["kind"] == kind for a in entries) for kind in sorted({a["kind"] for a in entries})}
    print("PASS local game asset library: " + json.dumps(counts), flush=True)
    for warning in warnings:
        print("WARN " + warning, flush=True)
    return manifest


def load(cache):
    path = cache / "index.json"
    if not path.exists():
        return {"assets": [], "warnings": [], "built": None}
    return json.loads(path.read_text(encoding="utf-8"))


def import_capture(cache, payload):
    """An explicitly labelled preview sample. Never installs copyrighted/custom files."""
    name, encoded = payload.get("name", "Runtime capture"), payload.get("image", "")
    if not isinstance(name, str) or not 1 <= len(name) <= 100 or not isinstance(encoded, str):
        raise ValueError("Give the capture a short name")
    if not re.match(r"^data:image/(?:png|jpeg|webp);base64,", encoded):
        raise ValueError("Choose a PNG, JPEG or WebP screenshot")
    raw = base64.b64decode(encoded.split(",", 1)[1], validate=True)
    with Image.open(io.BytesIO(raw)) as image:
        if image.width * image.height > 16000000:
            raise ValueError("Capture exceeds 16 million pixels")
        crop = payload.get("crop", [0, 0, image.width, image.height])
        if not isinstance(crop, list) or len(crop) != 4 or any(type(v) is not int for v in crop):
            raise ValueError("Crop must be integer X, Y, width and height")
        x, y, w, h = crop
        if x < 0 or y < 0 or w < 1 or h < 1 or x + w > image.width or y + h > image.height:
            raise ValueError("Crop is outside the screenshot")
        image = image.crop((x, y, x + w, y + h)).convert("RGBA")
        asset_id = hashlib.sha256(raw + json.dumps(crop).encode() + name.encode()).hexdigest()[:24]
        cache.mkdir(parents=True, exist_ok=True)
        file = cache / (asset_id + ".png")
        image.save(file)
        entry = {"id": asset_id, "name": name, "kind": "capture", "size": [w, h], "file": str(file),
                 "url": "/api/assets/file/" + asset_id, "source": "User-selected screenshot crop", "crop": crop,
                 "note": "Imported preview sample only. The game's runtime texture binding is not changed."}
    imports_path = cache / "imports.json"
    imports = json.loads(imports_path.read_text(encoding="utf-8")) if imports_path.exists() else []
    imports = [a for a in imports if a["id"] != asset_id] + [entry]
    manifest = load(cache)
    manifest["assets"] = [a for a in manifest["assets"] if a["id"] != asset_id] + [entry]
    for path, data in ((imports_path, imports), (cache / "index.json", manifest)):
        temp = path.with_suffix(".tmp")
        temp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        temp.replace(path)
    return {k: v for k, v in entry.items() if k != "file"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    build(args.game, args.cache, args.scratch)


if __name__ == "__main__":
    main()
