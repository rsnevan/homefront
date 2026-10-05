"""Builds docs/handbook/homefront-handbook.pdf from the Markdown docs, using Edge to print.

    pip install markdown
    python tools/build-handbook.py
"""
import html, pathlib, re, subprocess, datetime
import markdown

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
OUT = DOCS / "handbook"
OUT.mkdir(exist_ok=True)

CHAPTERS = [
    ("user-guide.md", "Living with homefront"),
    ("install.md", "Setting up a home"),
    ("automations.md", "How the house thinks"),
    ("remote-access.md", "Reaching home from anywhere"),
    ("troubleshooting.md", "When something's off"),
]

def slug(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")

body, toc = [], []
for i, (name, kicker) in enumerate(CHAPTERS, 1):
    md = (DOCS / name).read_text(encoding="utf-8")
    title = re.match(r"#\s+(.+)", md).group(1)
    md = re.sub(r"^#\s+.+\n", "", md, count=1)
    h = markdown.markdown(md, extensions=["tables", "fenced_code", "toc"])
    # Links between pages become links inside the book; images live one folder up.
    h = re.sub(r'href="([a-z-]+)\.md(#[^"]*)?"', lambda m: f'href="#{m.group(2)[1:] if m.group(2) else "ch-" + m.group(1)}"', h)
    h = h.replace('src="screenshots/', 'src="../screenshots/')
    h = re.sub(r'<p><img ([^>]*)alt="([^"]*)"([^>]*)></p>', r'<figure><img \1alt="\2"\3><figcaption>\2</figcaption></figure>', h)
    sid = "ch-" + name[:-3]
    toc.append(f'<li><a href="#{sid}"><span class="n">{i:02d}</span><span class="t">{html.escape(title)}</span><span class="k">{kicker}</span></a></li>')
    body.append(f'''
<section class="chapter" id="{sid}">
  <header class="ch-head">
    <div class="ch-num">{i:02d}</div>
    <div><p class="ch-kicker">{kicker}</p><h1>{html.escape(title)}</h1></div>
  </header>
  {h}
</section>''')

today = datetime.date.today().strftime("%B %Y")
page = f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<title>homefront Handbook</title>
<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Inter+Tight:wght@400;500;600&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@400&display=swap" rel="stylesheet">
<style>
:root {{ --ink:#0f1726; --ink-2:#3b475c; --muted:#6b778a; --paper:#ffffff; --wash:#f3f6fa; --line:#dfe5ee; --lamp:#ffc94d; --lamp-deep:#b58300; }}
@page {{ size: A4; margin: 20mm 18mm 22mm; @bottom-left {{ content: "homefront Handbook"; font: 8pt Inter; color: #6b778a; }} @bottom-right {{ content: counter(page); font: 8pt Inter; color: #6b778a; }} }}
@page :first {{ margin: 0; @bottom-left {{ content: none; }} @bottom-right {{ content: none; }} }}
* {{ box-sizing: border-box; }}
html {{ -webkit-print-color-adjust: exact; print-color-adjust: exact; }}
body {{ margin: 0; font: 10pt/1.55 Inter, system-ui, sans-serif; color: var(--ink); background: var(--paper); }}
h1, h2, h3 {{ font-family: "Inter Tight", Inter, sans-serif; font-weight: 500; letter-spacing: -0.015em; color: var(--ink); }}
h2 {{ font-size: 15pt; margin: 22pt 0 6pt; break-after: avoid; }}
h3 {{ font-size: 11.5pt; margin: 14pt 0 4pt; break-after: avoid; }}
p, li {{ max-width: 150mm; }}
a {{ color: var(--ink); text-decoration-color: var(--lamp); text-decoration-thickness: 1.5pt; text-underline-offset: 2pt; }}
code {{ font: 8.6pt "JetBrains Mono", Consolas, monospace; background: var(--wash); padding: 1pt 3pt; border-radius: 3pt; }}
pre {{ background: var(--ink); color: #e6ecf4; padding: 9pt 11pt; border-radius: 6pt; overflow: hidden; white-space: pre-wrap; break-inside: avoid; }}
pre code {{ background: none; color: inherit; padding: 0; font-size: 8pt; }}
table {{ width: 100%; border-collapse: collapse; margin: 8pt 0 12pt; font-size: 8.8pt; break-inside: auto; }}
th {{ text-align: left; font-weight: 600; color: var(--ink-2); border-bottom: 1.2pt solid var(--ink); padding: 5pt 6pt; }}
td {{ border-bottom: 0.6pt solid var(--line); padding: 5pt 6pt; vertical-align: top; }}
tr {{ break-inside: avoid; }}
figure {{ margin: 10pt 0 14pt; break-inside: avoid; }}
figure img {{ max-width: 100%; max-height: 120mm; display: block; border-radius: 6pt; border: 0.6pt solid var(--line); background: var(--ink); }}
figcaption {{ font-size: 8pt; color: var(--muted); margin-top: 4pt; }}
ol, ul {{ padding-left: 14pt; }}
li {{ margin: 2pt 0; }}

/* cover: the house as a floor plan at night */
.cover {{ height: 297mm; width: 210mm; background: var(--ink); color: #e6ecf4; position: relative; overflow: hidden; break-after: page; }}
.cover svg {{ position: absolute; inset: 0; width: 100%; height: 100%; }}
.cover .words {{ position: absolute; left: 20mm; right: 20mm; bottom: 26mm; }}
.cover .mark {{ font: 500 54pt/1 "Inter Tight"; letter-spacing: -0.035em; margin: 0; color: #f4f7fb; }}
.cover .sub {{ font: 400 17pt/1.3 "Inter Tight"; color: var(--lamp); margin: 6mm 0 0; }}
.cover .line {{ font-size: 10pt; color: #9aa6b8; margin-top: 9mm; max-width: 120mm; }}
.cover .ed {{ position: absolute; right: 20mm; top: 18mm; font-size: 8.5pt; color: #9aa6b8; letter-spacing: 0.02em; }}

/* contents */
.contents {{ break-after: page; padding-top: 14mm; }}
.contents h1 {{ font-size: 26pt; margin: 0 0 10mm; }}
.contents ol {{ list-style: none; padding: 0; margin: 0; }}
.contents li {{ max-width: none; border-top: 0.6pt solid var(--line); }}
.contents a {{ display: grid; grid-template-columns: 16mm 1fr; grid-template-rows: auto auto; padding: 5mm 0; text-decoration: none; }}
.contents .n {{ grid-row: span 2; font: 500 20pt "Inter Tight"; color: var(--lamp-deep); }}
.contents .t {{ font: 500 14pt "Inter Tight"; }}
.contents .k {{ color: var(--muted); font-size: 9.5pt; }}
.contents .about {{ margin-top: 14mm; color: var(--ink-2); max-width: 140mm; }}

/* chapters */
.chapter {{ break-before: page; }}
.ch-head {{ display: grid; grid-template-columns: auto 1fr; gap: 7mm; align-items: end; border-bottom: 1.2pt solid var(--ink); padding-bottom: 6mm; margin-bottom: 8mm; }}
.ch-num {{ font: 500 54pt/0.85 "Inter Tight"; color: var(--lamp); letter-spacing: -0.04em; -webkit-text-stroke: 0.6pt var(--lamp-deep); }}
.ch-kicker {{ margin: 0 0 1mm; color: var(--muted); font-size: 9.5pt; }}
.ch-head h1 {{ margin: 0; font-size: 26pt; line-height: 1.05; }}
</style></head>
<body>

<div class="cover">
  <svg viewBox="0 0 210 297" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
    <defs>
      <radialGradient id="glowA" cx="0.62" cy="0.7" r="0.75"><stop offset="0" stop-color="#ffc94d" stop-opacity="0.55"/><stop offset="1" stop-color="#ffc94d" stop-opacity="0"/></radialGradient>
      <radialGradient id="glowB" cx="0.4" cy="0.6" r="0.8"><stop offset="0" stop-color="#ffb347" stop-opacity="0.32"/><stop offset="1" stop-color="#ffb347" stop-opacity="0"/></radialGradient>
    </defs>
    <g transform="translate(22 40)">
      <rect x="0" y="0" width="98" height="70" fill="url(#glowB)"/>
      <rect x="0" y="70" width="166" height="76" fill="url(#glowA)"/>
      <g fill="none" stroke="#e6ecf4" stroke-opacity="0.85" stroke-width="0.6">
        <rect x="0" y="0" width="166" height="146"/>
        <path d="M98 0V70M0 70H166M98 36H166"/>
      </g>
      <g fill="#9aa6b8" font-family="Inter" font-size="3.4">
        <text x="5" y="8">Kitchen</text><text x="103" y="8">Bedroom</text><text x="103" y="44">Study</text><text x="5" y="78">Living Room</text>
      </g>
      <circle cx="83" cy="146" r="2.2" fill="#ffc94d"/>
    </g>
  </svg>
  <p class="ed">Edition {today}</p>
  <div class="words">
    <p class="mark">homefront</p>
    <p class="sub">The Handbook</p>
    <p class="line">Lights, the TV, the library, the PC, the camera and the phone in your pocket, from one place. How to live with it, set it up and keep it running.</p>
  </div>
</div>

<div class="contents">
  <h1>Contents</h1>
  <ol>{"".join(toc)}</ol>
  <p class="about">homefront runs on the Windows PC behind the TV. Home Assistant handles the devices and the timing underneath it, Jellyfin holds the library, and Tailscale makes all of it reachable from anywhere without opening anything to the internet. Every part is free software.</p>
</div>

{"".join(body)}
</body></html>'''

src = OUT / "homefront-handbook.html"
src.write_text(page, encoding="utf-8")
pdf = OUT / "homefront-handbook.pdf"
edge = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
subprocess.run([edge, "--headless=new", "--disable-gpu", "--no-pdf-header-footer", "--virtual-time-budget=8000",
                f"--print-to-pdf={pdf}", src.as_uri()], check=True, capture_output=True, timeout=120)
print("wrote", pdf, pdf.stat().st_size // 1024, "KB")
