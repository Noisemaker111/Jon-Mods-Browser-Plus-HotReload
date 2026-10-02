"""XUi scene editing. Reads native XML; saves explicit, version-checked mod patches."""
import copy
import hashlib
import json
import math
import re
import uuid
from pathlib import Path

from lxml import etree as XML

from xui_expr import evaluate

PARSER = XML.XMLParser(resolve_entities=False, no_network=True, remove_blank_text=False)
SCOPES = ("XUi_Common", "XUi_InGame", "XUi_Menu")
EDITABLE_TAGS = {"rect", "sprite", "filledsprite", "texture", "label", "grid", "button", "window"}
PIVOTS = {"topleft": (0, 0), "top": (.5, 0), "topright": (1, 0), "left": (0, .5),
          "center": (.5, .5), "right": (1, .5), "bottomleft": (0, 1), "bottom": (.5, 1), "bottomright": (1, 1)}


def parse(path):
    return XML.parse(str(path), parser=PARSER)


def scene_catalog(game, root=None):
    result = []
    for scope in SCOPES:
        for filename in ("templates.xml", "windows.xml"):
            path = game / "Data/Config" / scope / filename
            if not path.exists():
                continue
            doc = parse(path)
            if root is not None:
                for patch in patch_paths(root, scope, filename):
                    apply_patch(doc, patch, [])
            for element in doc.getroot():
                if not isinstance(element.tag, str):
                    continue
                name = element.get("name") if filename == "windows.xml" else element.tag
                if not name:
                    continue
                kind = "window" if filename == "windows.xml" else "template"
                result.append({"key": f"{scope}:{filename}:{name}", "name": name, "kind": kind,
                               "scope": scope, "source": str(path.relative_to(game)),
                               "title": {"party_entry": "Party portrait", "mainMenu": "Main menu"}.get(name, name)})
    return result


def owners(root):
    result = [{"id": "manager", "title": "HotReloadTool", "folder": root}]
    for folder in sorted((root / "mods").iterdir()):
        if folder.is_dir() and (folder / "ModInfo.xml").exists():
            result.append({"id": folder.name, "title": folder.name, "folder": folder})
    return result


def patch_paths(root, scope, filename):
    # Keep manager before gameplay mods, matching its native load order.
    return [owner["folder"] / "Config" / scope / filename for owner in owners(root)
            if (owner["folder"] / "Config" / scope / filename).exists()]


def apply_patch(doc, path, warnings):
    for op in parse(path).getroot():
        if not isinstance(op.tag, str):
            continue
        xpath = op.get("xpath")
        if not xpath:
            continue
        try:
            targets = doc.xpath(xpath)
        except XML.XPathError as error:
            warnings.append(f"{path.name}: {error}")
            continue
        for target in targets:
            if op.tag == "set":
                if isinstance(target, XML._ElementUnicodeResult) and target.is_attribute:
                    target.getparent().set(target.attrname, (op.text or "").strip())
                elif isinstance(target, XML._Element):
                    target.text = op.text
            elif op.tag == "remove":
                if isinstance(target, XML._ElementUnicodeResult) and target.is_attribute:
                    target.getparent().attrib.pop(target.attrname, None)
                elif isinstance(target, XML._Element) and target.getparent() is not None:
                    target.getparent().remove(target)
            elif op.tag == "append" and isinstance(target, XML._Element):
                for child in op:
                    if isinstance(child.tag, str):
                        target.append(copy.deepcopy(child))
            elif op.tag in ("insertBefore", "insertAfter") and isinstance(target, XML._Element):
                parent = target.getparent()
                index = parent.index(target) + (op.tag == "insertAfter")
                for child in op:
                    if isinstance(child.tag, str):
                        parent.insert(index, copy.deepcopy(child))
                        index += 1
            else:
                warnings.append("Unsupported patch operation: " + op.tag)


def load_scene(game, root, key):
    choices = {s["key"]: s for s in scene_catalog(game, root)}
    if key not in choices:
        raise ValueError("Choose a scene from the installed game's catalog")
    info = choices[key]
    source = game / info["source"]
    patches = patch_paths(root, info["scope"], source.name)
    doc, warnings = parse(source), []
    revision = hashlib.sha256(key.encode())
    for path in [source, *patches]:
        revision.update(str(path).encode())
        revision.update(path.read_bytes())
    for path in patches:
        apply_patch(doc, path, warnings)
    if info["kind"] == "window":
        element = next(e for e in doc.getroot() if isinstance(e.tag, str) and e.get("name") == info["name"])
    else:
        element = doc.getroot().find(info["name"])
    if element is None:
        raise ValueError("This scene was removed by a mod")
    model = element_model(element)
    relevant = [p for p in patches if info["name"] in p.read_text(encoding="utf-8")]
    owner_map = owners(root)
    default_owner = next((o["id"] for o in reversed(owner_map) if any(p.is_relative_to(o["folder"]) for p in relevant)
                          and (o["id"] != "manager" or any(not p.is_relative_to(root / "mods") for p in relevant))), "JonPartyPortraits")
    return {**info, "revision": revision.hexdigest(), "tree": model, "patches": [str(p.relative_to(root)) for p in patches],
            "owner": default_owner, "warnings": warnings}


def element_model(element, node_id="root"):
    return {"id": node_id, "tag": element.tag, "attrs": dict(element.attrib),
            "children": [element_model(e, f"{node_id}/{i}") for i, e in enumerate(element) if isinstance(e.tag, str)]}


def validate_tree(model, baseline):
    known = {}
    def collect(node):
        known[node["id"]] = node
        for child in node["children"]:
            collect(child)
    collect(baseline)
    seen = set()
    count = 0
    def walk(node, depth=0):
        nonlocal count
        count += 1
        if count > 3000 or depth > 60 or not isinstance(node, dict):
            raise ValueError("Scene is too large or invalid")
        node_id, tag = node.get("id"), node.get("tag")
        if not isinstance(node_id, str) or node_id in seen:
            raise ValueError("Every layer needs a unique ID")
        seen.add(node_id)
        original = known.get(node_id)
        if original and original["tag"] != tag:
            raise ValueError("Existing control types cannot be changed")
        if not original and (not node_id.startswith("new-") or tag not in EDITABLE_TAGS - {"window"}):
            raise ValueError("Unknown control type or node ID")
        attrs = node.get("attrs")
        if not isinstance(attrs, dict) or len(attrs) > 100:
            raise ValueError("Invalid control attributes")
        for key, value in attrs.items():
            if not re.fullmatch(r"[A-Za-z_][\w.-]*", key) or not isinstance(value, str) or len(value) > 4000:
                raise ValueError("Attributes must be valid XML names and short strings")
        children = node.get("children")
        if not isinstance(children, list):
            raise ValueError("Invalid child list")
        result = XML.Element(tag, attrs)
        for child in children:
            result.append(walk(child, depth + 1))
        return result
    if not isinstance(model, dict) or model.get("id") != "root" or model.get("tag") != baseline["tag"]:
        raise ValueError("The scene root must be preserved")
    return walk(model)


def patch_xml(info, element):
    parent = "windows" if info["kind"] == "window" else "templates"
    target = f"/{parent}/{element.tag}[@name='{info['name']}']" if info["kind"] == "window" else f"/{parent}/{info['name']}"
    patch = XML.Element("configs")
    XML.SubElement(patch, "remove", xpath=target)
    append = XML.SubElement(patch, "append", xpath="/" + parent)
    append.append(copy.deepcopy(element))
    return XML.tostring(patch, pretty_print=True, encoding="unicode")


def save_scene(game, root, home, payload):
    scene = load_scene(game, root, payload.get("key"))
    if payload.get("revision") != scene["revision"]:
        raise RuntimeError("Source XML changed outside the editor. Reload before saving; nothing was overwritten.")
    element = validate_tree(payload.get("tree"), scene["tree"])
    owner = next((o for o in owners(root) if o["id"] == payload.get("owner", scene["owner"])), None)
    if not owner:
        raise ValueError("Choose one of this repository's mods as the save destination")
    path = owner["folder"] / "Config" / scene["scope"] / Path(scene["source"]).name
    old = path.read_bytes() if path.exists() else b"<configs>\n</configs>\n"
    doc = XML.ElementTree(XML.fromstring(old, parser=PARSER))
    block_id = hashlib.sha256(scene["key"].encode()).hexdigest()[:16]
    begin, end = f"JonLabStudio {block_id} begin", f"JonLabStudio {block_id} end"
    existing = list(doc.getroot())
    begins = [i for i, child in enumerate(existing) if isinstance(child, XML._Comment) and child.text == begin]
    ends = [i for i, child in enumerate(existing) if isinstance(child, XML._Comment) and child.text == end]
    if (begins or ends) and (len(begins) != 1 or len(ends) != 1 or begins[0] >= ends[0]):
        raise RuntimeError("The existing editor block has damaged markers. Nothing was overwritten; repair that block before saving.")
    removing = False
    for child in list(doc.getroot()):
        if isinstance(child, XML._Comment) and child.text == begin:
            removing = True
        if removing:
            doc.getroot().remove(child)
        if isinstance(child, XML._Comment) and child.text == end:
            removing = False
    if len(doc.getroot()):
        doc.getroot()[-1].tail = "\n  "
    marker = XML.Comment(begin)
    marker.tail = "\n  "
    doc.getroot().append(marker)
    patch = XML.fromstring(patch_xml(scene, element).encode(), parser=PARSER)
    for op in patch:
        op.tail = "\n  "
        doc.getroot().append(op)
    marker = XML.Comment(end)
    marker.tail = "\n"
    doc.getroot().append(marker)
    data = XML.tostring(doc, encoding="utf-8", pretty_print=True).rstrip(b"\r\n") + b"\n"
    # Verify the proposed patch survives later-loaded mods before touching source.
    game_doc = parse(game / scene["source"])
    warnings = []
    paths = patch_paths(root, scene["scope"], path.name)
    if path not in paths:
        paths.append(path)
        order = {str(o["folder"]): i for i, o in enumerate(owners(root))}
        paths.sort(key=lambda p: order.get(str(p.parents[2]), 0))
    for candidate in paths:
        if candidate == path:
            proposed = home / "studio/proposed" / (uuid.uuid4().hex + ".xml")
            proposed.parent.mkdir(parents=True, exist_ok=True)
            proposed.write_bytes(data)
            try:
                apply_patch(game_doc, proposed, warnings)
            finally:
                proposed.unlink()
        else:
            apply_patch(game_doc, candidate, warnings)
    actual = next((e for e in game_doc.getroot() if isinstance(e.tag, str) and
                   (e.get("name") == scene["name"] if scene["kind"] == "window" else e.tag == scene["name"])), None)
    def signature(node):
        return (node.tag, dict(node.attrib), [signature(c) for c in node if isinstance(c.tag, str)]) if node is not None else None
    if signature(actual) != signature(element):
        raise RuntimeError("A later-loaded mod overrides this scene. Choose that mod as the save destination; nothing was written.")
    backup = home / "studio/backups" / uuid.uuid4().hex / path.relative_to(root)
    backup.parent.mkdir(parents=True, exist_ok=True)
    backup.write_bytes(old)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".studio-tmp")
    temp.write_bytes(data)
    # Compare once more immediately before replacing, including sources read at load time.
    if load_scene(game, root, scene["key"])["revision"] != payload.get("revision") or (path.exists() and path.read_bytes() != old):
        temp.unlink()
        raise RuntimeError("The destination changed while saving. Reload; nothing was overwritten.")
    temp.replace(path)
    saved = load_scene(game, root, scene["key"])
    return {"saved": True, "path": str(path.relative_to(root)), "backup": str(backup.relative_to(home)), "scene": saved}


def style_catalog(game):
    globals_, typed, named = {}, {}, {}
    for scope in SCOPES:
        path = game / "Data/Config" / scope / "styles.xml"
        if not path.exists():
            continue
        doc = parse(path)
        for entry in doc.getroot().findall("global/style_entry"):
            globals_[entry.get("name")] = entry.get("value", "")
        for style in doc.getroot().findall("style"):
            attrs = {e.get("name"): e.get("value", "") for e in style if isinstance(e.tag, str)}
            tag, name = style.get("type", ""), style.get("name")
            if name:
                named[(tag, name)] = attrs
            else:
                typed.setdefault(tag, {}).update(attrs)
    for (tag, name), attrs in named.items():
        for key, value in attrs.items():
            globals_[name + ":" + key] = value
    return globals_, typed, named


def layout(game, root, scene, tree, values, assets):
    """Native styles/atlas metadata and a scene graph; unresolved runtime logic is explicit."""
    validate_tree(tree, scene["tree"])
    if not isinstance(values, dict) or len(values) > 300 or any(not isinstance(v, (str, float, int, bool)) for v in values.values()):
        raise ValueError("Binding values must be a small dictionary of simple values")
    globals_, typed, named = style_catalog(game)
    controls, warnings = [], list(scene["warnings"])
    asset_map = {(a.get("atlas", ""), a["name"]): a for a in assets if a["kind"] in ("sprite", "item")}
    textures = {a["name"]: a for a in assets if a["kind"] in ("texture", "capture")}
    font = next((a for a in assets if a["kind"] == "font" and a["name"] == "ReferenceFont"), None)
    captured = next((a for a in assets if a.get("runtimeSlot") == "jonPortrait"), None)
    boxes = {}
    font_sizes = {}
    templates = {}
    for scope in SCOPES:
        path = game / "Data/Config" / scope / "templates.xml"
        if not path.exists():
            continue
        doc = parse(path)
        for patch in patch_paths(root, scope, "templates.xml"):
            apply_patch(doc, patch, warnings)
        for el in doc.getroot():
            if isinstance(el.tag, str):
                templates[el.tag] = element_model(el)

    def substitute(value, parameters=None):
        value = str(value)
        def stringify(result):
            return str(result).lower() if isinstance(result, bool) else str(result)
        def expression(match):
            try:
                return stringify(evaluate(match[1].lstrip("%# "), {**(parameters or {}), **values}, localization(game)))
            except (ValueError, TypeError, SyntaxError, ZeroDivisionError, OverflowError, RecursionError):
                return match[0]
        for _ in range(8):
            old = value
            value = re.sub(r"\[([\w.:]+)\]", lambda m: globals_.get(m[1], m[0]), value)
            value = re.sub(r"\$\{([^{}]+)\}", expression, value)
            value = re.sub(r"(?<!\$)\{([^{}]+)\}", expression, value)
            if value == old:
                break
        return value

    def number(value, default=0):
        try:
            result = float(value)
            return result if math.isfinite(result) else default
        except (ValueError, TypeError):
            if value and ("{" in str(value) or "$" in str(value)):
                message = "Unresolved layout expression: " + str(value)
                if message not in warnings:
                    warnings.append(message)
            return default

    def pair(value, default=(0, 0)):
        bits = str(value).split(",")
        return (number(bits[0], default[0]), number(bits[1], default[1])) if len(bits) == 2 else default

    def color(value):
        bits = str(value).split(",")
        if len(bits) in (3, 4) and all(re.fullmatch(r"\s*\d+\s*", b) for b in bits):
            return [min(255, int(b)) for b in bits] + ([255] if len(bits) == 3 else [])
        warnings.append("Unresolved color: " + str(value))
        return [255, 255, 255, 255]

    def walk(node, parent_box, depth=0, parameters=None, virtual=False):
        if depth > 35:
            warnings.append("Template recursion exceeded")
            return
        attrs = {**typed.get(node["tag"], {})}
        for name in node["attrs"].get("style", "").replace(" ", "").split(","):
            attrs.update(named.get(("", name), {}))
            attrs.update(named.get((node["tag"], name), {}))
        attrs.update(node["attrs"])
        tag = node["tag"]
        if (node["id"] != "root" or not node["children"]) and tag in templates and tag not in EDITABLE_TAGS:
            template = templates[tag]
            # Resolve invocation attributes in the caller's context first. An
            # inner template forwarding depth="${depth}" must not shadow itself.
            inherited = {**(parameters or {}), "parentinnerwidth": parent_box[2], "parentinnerheight": parent_box[3]}
            invocation = {k: substitute(v, inherited) for k, v in attrs.items()}
            params = {**inherited, **template["attrs"], **invocation}
            for _ in range(5):
                params = {k: substitute(v, {**(parameters or {}), **params}) for k, v in params.items()}
            for child in template["children"]:
                clone = copy.deepcopy(child)
                for key in ("pos", "width", "height", "name", "pivot", "depth", "visible", "anchor_left", "anchor_right", "anchor_top", "anchor_bottom"):
                    if key in invocation:
                        clone["attrs"].setdefault(key, invocation[key])
                def mark(model):
                    model["id"] = node["id"]
                    for sub in model["children"]:
                        mark(sub)
                mark(clone)
                walk(clone, parent_box, depth + 1, params, virtual=True)
            return
        px, py, pw, ph = parent_box
        params = {"parentinnerwidth": pw, "parentinnerheight": ph, "outerwidth": pw, "outerheight": ph,
                  "borderleft": 0, "borderright": 0, "bordertop": 0, "borderbottom": 0,
                  **(parameters or {}), **(node["attrs"] if node["id"] == "root" else {})}
        for _ in range(5):
            params = {k: substitute(v, params) for k, v in params.items()}
        attrs = {k: substitute(v, params) for k, v in attrs.items()}
        pos = pair(attrs.get("pos", "0,0"))
        size = pair(attrs.get("size", ""), (pw, ph))
        w, h = number(attrs.get("width"), size[0]), number(attrs.get("height"), size[1])
        w, h = max(0, w), max(0, h)
        if tag == "label" and attrs.get("overflow", "").lower() == "resizefreely" and font:
            from PIL import ImageFont
            text = localization(game).get(attrs.get("text_key"), attrs.get("text", ""))
            if attrs.get("upper_case") == "true":
                text = text.upper()
            fs = max(1, min(512, int(number(attrs.get("font_size"), 28))))
            if fs not in font_sizes:
                font_sizes[fs] = ImageFont.truetype(font["file"], fs)
            native_font = font_sizes[fs]
            w = min(number(attrs.get("overflow_width"), 10000), native_font.getlength(text))
            bounds = native_font.getbbox(text)
            h = min(number(attrs.get("overflow_height"), 10000), max(fs, bounds[3] - bounds[1]))
        if attrs.get("keep_aspect_ratio", "").lower() == "basedonheight":
            w = h * number(attrs.get("aspect_ratio"), 1)
        elif attrs.get("keep_aspect_ratio", "").lower() == "basedonwidth":
            h = w / max(.001, number(attrs.get("aspect_ratio"), 1))
        pivot = PIVOTS.get(attrs.get("pivot", "topleft").lower(), (0, 0))
        x, y = px + pos[0] - w * pivot[0], py - pos[1] - h * pivot[1]
        if node["id"] == "root":
            x, y = 0, 0  # isolated window canvas, not its position on the full-screen HUD
        def anchor(side):
            spec = attrs.get("anchor_" + side)
            if not spec:
                return None
            parts = spec.split(",")
            if len(parts) != 3:
                warnings.append("Unresolved anchor: " + spec)
                return None
            reference = parent_box if parts[0] == "#parent" else ([0, 0, number(values.get("viewportwidth"), 1920), number(values.get("viewportheight"), 1080)] if parts[0] == "#cam" else boxes.get(parts[0]))
            if reference is None:
                warnings.append("Anchor target not available: " + parts[0])
                return None
            fraction, offset = number(parts[1]), number(parts[2])
            return reference[0] + reference[2] * fraction + offset if side in ("left", "right") else reference[1] + reference[3] * (1 - fraction) - offset
        left, right, top, bottom = [anchor(side) for side in ("left", "right", "top", "bottom")]
        if left is not None and right is not None:
            x, w = left, max(0, right - left)
        elif left is not None:
            x = left
        elif right is not None:
            x = right - w
        if top is not None and bottom is not None:
            y, h = top, max(0, bottom - top)
        elif top is not None:
            y = top
        elif bottom is not None:
            y = bottom - h
        box = [x, y, w, h]
        if attrs.get("name"):
            boxes[attrs["name"]] = box
        visible = attrs.get("visible", "true").lower() not in ("false", "0")
        if "{" in attrs.get("visible", ""):
            warnings.append("Unresolved visibility binding: " + attrs["visible"])
        if not visible:
            return
        control = {"id": node["id"], "tag": tag, "name": attrs.get("name", tag), "box": box,
                   "attrs": attrs, "depth": number(attrs.get("depth"), depth), "virtual": virtual}
        if tag in ("sprite", "filledsprite", "button"):
            sprite = attrs.get("sprite", globals_.get("default_sprite", "menu_empty"))
            asset = asset_map.get((attrs.get("atlas", "UIAtlas"), sprite))
            control.update(asset=asset["id"] if asset else None, color=color(attrs.get("color", attrs.get("defaultcolor", "255,255,255,255"))),
                           border=asset.get("border", [0, 0, 0, 0]) if asset else [0, 0, 0, 0],
                           fill=max(0, min(1, number(attrs.get("fill"), 1))))
            if not asset and sprite:
                warnings.append("Missing sprite/binding: " + sprite)
        elif tag == "texture":
            sample_id = values.get("texture:" + attrs.get("name", node["id"]), values.get("texture:" + node["id"]))
            asset = next((a for a in assets if a["id"] == sample_id), None)
            texture_name = re.split(r"[/\\]", attrs.get("texture", ""))[-1].rsplit(".", 1)[0]
            asset = asset or (captured if attrs.get("name") == "jonPortrait" else textures.get(texture_name))
            control.update(asset=asset["id"] if asset else None, color=[255, 255, 255, 255])
            if not asset:
                warnings.append("Runtime texture not captured: " + attrs.get("name", tag))
        elif tag == "label":
            text = attrs.get("text", "")
            if attrs.get("text_key"):
                text = localization(game).get(attrs["text_key"], text or attrs["text_key"])
            text = re.sub(r"\[(?:[0-9a-fA-F]{6}|-)\]", "", text)
            if "{" in text:
                warnings.append("Unresolved text binding: " + text)
            control.update(text=text.upper() if attrs.get("upper_case") == "true" else text,
                           color=color(attrs.get("color", "255,255,255,255")), font=font["id"] if font else None)
        elif tag not in ("rect", "window", "grid", "panel") and tag not in templates:
            warnings.append("Native widget needs runtime validation: " + tag)
        if tag == "panel" and attrs.get("clipping", "none").lower() not in ("none", ""):
            warnings.append("Panel clipping needs runtime validation: " + attrs.get("name", tag))
        controls.append(control)
        children = node["children"]
        if tag == "grid" and attrs.get("repeat_content", "true").lower() != "false" and children:
            count = int(number(attrs.get("cols"), 1) * number(attrs.get("rows"), 1))
            if count > 600:
                warnings.append("Grid preview limited to 600 repeated cells")
            children = [children[i % len(children)] for i in range(min(600, max(len(children), count)))]
        for i, child in enumerate(children):
            if tag == "grid":
                child = copy.deepcopy(child)
                columns = max(1, int(number(attrs.get("cols"), 1)))
                cell_w = number(attrs.get("cell_width"), w / columns)
                cell_h = number(attrs.get("cell_height"), h / max(1, number(attrs.get("rows"), len(node["children"]))))
                child["attrs"]["pos"] = f"{(i % columns) * cell_w},{-(i // columns) * cell_h}"
                child["attrs"].setdefault("width", str(cell_w))
                child["attrs"].setdefault("height", str(cell_h))
                child_params = {**params, "repeat_i": i, "repeat_n": i + 1, "width": cell_w, "height": cell_h}
            else:
                child_params = params
            walk(child, box, depth + 1, child_params, virtual)

    root_w = number(tree["attrs"].get("width"), 310)
    root_h = number(tree["attrs"].get("height"), 76)
    if tree["tag"] == "window":
        root_w, root_h = root_w or 800, root_h or 600
    walk(tree, [0, 0, root_w, root_h])
    visible = [c for c in controls if c["tag"] in ("sprite", "filledsprite", "button", "texture", "label") and c["box"][2] and c["box"][3]
               and c.get("color", [0, 0, 0, 255])[3] > 0 and (c["tag"] == "label" or c.get("asset"))]
    if visible:
        min_x = min(c["box"][0] for c in visible)
        min_y = min(c["box"][1] for c in visible)
        max_x = max(c["box"][0] + c["box"][2] for c in visible)
        max_y = max(c["box"][1] + c["box"][3] for c in visible)
        bounds = [min_x, min_y, max_x - min_x, max_y - min_y]
    else:
        bounds = [0, 0, root_w, root_h]
    return {"controls": sorted(controls, key=lambda c: c["depth"]), "bounds": bounds,
            "warnings": list(dict.fromkeys(warnings)), "font": font["id"] if font else None,
            "patch": patch_xml(scene, validate_tree(tree, scene["tree"]))}


_LOCALIZATION = {}
def localization(game):
    if str(game) not in _LOCALIZATION:
        import csv
        path = game / "Data/Config/Localization.csv"
        with path.open(encoding="utf-8", errors="replace", newline="") as file:
            reader = csv.DictReader(file)
            _LOCALIZATION[str(game)] = {row["Key"]: row.get("english", row.get("English", "")) for row in reader}
    return _LOCALIZATION[str(game)]


def pencil_resources(graph, assets, workspace, scratch):
    """Real nine-sliced/tinted sprites for OpenPencil image fills, never text screenshots."""
    from urllib.parse import urlencode
    directory = workspace / "pencil/resources"
    directory.mkdir(parents=True, exist_ok=True)
    indexed = {a["id"]: a for a in assets}
    for control in graph["controls"]:
        if not control.get("asset") or control["tag"] == "label":
            continue
        # Position does not affect a sprite's pixels. Content/size/tint do.
        visual = {**control, "box": [0, 0, *control["box"][2:]]}
        visual.pop("imageUrl", None)
        source = Path(indexed[control["asset"]]["file"]).stat()
        digest = hashlib.sha256(json.dumps([visual, source.st_mtime_ns, source.st_size], sort_keys=True).encode()).hexdigest()
        path = directory / (digest + ".png")
        if not path.exists():
            render_image({"controls": [visual], "bounds": visual["box"]}, assets).save(path)
        control["imageUrl"] = "/file?" + urlencode({"path": str(path.relative_to(scratch))})


def render_image(layout_data, assets, scale=2):
    """Raster export using actual atlas crops and the extracted native font."""
    from PIL import Image, ImageChops, ImageDraw, ImageFont
    min_x, min_y, width, height = layout_data["bounds"]
    width, height = max(1, math.ceil(width * scale)), max(1, math.ceil(height * scale))
    if width * height > 16000000:
        raise ValueError("Canvas export exceeds 16 million pixels")
    output = Image.new("RGBA", (width, height))
    indexed = {a["id"]: a for a in assets}
    for control in layout_data["controls"]:
        tag, attrs = control["tag"], control["attrs"]
        x, y, w, h = control["box"]
        x, y, w, h = round((x - min_x) * scale), round((y - min_y) * scale), round(w * scale), round(h * scale)
        if w <= 0 or h <= 0 or w * h > 16000000:
            continue
        layer = Image.new("RGBA", (w, h))
        color = tuple(control.get("color", [255, 255, 255, 255]))
        asset = indexed.get(control.get("asset"))
        if tag in ("sprite", "filledsprite", "button", "texture") and asset:
            with Image.open(asset["file"]) as image:
                image = image.convert("RGBA")
                image = ImageChops.multiply(image, Image.new("RGBA", image.size, color))
                border = control.get("border", [0, 0, 0, 0])
                if attrs.get("type") == "sliced" and any(border):
                    left, top, right, bottom = border
                    sx = [0, left, image.width - right, image.width]
                    sy = [0, top, image.height - bottom, image.height]
                    dl, dr = min(left * scale, w / 2), min(right * scale, w / 2)
                    dt, db = min(top * scale, h / 2), min(bottom * scale, h / 2)
                    dx, dy = [0, dl, w - dr, w], [0, dt, h - db, h]
                    for row in range(3):
                        for col in range(3):
                            if row == col == 1 and attrs.get("fillcenter", "true").lower() == "false":
                                continue
                            target_w, target_h = round(dx[col + 1] - dx[col]), round(dy[row + 1] - dy[row])
                            if target_w > 0 and target_h > 0:
                                piece = image.crop((sx[col], sy[row], sx[col + 1], sy[row + 1])).resize((target_w, target_h), Image.Resampling.LANCZOS)
                                layer.alpha_composite(piece, (round(dx[col]), round(dy[row])))
                else:
                    layer = image.resize((w, h), Image.Resampling.LANCZOS)
                if attrs.get("type") == "filled" or tag == "filledsprite":
                    fill = round(w * control.get("fill", 1))
                    layer.paste((0, 0, 0, 0), (fill, 0, w, h))
        elif tag == "label":
            font_asset = indexed.get(control.get("font"))
            if not font_asset:
                continue
            font_size = max(1, round(min(512, float(attrs.get("font_size", 28))) * scale))
            font = ImageFont.truetype(font_asset["file"], font_size)
            draw = ImageDraw.Draw(layer)
            text = control.get("text", "")
            text_width = draw.textlength(text, font=font)
            tx = (w - text_width) / 2 if attrs.get("justify") == "center" else w - text_width if attrs.get("justify") == "right" else 0
            bbox = font.getbbox(text)
            ty = max(0, (h - (bbox[3] - bbox[1])) / 2 - bbox[1])
            effect = attrs.get("effect", "none")
            if effect in ("outline", "shadow"):
                draw.text((tx + scale, ty + scale), text, font=font, fill=(0, 0, 0, 255))
            draw.text((tx, ty), text, font=font, fill=color)
        output.alpha_composite(layer, (x, y))
    return output
