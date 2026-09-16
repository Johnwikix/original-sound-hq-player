"""Copy versioned legal text to the website and regenerate the legacy policy URL.

Usage: python _tools/LegalContent/sync.py --website PATH [--check]
The app's Legal/*.json files are the text source of truth. No publishing is performed.
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--website', type=Path, required=True)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
site = args.website.resolve()
assert (site / 'src/views/DisclaimerPage.vue').is_file(), 'Expected OriginalSoundPlayer website'


def write(path, text):
    if args.check:
        assert path.read_text(encoding='utf-8') == text, f'Out of sync: {path}'
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')


bundles = []
for language in ['zh-CN', 'en']:
    source = ROOT / 'Legal' / f'legal.{language}.json'
    content = source.read_text(encoding='utf-8')
    bundle = json.loads(content)
    assert [d['id'] for d in bundle['documents']] == ['terms', 'privacy', 'disclaimer']
    bundles.append(bundle)
    write(site / 'src/data/legal' / source.name, content)
assert bundles[0]['version'] == bundles[1]['version']
assert f'CurrentVersion = "{bundles[0]["version"]}"' in (ROOT / 'Services/AgreementAcceptanceStore.cs').read_text(encoding='utf-8')

# Retain the existing public GitHub policy.md URL without a stale generated template.
policy = '# Privacy Policy / 隐私政策\n\n'
for bundle in bundles:
    doc = next(d for d in bundle['documents'] if d['id'] == 'privacy')
    policy += f"## {doc['title']}\n\n{bundle['version']}\n\n{doc['summary']}\n\n"
    for section in doc['sections']:
        policy += f"### {section['title']}\n\n" + '\n\n'.join(section['paragraphs']) + '\n\n'
write(ROOT / 'policy.md', policy.rstrip() + '\n')
print('Legal documents and policy.md are in sync.' if args.check else 'Updated versioned legal documents and policy.md.')
