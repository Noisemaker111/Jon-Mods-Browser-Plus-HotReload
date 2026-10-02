"""Offline XUi preview.

Applies a mod's XUi patch (Config/XUi_*/templates.xml) on top of the real game
template and renders the result to a PNG with the same box model the game uses,
so a panel can be designed and iterated without launching the game.

  python xui-preview.py --template party_entry --out preview.png
  python xui-preview.py --template party_entry --size 2 --out preview@2x.png
  python xui-preview.py --list

Geometry, colours, fills and text are taken from the real XML. Symbols that live
in the game's texture atlases are drawn as labelled placeholders; for a pixel
exact image use the engine tier (see sim/README.md).
"""
import argparse, json, math, re, sys
from pathlib import Path
from xml.etree import ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

GAME = Path(r"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die")
REPO = Path(__file__).resolve().parent.parent
TEMPLATE_ROOTS = [GAME / "Data" / "Config" / "XUi_InGame", GAME / "Data" / "Config" / "XUi_Menu", GAME / "Data" / "Config" / "XUi_Common"]
PATCH_DIRS = [REPO / "mods", REPO]

# Values the game substitutes for {placeholders}. Override with --values.
DEFAULT_VALUES = {
    "name": "NoisemakerJon", "jonlevel": "Lv 42", "distance": "128m",
    "healthmodifiedmax": "0.78", "healthfill": "0.62", "staminamodifiedmax": "0.9",
    "staminafill": "0.55", "jondeficitfill": "0.35", "jonxpfill": "0.6",
    "healthcurrentwithmax": "78/100", "healthcolor": "255,255,255,255",
    "distancecolor": "255,255,255,255", "partyvisible": "true", "showarrow": "true",
    "showicon1": "true", "showicon2": "true", "voicevisible": "true",
    "arrowcolor": "255,255,255,255", "icon1": "ui_game_symbol_quest",
    "icon2": "ui_game_symbol_stealth", "voicemuted": "false", "voiceactive": "true",
    "icon1color": "255,255,255,255", "icon2color": "255,255,255,255",
}

# Ready-made player states so a layout can be checked against real situations.
STATES = {
    "healthy": {},
    "low": {"healthfill": "0.15", "healthcurrentwithmax": "12/100", "staminafill": "0.1",
            "healthcolor": "255,120,120,255", "jonxpfill": "0.2"},
    "dead": {"healthfill": "0", "healthcurrentwithmax": "0/100", "partyvisible": "true",
             "staminafill": "0", "showarrow": "false", "voicevisible": "false"},
    "muted": {"voicemuted": "true", "voiceactive": "false"},
    "far": {"distance": "412m", "showarrow": "true", "arrowcolor": "255,180,60,255", "icon1": "ui_game_symbol_food"},
}


def parse_color(text, values):
    if not text:
        return None
    value = substitute(text, values).strip()
    m = re.match(r"^(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?$", value)
    if m:
        r, g, b, a = (int(x) if x else 255 for x in (m.group(1), m.group(2), m.group(3), m.group(4) or "255"))
        return (r, g, b, a if len(m.groups()) == 4 and m.group(4) else 255)
    if "white" in value:
        return (255, 255, 255, 255)
    if "black" in value:
        return (0, 0, 0, 255)
    return (200, 200, 200, 255)


def substitute(text, values):
    if text is None:
        return ""
    return re.sub(r"\{([^}]+)\}", lambda m: str(values.get(m.group(1).strip(), "")), text)


def number(text, default=0.0):
    try:
        return float(text)
    except (TypeError, ValueError):
        return default


def load_game_template(name):
    """Return the <templates> document and the template element for `name`."""
    for root in TEMPLATE_ROOTS:
        for path in root.glob("*.xml"):
            try:
                doc = ET.parse(path)
            except ET.ParseError:
                continue
            element = doc.getroot().find(name)
            if element is not None:
                return doc, element, path
    raise SystemExit("template not found: " + name)


def load_game_window(name):
    """Return the <windows> document and the window element for `name`."""
    for root in TEMPLATE_ROOTS:
        for path in root.glob("*.xml"):
            try:
                doc = ET.parse(path)
            except ET.ParseError:
                continue
            element = doc.getroot().find("window[@name='" + name + "']")
            if element is None:
                for window in doc.getroot().findall("window"):
                    if window.get("name") == name:
                        element = window
                        break
            if element is not None:
                return doc, element, path
    raise SystemExit("window not found: " + name)


def apply_patch(doc, patch_path):
    """Apply one XUi <configs> patch document to the template document."""
    patch = ET.parse(patch_path).getroot()
    namespace = "{http://www.w3.org/XML/1998/namespace}"
    for op in list(patch):
        xpath = op.get("xpath")
        if not xpath:
            continue
        targets = xpath_find(doc, xpath)
        for target in targets:
            if op.tag == "set":
                for child in list(op):
                    target.set(child.get("name"), child.get("value"))
                text = (op.text or "").strip()
                if text:
                    target.text = op.text
                    # attribute form: <set xpath=".../@attr">value</set>
                    attr = re.search(r"/@([\w]+)$", xpath)
                    if attr:
                        set_attr_on_target(doc, xpath, attr.group(1), op.text.strip())
            elif op.tag == "remove":
                parent = parent_of(doc, target)
                if parent is not None:
                    parent.remove(target)
            elif op.tag in ("append", "insertBefore", "insertAfter"):
                for node in list(op):
                    clone = copy_node(node)
                    if op.tag == "append":
                        target.append(clone)
                    else:
                        parent = parent_of(doc, target)
                        index = list(parent).index(target)
                        parent.insert(index if op.tag == "insertBefore" else index + 1, clone)
    return patch_path


def set_attr_on_target(doc, xpath, attr, value):
    element_path = xpath[: xpath.rindex("/@")]
    for target in xpath_find(doc, element_path):
        target.set(attr, value)


def copy_node(node):
    clone = ET.Element(node.tag, node.attrib)
    clone.text = node.text
    for child in node:
        clone.append(copy_node(child))
    return clone


def xpath_find(doc, xpath):
    """Support the subset the game uses: absolute element paths plus /@attr and [@name='x']."""
    if "/@" in xpath:
        base, _ = xpath.rsplit("/@", 1)
        return xpath_find(doc, base)
    parts = [p for p in xpath.strip("/").split("/") if p]
    root = doc.getroot()
    current = [root]
    if parts and parts[0] == root.tag:
        parts = parts[1:]
    if xpath.startswith("//"):
        parts = [p for p in xpath.strip("/").split("/") if p]
        current = list(root.iter())
    for part in parts:
        name = part
        predicates = re.findall(r"\[@([\w:]+)=['\"]([^'\"]*)['\"]\]", part)
        name = re.sub(r"\[.*?\]", "", part)
        nxt = []
        for node in current:
            candidates = list(node) if name == "*" else node.findall(name)
            for candidate in candidates:
                if all(candidate.get(k) == v for k, v in predicates):
                    nxt.append(candidate)
        current = nxt
    return current


def parent_of(doc, target):
    for parent in doc.iter():
        for child in parent:
            if child is target:
                return parent
    return None


class Renderer:
    def __init__(self, values, scale=1.0):
        self.values = values
        self.scale = scale
        self.font_cache = {}

    def font(self, size, bold=False):
        key = (int(size), bold)
        if key not in self.font_cache:
            candidates = ["seguisb.ttf" if bold else "segoeui.ttf", "arialbd.ttf" if bold else "arial.ttf"]
            for name in candidates:
                try:
                    self.font_cache[key] = ImageFont.truetype(name, int(size * self.scale))
                    break
                except OSError:
                    continue
            else:
                self.font_cache[key] = ImageFont.load_default()
        return self.font_cache[key]

    def visible(self, element, default=True):
        value = substitute(element.get("visible", ""), self.values)
        if value == "":
            return default
        return value.strip().lower() not in ("false", "0", "no")

    def render(self, element, fit=False):
        width = int(number(element.get("width", 0)))
        height = int(number(element.get("height", 0)))
        if width <= 0:
            width = 310
        if height <= 0:
            height = 76
        if fit:
            width, height = self.measure(element, width, height)
        img = Image.new("RGBA", (int(width * self.scale), int(height * self.scale)), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        self.draw_children(draw, element, 0.0, 0.0, width, height)
        return img

    def measure(self, element, base_w, base_h):
        """Bounding box of an element and its descendants in local pixels (y grows
        downward), so a window that overflows its own height still renders fully."""
        right, bottom = base_w, base_h

        def walk(parent, ox, oy, pw, ph):
            nonlocal right, bottom
            children = [c for c in parent if self.visible(c)]
            grid = parent.tag == "grid"
            cell_h = number(parent.get("cell_height", 0))
            for index, child in enumerate(children):
                cx_off, cy_off = self.position(child)
                cw, ch = self.size(child, pw, ph)
                if grid and cell_h:
                    cx_off, cy_off = 0.0, index * cell_h
                cx, cy = ox + cx_off, oy + cy_off
                right = max(right, cx + cw)
                bottom = max(bottom, cy + ch)
                walk(child, cx, cy, cw, ch)

        walk(element, 0.0, 0.0, base_w, base_h)
        return max(1, int(right)), max(1, int(bottom))

    def draw_children(self, draw, parent, origin_x, origin_y, parent_w, parent_h):
        children = [c for c in parent if self.visible(c)]
        children.sort(key=lambda c: int(number(c.get("depth", 0))))
        for child in children:
            self.draw_node(draw, child, origin_x, origin_y, parent_w, parent_h)

    def position(self, element):
        pos = element.get("pos")
        if not pos:
            return 0.0, 0.0
        parts = [number(p) for p in pos.split(",")]
        x = parts[0]
        y = -parts[1] if len(parts) > 1 else 0.0  # XUi y is negative downward
        return x, y

    def size(self, element, parent_w, parent_h):
        w = number(element.get("width", parent_w), parent_w)
        h = number(element.get("height", parent_h), parent_h)
        style = element.get("style", "")
        m = re.search(r"icon(\d+)px", style)
        if m:
            icon = int(m.group(1))
            if not element.get("width"):
                w = icon
            if not element.get("height"):
                h = icon
        return w, h

    def draw_node(self, draw, element, origin_x, origin_y, parent_w, parent_h):
        x_off, y_off = self.position(element)
        w, h = self.size(element, parent_w, parent_h)
        if element.get("pivot") == "center":
            x_off -= w / 2
            y_off -= h / 2
        x = origin_x + x_off * self.scale
        y = origin_y + y_off * self.scale
        w_s, h_s = w * self.scale, h * self.scale
        tag = element.tag

        if tag == "label":
            self.draw_label(draw, element, x, y, w_s, h_s)
        elif tag in ("button", "mainmenubutton") or element.get("caption_key"):
            self.draw_button(draw, element, x, y, w_s, h_s)
            self.draw_children(draw, element, x, y, w, h)
        elif tag == "texture":
            self.draw_texture(draw, element, x, y, w_s, h_s)
        elif tag == "grid":
            self.draw_grid(draw, element, x, y, w_s, h_s)
        elif tag in ("sprite", "filledsprite") or element.get("type") in ("filled", "sliced"):
            self.draw_sprite(draw, element, x, y, w_s, h_s, parent_w, parent_h)
            self.draw_children(draw, element, x, y, w, h)
        else:
            self.draw_children(draw, element, x, y, w, h)

    def draw_button(self, draw, element, x, y, w, h):
        caption = substitute(element.get("caption", ""), self.values)
        if not caption and element.get("caption_key"):
            caption = english(substitute(element.get("caption_key"), self.values))
        draw.rectangle([x, y, x + w, y + h], fill=(38, 34, 46, 235), outline=(150, 132, 92, 255), width=max(1, int(self.scale)))
        font = self.font(18)
        bbox = draw.textbbox((0, 0), caption, font=font)
        draw.text((x + (w - (bbox[2] - bbox[0])) / 2, y + (h - (bbox[3] - bbox[1])) / 2 - bbox[1]), caption, fill=(240, 240, 240, 255), font=font)

    def draw_grid(self, draw, element, x, y, w, h):
        rows = int(number(element.get("rows", len([c for c in element if c.tag in ("button", "mainmenubutton")]) or 1), 1))
        cell_h = number(element.get("cell_height", h / max(1, rows)), h / max(1, rows))
        # Children stack vertically from the grid origin.
        for index, child in enumerate([c for c in element if self.visible(c)]):
            cell_y = y + index * cell_h * self.scale
            child_w = number(child.get("width", element.get("cell_width", w)), w)
            child_h = number(child.get("height", cell_h), cell_h)
            if child.tag in ("button", "mainmenubutton") or child.get("caption_key"):
                self.draw_button(draw, child, x, cell_y, child_w * self.scale, child_h * self.scale)
            else:
                self.draw_node(draw, child, x, cell_y, child_w, child_h)
    def draw_sprite(self, draw, element, x, y, w, h, parent_w, parent_h):
        color = parse_color(element.get("color"), self.values) or (180, 180, 180, 255)
        sprite = substitute(element.get("sprite", ""), self.values).strip()
        kind = element.get("type", "sliced")
        fill = element.get("fill")
        if sprite:
            self.draw_symbol(draw, sprite, x, y, w, h, color)
            return
        if kind == "filled" and fill is not None:
            fraction = max(0.0, min(1.0, number(substitute(fill, self.values), 1.0)))
            draw.rectangle([x, y, x + w * fraction, y + h], fill=color)
        elif element.get("fillcenter", "").lower() == "false":
            draw.rectangle([x, y, x + w, y + h], outline=color, width=max(1, int(self.scale)))
        else:
            draw.rectangle([x, y, x + w, y + h], fill=color)

    def draw_texture(self, draw, element, x, y, w, h):
        label = substitute(element.get("name", "texture"), self.values)
        draw.rectangle([x, y, x + w, y + h], outline=(210, 170, 90, 255), width=max(1, int(self.scale)))
        draw.text((x + 3, y + h / 2 - 7 * self.scale), label[:12], fill=(230, 190, 110, 255), font=self.font(10))

    def draw_symbol(self, draw, sprite, x, y, w, h, color):
        draw.rectangle([x, y, x + w, y + h], outline=(90, 190, 255, 255), width=max(1, int(self.scale)))
        short = sprite.replace("ui_game_symbol_", "")[:10]
        draw.text((x + 2, y + h / 2 - 6 * self.scale), short, fill=(120, 200, 255, 255), font=self.font(9))

    def draw_label(self, draw, element, x, y, w, h):
        text = substitute(element.get("text", ""), self.values)
        if not text:
            return
        if text.startswith("{") and text.endswith("}"):
            text = english(text[1:-1])
        size = number(element.get("font_size", 16), 16)
        color = parse_color(element.get("color"), self.values) or (255, 255, 255, 255)
        justify = element.get("justify", "left")
        font = self.font(size)
        bbox = draw.textbbox((0, 0), text, font=font)
        tw = bbox[2] - bbox[0]
        tx = x
        if justify == "center":
            tx = x + (w - tw) / 2
        elif justify == "right":
            tx = x + w - tw
        draw.text((tx, y + 2 * self.scale), text, fill=color, font=font)


def load_localization():
    """Key -> English text from the game's own Localization.csv, so caption_key
    buttons preview with real labels instead of tokens."""
    mapping = {}
    path = GAME / "Data" / "Config" / "Localization.csv"
    if not path.exists():
        return mapping
    import csv
    with path.open(encoding="utf-8", errors="replace", newline="") as handle:
        reader = csv.reader(handle)
        try:
            header = next(reader)
        except StopIteration:
            return mapping
        try:
            lower = [h.strip().lower() for h in header]
            key_col = lower.index("key")
            english_col = lower.index("english")
        except ValueError:
            return mapping
        for row in reader:
            if len(row) > max(key_col, english_col) and row[english_col]:
                mapping[row[key_col]] = row[english_col]
    return mapping


LOCALIZATION = None


def english(text):
    global LOCALIZATION
    if LOCALIZATION is None:
        LOCALIZATION = load_localization()
    return LOCALIZATION.get(text, "{" + text + "}")


def find_patches(template_name, kind="template"):
    """Every mod patch file whose XPath targets this template or window."""
    hits = []
    needle = ("templates/" + template_name) if kind == "template" else ("window[@name='" + template_name + "']")
    for base in PATCH_DIRS:
        for path in base.glob("**/Config/XUi_*/*.xml"):
            try:
                text = path.read_text(errors="ignore")
            except OSError:
                continue
            if needle not in text:
                continue
            if "legacy" in path.parts:
                continue
            hits.append(path)
    # de-duplicate by resolved path, keep first occurrence order
    seen, unique = set(), []
    for path in hits:
        key = str(path.resolve()).lower()
        if key not in seen:
            seen.add(key)
            unique.append(path)
    return unique


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", default="party_entry", help="template name to preview")
    parser.add_argument("--window", help="window name to preview instead of a template")
    parser.add_argument("--patch", action="append", default=[], help="patch file(s); default = all mods")
    parser.add_argument("--values", help="JSON file of placeholder values")
    parser.add_argument("--state", choices=sorted(STATES), help="preview a built-in player state")
    parser.add_argument("--out", default="preview.png")
    parser.add_argument("--size", type=float, default=2.0, help="render scale")
    parser.add_argument("--check", action="store_true", help="headless sanity render (no image)")
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()

    if args.list:
        for root in TEMPLATE_ROOTS:
            for path in root.glob("*.xml"):
                try:
                    root_el = ET.parse(path).getroot()
                except ET.ParseError:
                    continue
                names = [c.tag for c in root_el]
                if names:
                    print(path.name + ": " + ", ".join(names))
        return

    target = args.window or args.template
    kind = "window" if args.window else "template"
    if args.window:
        doc, element, source = load_game_window(args.window)
    else:
        doc, element, source = load_game_template(args.template)
    original = element
    values = dict(DEFAULT_VALUES)
    if args.state:
        values.update(STATES[args.state])
    if args.values:
        values.update(json.loads(Path(args.values).read_text()))
    patches = [Path(p) for p in args.patch] if args.patch else find_patches(target, kind)
    applied = []
    for patch in patches:
        try:
            apply_patch(doc, patch)
            applied.append(patch)
        except Exception as error:  # keep previewing even if one patch is malformed
            print("skipped " + str(patch) + ": " + str(error))
    print("base " + kind + ": " + str(source))
    for patch in applied:
        print("applied patch: " + str(patch))
    if args.window:
        element = next((w for w in doc.getroot().findall("window") if w.get("name") == args.window), original)
    else:
        element = doc.getroot().find(args.template)
        if element is None:
            element = original
    renderer = Renderer(values, args.size)
    img = renderer.render(element, fit=bool(args.window))
    if args.check:
        # Sanity render used by the test chain: confirm geometry without writing.
        alpha = img.convert("RGBA").getchannel("A")
        opaque = sum(alpha.histogram()[1:])
        print("check: " + target + " renders " + str(img.size[0]) + "x" + str(img.size[1]) + " with " + str(opaque) + " visible pixels")
        return 0 if opaque > 100 else 1
    img.save(args.out)
    print("wrote " + args.out + " " + str(img.size))
    return 0


if __name__ == "__main__":
    sys.exit(main())
