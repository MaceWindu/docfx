"""Site figures for the PR: API pages, pages with Applies to, member markers, pages showing a variant."""
import sys
from collections import Counter
from pathlib import Path

api = Path(sys.argv[1]) / 'api'
pages = applies = markers = variant_pages = variants = 0
ef = Counter()
for path in api.rglob('*.html'):
    if path.name in ('index.html', 'toc.html'):
        continue
    text = path.read_text(encoding='utf-8')
    pages += 1
    if path.parent.name.startswith('linq2db.efcore'):
        ef[path.parent.name] += 1
    if '<dt>Applies to</dt>' in text:
        applies += 1
    markers += text.count('class="applies-to ')
    n = text.count('class="api-variant"')
    if n:
        variant_pages += 1
        variants += n
print(f'pages={pages} appliesTo={applies} memberMarkers={markers} variantPages={variant_pages} variantBlocks={variants} ef={dict(ef)}')
