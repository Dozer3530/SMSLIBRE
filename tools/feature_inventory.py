"""Turn the 835 CHM topic filenames into a categorized SMS feature inventory."""

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
topics_file = ROOT / "analysis" / "chm_topics.txt"

# Ordered category rules: (label, regex over the cleaned topic name).
CATS = [
    ("Import / read data",        r"import|read data|assign .*import|assign columns|open data|card|download|convert"),
    ("Export / write data",       r"export|write .*card|save to|send to"),
    ("Prescriptions / VRA",       r"prescription|variable rate|\bvra\b|target rate|write.*rx"),
    ("Analysis",                  r"analy|aggregate|equation|formula|filter|cross.?reference|zone|multi.?year|compare|comparison|statistic|profit|cost analysis"),
    ("Mapping & visualization",   r"\bmap|layer|legend|theme|display|view settings|zoom|pan|render|attribute options|classif|color|shade|grid|contour|surface|hotspot"),
    ("3D",                        r"3d"),
    ("Editing & cleaning",        r"edit|adjust|calibrat|clean|delay|offset|swath|overlap|delete|move|split|merge|smooth|recalc"),
    ("Boundaries",                r"boundary|boundaries|headland|drainage tile"),
    ("Guidance",                  r"guidance|swath pattern|\bab line|guidance pattern"),
    ("Field trials",              r"field trial|trial|replicat|strip"),
    ("Scouting / pests / notes",  r"scout|pest|note|flag|photo|picture|observation"),
    ("Soil / sampling",           r"soil|sampl|grid sample|tissue|nutrient"),
    ("Weather",                   r"weather|rain|climate"),
    ("Products / inputs",         r"product|input|seed|fertil|chemical|mix|blend|variety|hybrid"),
    ("Equipment / config",        r"equipment|vehicle|implement|machine|setup configuration|operating config|attachment|monitor|controller|device"),
    ("Management tree / org",     r"grower|farm|\bfield\b|season|management item|person|operator|tree"),
    ("Jobs / tasks / calendar",   r"job|task|calendar|schedul|resource tracking|work order"),
    ("Financial",                 r"financ|expense|income|cost|budget|invoice|account|price"),
    ("Reports / print / charts",  r"report|print|chart|graph|summary|page setup|page$|document window"),
    ("AgFiniti / cloud",          r"agfiniti|cloud|sync|web|online"),
    ("Setup / preferences",       r"setting|option|preference|configur|units|projection|coordinate|default|customize|toolbar|ribbon|template"),
    ("File / database / backup",  r"backup|restore|database|\bfile\b|new .*database|repair|archive|merge database"),
    ("Wizards & dialogs (misc)",  r"wizard|dialog|window|\bmenu\b"),
]


def clean(name: str) -> str:
    n = name.lstrip("/")
    n = re.sub(r"\.html?$", "", n, flags=re.I)
    n = n.replace("_", " ").replace("...", "").strip()
    n = re.sub(r"\s+", " ", n)
    return n


def categorize(name: str) -> str:
    low = name.lower()
    for label, pat in CATS:
        if re.search(pat, low):
            return label
    return "Other"


topics = [clean(t) for t in topics_file.read_text(encoding="utf-8").splitlines() if t.strip()]
# Drop pure navigation/boilerplate topics.
skip = re.compile(r"^(welcome|copyright|trademark|legal|getting started|what's new|"
                  r"table of contents|index|glossary|contacting|technical support|"
                  r"overview|introduction|home)$", re.I)
topics = [t for t in topics if t and not skip.match(t)]

groups: dict[str, list[str]] = {label: [] for label, _ in CATS}
groups["Other"] = []
for t in sorted(set(topics)):
    groups[categorize(t)].append(t)

lines = ["# SMS — Full Feature Inventory (from the help file)",
         "",
         f"Extracted from `ALMapping.chm` — **{len(set(topics))} distinct help topics**, "
         "each a dialog / wizard / function in SMS. This is the authoritative "
         "breadth list for the parity goal. Grouped by functional area below; "
         "raw list in `analysis/chm_topics.txt`.",
         ""]
for label, _ in CATS:
    items = groups[label]
    if not items:
        continue
    lines.append(f"## {label}  ({len(items)})")
    lines.append("")
    lines += [f"- {t}" for t in items]
    lines.append("")
if groups["Other"]:
    lines.append(f"## Other / uncategorized  ({len(groups['Other'])})")
    lines.append("")
    lines += [f"- {t}" for t in groups["Other"]]
    lines.append("")

out = ROOT / "notes" / "SMS_FEATURE_INVENTORY.md"
out.write_text("\n".join(lines), encoding="utf-8")

print(f"{len(set(topics))} distinct features")
print("\nBy category:")
for label, _ in CATS:
    if groups[label]:
        print(f"  {label:32} {len(groups[label]):3d}")
if groups["Other"]:
    print(f"  {'Other':32} {len(groups['Other']):3d}")
print(f"\nwrote {out}")
