"""Regenerate the self-hosted Material Symbols Rounded subsets after adding icon names (requires fonttools[woff]).

Writes two static instances: outline (FILL 0) and filled (FILL 1, used for active nav and status icons).
"""
import re
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

root = Path(__file__).resolve().parent.parent
source = root / "node_modules/material-symbols/material-symbols-rounded.woff2"
candidates = set()
for path in (root / "src").rglob("*"):
    if path.suffix in {".vue", ".ts"}:
        candidates.update(re.findall(r"""['"]([a-z][a-z0-9_]*)['"]""", path.read_text(encoding="utf-8")))

for fill, name in ((0, "outline"), (1, "filled")):
    font = TTFont(source)
    instantiateVariableFont(font, {"FILL": fill, "GRAD": 0, "opsz": 24, "wght": 400}, inplace=True)
    icons = sorted(candidates.intersection(font.getGlyphOrder()))
    options = subset.Options()
    options.flavor = "woff2"
    options.layout_features = ["*"]
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(glyphs=icons, text=" ".join(icons))
    subsetter.subset(font)
    destination = root / f"src/assets/material-symbols-rounded-{name}.woff2"
    destination.parent.mkdir(exist_ok=True)
    font.save(destination)
    print(f"{name}: {len(icons)} icons; {destination.stat().st_size} bytes")
