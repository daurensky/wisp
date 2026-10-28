"""Check WPF resource references and design-token invariants without loading WPF."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
desktop = root / "src" / "Wisp.Desktop"
files = sorted(desktop.glob("*.xaml")) + sorted((desktop / "Design").glob("*.xaml"))
keys = set()
documents = []
for path in files:
    document = ET.parse(path)
    local_keys = set()
    for element in document.iter():
        key = element.get("{http://schemas.microsoft.com/winfx/2006/xaml}Key")
        if key:
            assert key not in local_keys, f"Duplicate key {key} in {path}"
            local_keys.add(key)
    keys.update(local_keys)
    documents.append((path, document))

for path, document in documents:
    for element in document.iter():
        for value in element.attrib.values():
            for key in re.findall(r"\{(?:Static|Dynamic)Resource ([\w.]+)\}", value):
                assert key in keys, f"Unknown resource {key} in {path}"
        if path.name == "Tokens.xaml" and element.tag.endswith("Thickness"):
            for value in (element.text or "").split(","):
                assert float(value) % 4 == 0, f"Off-grid spacing {element.text}"
        if path.name == "Tokens.xaml" and element.tag.endswith("CornerRadius"):
            assert float(element.text or "0") in (4, 8, 12)

assert (root / "docs" / "licenses" / "Lucide.txt").is_file()
print(f"WPF resources: {len(keys)} named resources, no duplicates or broken references; spacing/radii valid.")
