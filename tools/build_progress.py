"""Generate an offline progress dashboard from the deliberately simple roadmap tables.

Usage: python tools/build_progress.py [--check]
Standard library only. No network calls or browser-local state.
"""
from pathlib import Path
import html
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'docs' / 'ROADMAP.md'
OUTPUT = ROOT / 'docs' / 'progress.html'
STATUSES = {'done', 'partial', 'planned', 'blocked'}


def read_rows(source, heading, size):
    section = source.split('## ' + heading + '\n', 1)[1].split('\n## ', 1)[0]
    rows = []
    for line in section.splitlines():
        if not line.startswith('|'):
            continue
        cells = [cell.strip() for cell in line.strip().strip('|').split('|')]
        if cells[0] in {'ID', 'Day'} or re.fullmatch(r'[- :]+', cells[0]):
            continue
        if len(cells) != size or cells[2] not in STATUSES:
            raise ValueError('Invalid roadmap row: ' + line)
        rows.append(cells)
    if not rows:
        raise ValueError('Empty roadmap section: ' + heading)
    return rows


source = SOURCE.read_text(encoding='utf-8')
baseline = read_rows(source, 'Current baseline', 5)
plan = read_rows(source, 'Next 30 work slots', 6)
if [row[0] for row in plan] != [str(i) for i in range(1, 31)]:
    raise ValueError('Plan must contain unique ordered slots 1 through 30')
ids = {row[0] for row in baseline} | {row[0] for row in plan}
for row in plan:
    if any(dep.strip() not in ids for dep in row[5].split(',')):
        raise ValueError('Unknown dependency for day ' + row[0])
reviewed = re.search(r'Reviewed: ([0-9-]+)', source).group(1)
payload = json.dumps({'baseline': baseline, 'plan': plan}, ensure_ascii=True).replace('<', '\\u003c')
template = r'''<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>HappyMix · Implementation progress</title>
<style>
:root{color-scheme:dark;font:16px/1.55 system-ui,sans-serif;background:#101420;color:#e8edf7}*{box-sizing:border-box}body{max-width:1250px;margin:auto;padding:36px 24px}h1{font-size:clamp(2rem,5vw,3.5rem);line-height:1.1;margin:12px 0}h2{margin-top:32px}a{color:#a7c5ff}.muted,small{color:#a9b5ca}.eyebrow{color:#92e5cb;text-transform:uppercase;letter-spacing:.15em;font-size:.8rem}.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px;margin:28px 0}.stat,.note{background:#1a2233;border:1px solid #303d55;border-radius:14px;padding:20px}.stat strong{display:block;font-size:2rem}progress{width:100%;accent-color:#7dddc0;height:12px}.controls{display:flex;gap:12px;flex-wrap:wrap;position:sticky;top:0;background:#101420;padding:14px 0;z-index:1}input,select,button{font:inherit;color:inherit;background:#1a2233;border:1px solid #566582;border-radius:8px;padding:10px}input{flex:1;min-width:180px}button{cursor:pointer}.table-wrap{overflow:auto}table{border-collapse:collapse;width:100%;font-size:.9rem}th,td{text-align:left;vertical-align:top;padding:14px 10px;border-bottom:1px solid #303d55}th{color:#a9b5ca}td:nth-child(4){min-width:190px}td:nth-child(5){min-width:280px}.badge{display:inline-block;border-radius:20px;padding:3px 10px;white-space:nowrap;font-size:.78rem;background:#29364c}.done{background:#164d40;color:#a2f1d7}.partial{background:#544119;color:#ffdc91}.blocked{background:#5a2837;color:#ffb8c7}.planned{color:#c0cbe0}details{margin:24px 0}summary{cursor:pointer;font-weight:600}#empty{padding:24px}.next{border-left:4px solid #92e5cb}footer{margin:32px 0} @media print{.controls,button{display:none}body{background:white;color:black}td,th{color:black}.table-wrap{overflow:visible}.stat,.note{background:white;color:black}details{display:block}}
</style>
<header><div class="eyebrow">HappyMix / Build journal</div><h1>A gaming hub, one useful step at a time.</h1>
<p class="muted">Source reviewed __DATE__ · 30 adjustable work slots · 5–20 minutes of daily human review</p>
<p>Availability → shared games → buying votes → groups → sessions → esports watch parties.</p></header>
<noscript>This dashboard requires JavaScript. Read <a href="ROADMAP.md">the Markdown roadmap</a> instead.</noscript>
<section class="stats" id="stats" aria-label="Progress totals"></section>
<section class="note next" id="next"></section>
<p class="muted">Counts reflect the Markdown snapshot, not live bot telemetry. Partial items receive no completion credit. Each slot counts equally; this is not an effort estimate.</p>
<details><summary>Implemented baseline and known limits</summary><div class="table-wrap"><table><thead><tr><th>ID</th><th>Area</th><th>Status</th><th>Capability</th><th>Evidence / limitation</th></tr></thead><tbody id="baseline"></tbody></table></div></details>
<h2>Next 30 work slots</h2>
<div class="controls"><input id="search" aria-label="Search roadmap" placeholder="Search features, acceptance criteria…"><select id="status" aria-label="Filter status"><option value="">All statuses</option><option>planned</option><option>partial</option><option>done</option><option>blocked</option></select><select id="area" aria-label="Filter area"><option value="">All areas</option></select><button id="reset">Reset filters</button></div>
<p id="count" class="muted" aria-live="polite"></p>
<div class="table-wrap"><table><thead><tr><th>Day</th><th>Area</th><th>Status</th><th>Deliverable</th><th>Acceptance criterion</th><th>Depends on</th></tr></thead><tbody id="plan"></tbody></table></div><p id="empty" hidden>No matching work slots.</p>
<section class="note"><strong>Keep the plan current</strong><p>Edit <a href="ROADMAP.md">docs/ROADMAP.md</a>, update status and evidence, then run <code>python tools/build_progress.py</code>. Commit both files. Reopen or refresh this page. Filters never modify the roadmap.</p><p>Hosting and provider access are validation gates. The roadmap includes deployment, recovery, authorization, and spending checks; source code alone does not establish production readiness.</p></section>
<footer class="muted">Offline by design · no external libraries, analytics, API keys, or hosting required · <a href="ROADMAP.md">Full scope, decisions and stretch backlog</a></footer>
<script id="data" type="application/json">__DATA__</script>
<script>
const data=JSON.parse(document.getElementById('data').textContent);
const $=id=>document.getElementById(id);
function cell(text){const td=document.createElement('td');td.textContent=text;return td}
function rows(target,items){$(target).replaceChildren();for(const row of items){const tr=document.createElement('tr');row.forEach((value,i)=>{const td=cell(value);if(i===2){const badge=document.createElement('span');badge.className='badge '+value;badge.textContent=value;td.replaceChildren(badge)}tr.append(td)});$(target).append(tr)}}
const done=items=>items.filter(r=>r[2]==='done').length;
for(const [title,items] of [['Baseline capabilities',data.baseline],['Future plan completed',data.plan]]){const box=document.createElement('div');box.className='stat';const label=document.createElement('span');label.textContent=title;const value=document.createElement('strong');value.textContent=`${done(items)} / ${items.length}`;const progress=document.createElement('progress');progress.max=items.length;progress.value=done(items);progress.setAttribute('aria-label',title);const sub=document.createElement('small');sub.textContent=`${items.filter(r=>r[2]==='partial').length} partial · ${items.filter(r=>r[2]==='blocked').length} blocked`;box.append(label,value,progress,sub);$('stats').append(box)}
const complete=new Set([...data.baseline,...data.plan].filter(r=>r[2]==='done').map(r=>r[0]));
const next=data.plan.find(r=>r[2]!=='done'&&r[2]!=='blocked'&&r[5].split(',').every(d=>complete.has(d.trim())));
$('next').textContent=next?`Next ready slot · Day ${next[0]}: ${next[3]}. ${next[4]}`:'No ready slot: review incomplete dependencies or blocked items in the roadmap.';
rows('baseline',data.baseline);
for(const area of [...new Set(data.plan.map(r=>r[1]))].sort()){const option=document.createElement('option');option.value=area;option.textContent=area;$('area').append(option)}
function render(){const term=$('search').value.toLowerCase().trim();const items=data.plan.filter(r=>(!$('status').value||r[2]===$('status').value)&&(!$('area').value||r[1]===$('area').value)&&r.join(' ').toLowerCase().includes(term));rows('plan',items);$('count').textContent=`Showing ${items.length} of ${data.plan.length} slots`;$('empty').hidden=items.length>0}
for(const id of ['search','status','area'])$(id).addEventListener('input',render);
$('reset').addEventListener('click',()=>{for(const id of ['search','status','area'])$(id).value='';render()});render();
</script></html>
'''
output = template.replace('__DATE__', html.escape(reviewed)).replace('__DATA__', payload)
if '--check' in sys.argv:
    if not OUTPUT.exists() or OUTPUT.read_text(encoding='utf-8') != output:
        raise SystemExit('Dashboard is stale. Run python tools/build_progress.py')
    print('Dashboard matches roadmap; 30 slots and dependencies validated.')
else:
    OUTPUT.write_text(output, encoding='utf-8')
    print('Generated docs/progress.html')
