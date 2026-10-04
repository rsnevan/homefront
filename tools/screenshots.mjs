// README screenshots. Runs against a local homefront (loopback = signed in) using installed Edge.
// Light states and the HTPC screen are staged in the page so no real bulbs or private screens are involved.
import { chromium } from 'playwright-core';
import fs from 'fs';

const BASE = process.env.HF_URL || 'http://localhost:8090';
const OUT = new URL('../docs/screenshots/', import.meta.url);
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ channel: 'msedge', headless: true });

async function api(path) { const r = await fetch(BASE + path); return r.json(); }
const movies = (await api('/api/jf/browse?type=Movie&limit=40')).items.filter(i => i.hasBackdrop);
const shows = (await api('/api/jf/browse?type=Series&limit=40')).items.filter(i => i.hasBackdrop || i.hasPrimary);
const feature = movies.find(m => /Help/i.test(m.name)) || movies[0];
const backdrop = Buffer.from(await (await fetch(`${BASE}/api/jf/img/${feature.id}/Backdrop?w=1280`)).arrayBuffer());

async function page(viewport, dpr = 2) {
  const ctx = await browser.newContext({ viewport, deviceScaleFactor: dpr, colorScheme: 'dark' });
  await ctx.addInitScript(() => { try { localStorage.setItem('hf-theme', 'dark'); } catch {} });
  // The "HTPC screen" shows a movie still instead of a real desktop.
  await ctx.route('**/api/pc/screen.jpg*', r => r.fulfill({ body: backdrop, contentType: 'image/jpeg' }));
  const p = await ctx.newPage();
  return { ctx, p };
}

const stage = (feature, runtime) => async () => {
  const m = await import('/js/lib.mjs');
  for (let i = 0; i < 40 && !m.store.s.snapshot; i++) await new Promise(r => setTimeout(r, 250));
  m.disconnect();
  await new Promise(r => setTimeout(r, 300));
  const s = m.store.s;
  const L = (on, bri, k) => ({ state: on ? 'on' : 'off', attributes: { brightness: Math.round(bri * 2.55), color_temp_kelvin: k, color_mode: 'color_temp', supported_color_modes: ['color_temp', 'hs'], min_color_temp_kelvin: 2000, max_color_temp_kelvin: 6500 } });
  const looks = {
    'light.living_room_bulb_1': L(true, 22, 2300), 'light.living_room_bulb_2': L(true, 22, 2300), 'light.living_room_bulb_3': L(true, 22, 2300), 'light.living_room_bulb_4': L(false, 0, 2700),
    'light.kitchen_bulb': L(true, 85, 3800), 'light.study_bulb_1': L(true, 60, 4200), 'light.study_bulb_2': L(false, 0, 2700),
    'light.bedroom_bulb_1': L(false, 0, 2700), 'light.bedroom_bulb_2': L(false, 0, 2700),
  };
  const entities = { ...s.entities };
  for (const [id, v] of Object.entries(looks)) if (entities[id]) entities[id] = { ...entities[id], ...v, attributes: { ...entities[id].attributes, ...v.attributes } };
  m.store.set({
    connected: true, entities,
    weather: s.weather ? { ...s.weather, place: 'Weather' } : s.weather,
    cinema: { ...s.cinema, mode: 'playing' },
    kiosk: 'player',
    player: { status: 'playing', itemId: feature.id, title: feature.name, subtitle: String(feature.year || ''), imageId: feature.id, position: runtime * 0.38, duration: runtime, at: Date.now() },
    stats: { cpu: 18, memUsed: 5.1, memTotal: 7.8, uptime: 86400, disks: [{ name: 'C:', free: 140, total: 238 }, { name: 'D:', free: 61, total: 476 }], foreground: { title: `${feature.name} — homefront player`, process: 'brave' } },
  });
};

async function shot(p, name, opts = {}) {
  await p.waitForTimeout(opts.wait ?? 1800);
  await p.screenshot({ path: new URL(name, OUT).pathname.slice(1), fullPage: !!opts.full, animations: 'disabled', ...(name.endsWith('.jpg') ? { quality: 86 } : {}) });
  console.log('saved', name);
}

const runtime = feature.runtime || 7200;
const S = stage(feature, runtime);

// Desktop home
{
  const { ctx, p } = await page({ width: 1440, height: 1000 });
  await p.goto(BASE + '/');
  await p.waitForSelector('.plan .room');
  await p.evaluate(`(${S.toString().replace(/^async \(\) => /, 'async (feature, runtime) => ')})(${JSON.stringify(feature)}, ${runtime})`);
  await shot(p, 'home.jpg', { wait: 3500 });
  await shot(p, 'home-full.jpg', { full: true, wait: 600 });
  // room sheet
  await p.locator('.room').first().click();
  await shot(p, 'room.jpg', { wait: 900 });
  await ctx.close();
}

// Library + detail sheet
{
  const { ctx, p } = await page({ width: 1440, height: 1000 });
  await p.goto(BASE + '/library');
  await p.waitForSelector('.lib-hero, .shelf .tile', { timeout: 20000 });
  await shot(p, 'library.jpg', { wait: 2500 });
  const show = shows.find(s => /Abbott|Severance/.test(s.name)) || shows[0];
  await p.evaluate(async id => {
    const tile = [...document.querySelectorAll('.tile')].find(t => t.textContent.includes(id));
    tile?.click();
  }, show.name);
  await p.waitForSelector('.ep', { timeout: 20000 }).catch(() => {});
  await shot(p, 'details.jpg', { wait: 1500 });
  await ctx.close();
}

// Remote
{
  const { ctx, p } = await page({ width: 1440, height: 1000 });
  await p.goto(BASE + '/remote');
  await p.waitForSelector('.mirror');
  await p.evaluate(`(${S.toString().replace(/^async \(\) => /, 'async (feature, runtime) => ')})(${JSON.stringify(feature)}, ${runtime})`);
  await shot(p, 'remote.jpg', { wait: 2500 });
  await ctx.close();
}

// Phone
{
  const { ctx, p } = await page({ width: 390, height: 844 }, 3);
  await p.goto(BASE + '/');
  await p.waitForSelector('.plan .room');
  await p.evaluate(`(${S.toString().replace(/^async \(\) => /, 'async (feature, runtime) => ')})(${JSON.stringify(feature)}, ${runtime})`);
  await shot(p, 'phone-home.jpg', { wait: 3500 });
  await p.goto(BASE + '/remote');
  await p.waitForSelector('.mirror');
  await shot(p, 'phone-remote.jpg', { wait: 2500 });
  await ctx.close();
}

// TV ambient
{
  const { ctx, p } = await page({ width: 1920, height: 1080 }, 1);
  await p.goto(BASE + '/ambient');
  await p.waitForSelector('.amb-time');
  await p.evaluate(`(${S.toString().replace(/^async \(\) => /, 'async (feature, runtime) => ')})(${JSON.stringify(feature)}, ${runtime})`);
  await shot(p, 'ambient.png', { wait: 2500 });
  await ctx.close();
}

await browser.close();
