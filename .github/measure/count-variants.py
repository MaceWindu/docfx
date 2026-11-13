"""Count variants in ManagedReference YAML: items with variants and variants per entry, and list the uids."""
import re
import sys
from collections import Counter
from pathlib import Path

root = Path(sys.argv[1])
per_entry = Counter()
items = []
for path in root.rglob('*.yml'):
    if path.name == 'toc.yml':
        continue
    lines = path.read_text(encoding='utf-8').splitlines()
    uid = None
    i = 0
    while i < len(lines):
        line = lines[i]
        m = re.match(r'^- uid: (.+)$', line)
        if m:
            uid = m.group(1).strip()
        if line == '  variants:':
            count = 0
            j = i + 1
            while j < len(lines) and (lines[j].startswith('  - ') or lines[j].startswith('    ')):
                if lines[j].startswith('  - '):
                    count += 1
                j += 1
            items.append((path.parent.name, uid, count))
            per_entry[path.parent.name] += count
            i = j
            continue
        i += 1

print(f'variants={sum(c for _, _, c in items)} items={len(items)}')
for entry, n in per_entry.most_common():
    print(f'  {entry}: {n}')
for entry, uid, n in sorted(items):
    print(f'{entry}\t{n}\t{uid}')
