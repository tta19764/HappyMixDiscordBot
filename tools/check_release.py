"""Validate tagged release content and pull-request branch names."""
import json
import os
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
branch = os.environ.get('HEAD_REF', '')
if branch and not re.fullmatch(r'(feature|fix)/[a-z0-9][a-z0-9._/-]*', branch):
    raise SystemExit('Use a feature/<name> or fix/<name> branch for pull requests.')

version = os.environ.get('REF_NAME', '') if os.environ.get('REF_TYPE') == 'tag' else None
if version and not re.fullmatch(r'v\d+\.\d+\.\d+', version):
    raise SystemExit('Release tags must use vMAJOR.MINOR.PATCH.')
files = [root / 'src/HappyMix.Bot/Releases' / (version + '.json')] if version else list((root / 'src/HappyMix.Bot/Releases').glob('*.json'))
for path in files:
    notes = json.loads(path.read_text(encoding='utf-8'))
    if set(notes) != {'en', 'uk', 'ru'}:
        raise SystemExit(f'{path.name}: provide en, uk and ru notes.')
    for language, pages in notes.items():
        if not isinstance(pages, list) or not pages or any(not isinstance(page, str) or not page.strip() or len(page) > 4000 for page in pages):
            raise SystemExit(f'{path.name}/{language}: use nonempty pages of up to 4000 characters.')
print('Release notes and branch naming validated.')
