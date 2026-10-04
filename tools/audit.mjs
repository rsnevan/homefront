// Layout audit: overlaps, overflow, clipping, horizontal scroll and small tap targets,
// across viewports and UI states. Usage: node audit.mjs [--shots]
import { chromium } from 'playwright-core';
import fs from 'fs';

const BASE = process.env.HF_URL || 'http://localhost:8090';
const LAN = process.env.HF_LAN;   // this PC's LAN address (non-loopback), to audit the login page; skipped if unset
const SHOTS = process.argv.includes('--shots');
const OUT = new URL('./audit-out/', import.meta.url);
fs.mkdirSync(OUT, { recursive: true });

const VIEWPORTS = [
  [320, 640, 'phone-xs'], [360, 780, 'phone-s'], [390, 844, 'phone'], [430, 932, 'phone-l'],
  [768, 1024, 'tablet'], [1024, 768, 'tablet-l'], [1280, 800, 'laptop'], [1440, 900, 'desktop'], [1920, 1080, 'tv'],
];

const LONG = 'The Extraordinarily Long Title Of A Film That Keeps Going Well Past Any Reasonable Width';

// Runs in the page: returns a list of problems.
function audit() {
  const issues = [];
  const de = document.documentElement;
  const vw = de.clientWidth, vh = innerHeight;
  if (de.scrollWidth > vw + 1) issues.push({ kind: 'horizontal-scroll', detail: `${de.scrollWidth - vw}px` });

  const scope = document.querySelector('.sheet') || document.body;
  const isVisible = el => {
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0.5 && r.height > 0.5;
  };
  // Visible part of an element after clipping by overflow ancestors.
  const clipRect = el => {
    let r = el.getBoundingClientRect();
    let box = { l: r.left, t: r.top, r: r.right, b: r.bottom };
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      const cs = getComputedStyle(p);
      if (cs.overflowX !== 'visible' || cs.overflowY !== 'visible') {
        const pr = p.getBoundingClientRect();
        box = { l: Math.max(box.l, pr.left), t: Math.max(box.t, pr.top), r: Math.min(box.r, pr.right), b: Math.min(box.b, pr.bottom) };
      }
    }
    return box.r - box.l > 0.5 && box.b - box.t > 0.5 ? box : null;
  };
  const label = el => {
    const t = (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 40);
    return `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''} "${t}"`;
  };

  const all = [...scope.querySelectorAll('*')].filter(isVisible);
  const textLeaves = all.filter(el => [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim()) && !el.closest('svg') && !el.closest('.sr'));
  const controls = all.filter(el => el.matches('button, a[href], input, select, textarea, [role=button], label.toggle'));

  // 1. Text overflowing its own box (and not deliberately truncated).
  for (const el of textLeaves) {
    const cs = getComputedStyle(el);
    if (el.scrollWidth > el.clientWidth + 2 && cs.overflowX === 'visible' && cs.display !== 'inline' && cs.whiteSpace !== 'normal')
      issues.push({ kind: 'text-overflow', detail: label(el), px: el.scrollWidth - el.clientWidth });
    if (el.scrollWidth > el.clientWidth + 2 && cs.overflowX !== 'visible' && cs.textOverflow !== 'ellipsis' && !el.matches('input,textarea,select'))
      issues.push({ kind: 'text-clipped', detail: label(el), px: el.scrollWidth - el.clientWidth });
  }

  // 2. Elements sticking out past the viewport's right edge (outside horizontal scrollers).
  for (const el of [...textLeaves, ...controls]) {
    const r = el.getBoundingClientRect();
    let inScroller = false;
    for (let p = el.parentElement; p; p = p.parentElement) { const ox = getComputedStyle(p).overflowX; if (ox === 'auto' || ox === 'scroll' || ox === 'hidden') { inScroller = true; break; } }
    if (!inScroller && (r.right > vw + 1 || r.left < -1)) issues.push({ kind: 'off-screen', detail: label(el), px: Math.round(Math.max(r.right - vw, -r.left)) });
  }

  // 3. Overlaps between text and controls that aren't nested in each other.
  const boxesOf = el => {
    const full = clipRect(el); if (!full) return [];
    if (getComputedStyle(el).display !== 'inline') return [full];
    return [...el.getClientRects()].map(r => ({ l: Math.max(r.left, full.l), t: Math.max(r.top, full.t), r: Math.min(r.right, full.r), b: Math.min(r.bottom, full.b) })).filter(b => b.r - b.l > 0.5 && b.b - b.t > 0.5);
  };
  const cands = [...new Set([...textLeaves, ...controls])].map(el => ({ el, boxes: boxesOf(el) })).filter(c => c.boxes.length);
  const overlapsIgnored = el => el.closest('.live-dot, .ripple, .tag, .badge, .plan-cta, .osd, .toasts, .offline, .upnext, .tabbar, .lib-hero .veil');
  for (let i = 0; i < cands.length; i++) {
    for (let j = i + 1; j < cands.length; j++) {
      const a = cands[i], b = cands[j];
      if (a.el.contains(b.el) || b.el.contains(a.el)) continue;
      if (overlapsIgnored(a.el) || overlapsIgnored(b.el)) continue;
      let hit = null;
      for (const x of a.boxes) for (const y of b.boxes) {
        const w = Math.min(x.r, y.r) - Math.max(x.l, y.l), h = Math.min(x.b, y.b) - Math.max(x.t, y.t);
        if (w > 2 && h > 2) hit = `${Math.round(w)}x${Math.round(h)}`;
      }
      if (hit) issues.push({ kind: 'overlap', detail: `${label(a.el)}  x  ${label(b.el)}`, px: hit });
    }
  }

  // 4. Small touch targets on touch-sized screens.
  if (vw <= 1024) for (const c of controls) {
    if (c.closest('.overview, p')) continue;
    const r = c.getBoundingClientRect();
    if (c.matches('input[type=range]') ? r.height < 28 : (r.width < 32 || r.height < 32))
      issues.push({ kind: 'small-target', detail: label(c), px: `${Math.round(r.width)}x${Math.round(r.height)}` });
  }

  // 5. Fixed tab bar must not hide the end of the page.
  const tab = document.querySelector('.tabbar');
  if (tab && isVisible(tab) && !document.querySelector('.sheet')) {
    window.scrollTo(0, de.scrollHeight);
    const lastContent = [...document.querySelectorAll('main > *')].filter(isVisible).pop();
    if (lastContent && lastContent.getBoundingClientRect().bottom > tab.getBoundingClientRect().top + 1)
      issues.push({ kind: 'hidden-by-tabbar', detail: label(lastContent) });
    window.scrollTo(0, 0);
  }

  // de-duplicate
  const seen = new Set();
  return issues.filter(x => { const k = x.kind + x.detail; if (seen.has(k)) return false; seen.add(k); return true; });
}

// Puts the dashboard into a busy, worst-case state: lights on, long titles playing.
async function stressFn(LONG) {
  const m = await import('/js/lib.mjs');
  for (let i = 0; i < 40 && !m.store.s.snapshot; i++) await new Promise(r => setTimeout(r, 250));
  m.disconnect(); await new Promise(r => setTimeout(r, 300));
  const s = m.store.s;
  const entities = { ...s.entities };
  for (const id of Object.keys(entities)) if (id.startsWith('light.')) entities[id] = { ...entities[id], state: 'on', attributes: { ...entities[id].attributes, brightness: 200, color_temp_kelvin: 2700, color_mode: 'color_temp' } };
  m.store.set({
    connected: true, entities, cinema: { ...s.cinema, mode: 'playing' }, kiosk: 'player',
    player: { status: 'playing', itemId: 'x', title: LONG, subtitle: 'S12 E104  ' + LONG, imageId: null, position: 3000, duration: 9000, at: Date.now(), tracks: { subtitles: [{ index: 2, name: 'English', isText: true }, { index: 3, name: 'Spanish (Latin American)', isText: true }, { index: 4, name: 'English (SDH)', isText: false }], audio: [{ index: 1, name: 'English 5.1' }, { index: 5, name: 'Japanese stereo' }], sub: 2, audioIndex: 1 } },
    timer: { endsAt: Date.now() + 45 * 60000 },
    stats: { ...s.stats, foreground: { title: LONG + ' — Brave', process: 'brave' } },
  });
}

const results = {};
const browser = await chromium.launch({ channel: 'msedge', headless: true });

async function check(name, vp, setup, theme = 'dark') {
  const ctx = await browser.newContext({ viewport: { width: vp[0], height: vp[1] }, deviceScaleFactor: 1, colorScheme: theme, hasTouch: vp[0] <= 1024 });
  await ctx.addInitScript(t => { try { localStorage.setItem('hf-theme', t); } catch {} }, theme);
  await ctx.route('**/api/pc/screen.jpg*', r => r.fulfill({ status: 200, contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080"><rect width="100%" height="100%" fill="#333"/></svg>' }));
  const p = await ctx.newPage();
  const errors = [];
  p.on('pageerror', e => errors.push(e.message));
  p.on('console', m => { if (m.type() === 'error' && !/favicon|net::ERR|Failed to load resource/.test(m.text())) errors.push(m.text()); });
  try {
    await setup(p);
    await p.waitForTimeout(700);
    const issues = await p.evaluate(audit);
    for (const e of errors) issues.push({ kind: 'js-error', detail: e.slice(0, 160) });
    const key = `${name} @ ${vp[2]} ${vp[0]}x${vp[1]}${theme === 'light' ? ' light' : ''}`;
    results[key] = issues;
    if (SHOTS) await p.screenshot({ path: new URL(`${name}-${vp[2]}${theme === 'light' ? '-light' : ''}.png`, OUT).pathname.slice(1), fullPage: true });
  } catch (e) {
    results[`${name} @ ${vp[2]}`] = [{ kind: 'setup-failed', detail: e.message.split('\n')[0] }];
  }
  await ctx.close();
}

const go = (path, wait) => async p => { await p.goto(BASE + path); if (wait) await p.waitForSelector(wait, { timeout: 20000 }); };
const stressed = (path, wait) => async p => { await go(path, wait)(p); await p.evaluate(stressFn, LONG); };

const STATES = [
  ['home', go('/', '.plan .room')],
  ['home-busy', stressed('/', '.plan .room')],
  ['room-sheet', async p => { await stressed('/', '.plan .room')(p); await p.locator('.room').first().click(); await p.waitForSelector('.sheet .light-row'); }],
  ['tracks-sheet', async p => { await stressed('/', '.plan .room')(p); await p.getByRole('button', { name: 'Subtitles and audio' }).first().click(); await p.waitForSelector('.track-list'); }],
  ['timer-sheet', async p => { await stressed('/', '.plan .room')(p); await p.locator('.chip', { hasText: 'Off in' }).first().click(); await p.waitForSelector('.sheet'); }],
  ['sleep-confirm', async p => { await go('/', '.plan .room')(p); await p.getByRole('button', { name: 'Sleep', exact: true }).click(); await p.waitForSelector('.sheet'); }],
  ['remote', stressed('/remote', '.mirror')],
  ['library', go('/library', '.shelf .tile')],
  ['library-movies', async p => { await go('/library', '.lib-tabs')(p); await p.getByRole('button', { name: 'Movies' }).click(); await p.waitForSelector('.grid-posters .tile'); }],
  ['library-search', async p => { await go('/library', '.lib-tabs')(p); await p.fill('.search input', 'the'); await p.waitForTimeout(1500); }],
  ['item-sheet', async p => { await go('/library', '.shelf .tile')(p); await p.locator('.shelf.posters .tile').last().click(); await p.waitForSelector('.detail-body h2'); await p.waitForTimeout(1500); }],
  ['lights', stressed('/lights', '.plan .room')],
  ['lights-setup', go('/lights?setup=1', '#uc')],
  ['settings', go('/settings', '.settings .panel')],
  ...(LAN ? [['login', async p => { await p.goto(LAN + '/'); await p.waitForSelector('.login form'); }]] : []),
];

for (const vp of VIEWPORTS) for (const [name, setup] of STATES) await check(name, vp, setup);
// TV-only pages
for (const vp of [[1920, 1080, 'tv'], [1280, 720, 'tv-720']]) await check('ambient', vp, stressed('/ambient', '.amb-time'));
// Light theme, colour review
for (const vp of [[390, 844, 'phone'], [1440, 900, 'desktop']]) for (const [name, setup] of STATES.filter(s => ['home-busy', 'settings', 'item-sheet', 'login'].includes(s[0]))) await check(name, vp, setup, 'light');

await browser.close();
fs.writeFileSync(new URL('results.json', OUT), JSON.stringify(results, null, 2));
let total = 0;
for (const [k, list] of Object.entries(results)) { if (!list.length) continue; total += list.length; console.log(`\n# ${k}`); for (const i of list.slice(0, 12)) console.log(`  ${i.kind}: ${i.detail}${i.px ? '  (' + i.px + ')' : ''}`); if (list.length > 12) console.log(`  … ${list.length - 12} more`); }
console.log(`\n${Object.keys(results).length} checks, ${total} issues`);
