"""Regenerate the self-hosted icon subset after adding icon names (requires fonttools[woff])."""
import re
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

root = Path(__file__).resolve().parent.parent
font = TTFont(root / "node_modules/material-symbols/material-symbols-sharp.woff2")
instantiateVariableFont(font, {"FILL": 0, "GRAD": 0, "opsz": 24, "wght": 400}, inplace=True)
candidates = set()
for path in (root / "src").rglob("*"):
    if path.suffix in {".vue", ".ts"}:
        candidates.update(re.findall(r"""['"]([a-z][a-z0-9_]*)['"]""", path.read_text(encoding="utf-8")))
icons = sorted(candidates.intersection(font.getGlyphOrder()))
options = subset.Options()
options.flavor = "woff2"
subsetter = subset.Subsetter(options=options)
subsetter.populate(glyphs=icons, text=" ".join(icons))
subsetter.subset(font)
destination = root / "src/assets/material-symbols-sharp-subset.woff2"
destination.parent.mkdir(exist_ok=True)
font.save(destination)
print(f"{len(icons)} icons; {destination.stat().st_size} bytes")
