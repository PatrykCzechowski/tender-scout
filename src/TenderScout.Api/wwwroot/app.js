// The web page: three views (Pulpit, Skrzynka, Asystent) switched by the address after '#'. The assistant sends one
// question at a time to POST /ask; the start page and the inbox read GET /notices.
// Text from the API is always inserted as text (textContent), never as HTML. Nothing is stored in the browser.
'use strict';

const TIMEOUT_MS = 120000;
const MAX_QUESTION = 1000;
const NUMBER_RULE = /^[0-9]{1,6}-[0-9]{4}$/;
const EXAMPLES = [
  'Jaka jest wysokość wadium?',
  'Do kiedy trzeba złożyć ofertę?',
  'Jakie są kryteria oceny ofert i ich wagi?',
  'Jakie są warunki udziału w postępowaniu?'
];
const TEXT = {
  questionField: 'Wpisz pytanie. Może mieć najwyżej 1000 znaków.',
  numberField: 'Numer ogłoszenia ma postać 123456-2026.',
  refusedTitle: 'W dokumentach nie ma odpowiedzi',
  refused: 'Nie znalazłem fragmentu SWZ, który odpowiada na to pytanie, więc nie zgaduję. Spróbuj zapytać inaczej.',
  refusedWithNumber: 'Nie znalazłem fragmentu SWZ, który odpowiada na to pytanie, więc nie zgaduję. Spróbuj zapytać inaczej albo bez numeru ogłoszenia.',
  invalid: ['Nie udało się wysłać pytania', 'Sprawdź pytanie i numer ogłoszenia.'],
  unavailable: ['Model jest teraz niedostępny', 'Spróbuj ponownie za chwilę.'],
  timeout: ['Odpowiedź nie przyszła w ciągu 2 minut', 'Przerwałem czekanie. Spróbuj ponownie za chwilę.'],
  offline: ['Brak połączenia z API', 'Sprawdź, czy aplikacja działa, i spróbuj ponownie.'],
  failed: ['Coś poszło nie tak', 'Serwer zwrócił błąd. Spróbuj ponownie za chwilę.']
};

// The design's icons: 16-unit paths, drawn with the stroke settings of the design.
const ICONS = {
  spark: '<path d="M8 1.8l1.4 3.9 3.9 1.4-3.9 1.4L8 12.4 6.6 8.5 2.7 7.1l3.9-1.4L8 1.8z"/><path d="M12.8 11.2v2.6M11.5 12.5h2.6"/>',
  doc: '<path d="M4 1.8h5L12.5 5.3v7.9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V2.8a1 1 0 0 1 1-1z"/><path d="M9 1.8v3.5h3.5M5.5 8.7h5M5.5 11.2h3"/>',
  plug: '<path d="M6 2v3M10 2v3M4.5 5h7v2.5a3.5 3.5 0 0 1-7 0V5zM8 11v3"/>',
  check: '<path d="M3 8.5l3.2 3.2L13 4.8"/>',
  alert: '<path d="M8 2.2l6.2 11H1.8L8 2.2z"/><path d="M8 6.6v3M8 11.7v.01"/>',
  chev: '<path d="M4 6l4 4 4-4"/>',
  open: '<path d="M6 3.5h6.5V10M12.5 3.5L4 12"/>',
  arrow: '<path d="M10 3L5 8l5 5"/>'
};

const $ = id => document.getElementById(id);
const form = $('form'), question = $('question'), number = $('number'), send = $('send'), log = $('log');
let pending = null; // { controller, timedOut } of the request in flight
let sourceCount = 0;

function icon(name, size) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  for (const [k, v] of Object.entries({ class: 'ic', width: size, height: size, viewBox: '0 0 16 16', fill: 'none', stroke: 'currentColor',
    'stroke-width': '1.6', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(k, v);
  svg.innerHTML = ICONS[name]; // constant markup from this file, never API text
  return svg;
}

// Builds an element; children are nodes or strings, and strings always become text nodes.
function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) node.setAttribute(k, v);
  node.append(...children.filter(c => c !== null && c !== undefined));
  return node;
}

function showExamples() {
  $('examples').replaceChildren(...EXAMPLES.map(q => {
    const card = el('button', { type: 'button', class: 'card sug' },
      el('span', { class: 'card-h' }, icon('spark', 15), 'Zapytaj asystenta'), el('b', null, q));
    card.addEventListener('click', () => askExample(q));
    return card;
  }));
}

function setFieldMessage(input, text) {
  const msg = $(input.id + 'Msg');
  msg.textContent = text || '';
  msg.hidden = !text;
  if (text) input.setAttribute('aria-invalid', 'true'); else input.removeAttribute('aria-invalid');
}

// Shows the messages and moves focus: to the question field when both have one, else to the field that has one.
function markFields(questionBad, numberBad) {
  setFieldMessage(question, questionBad ? TEXT.questionField : '');
  setFieldMessage(number, numberBad ? TEXT.numberField : '');
  if (questionBad) question.focus(); else if (numberBad) number.focus();
}

function readForm() {
  const q = question.value.trim();
  const n = number.value.trim();
  const questionBad = q.length === 0 || q.length > MAX_QUESTION;
  const numberBad = n !== '' && !NUMBER_RULE.test(n);
  return { q, n: n === '' ? null : n, questionBad, numberBad };
}

function addEntry(q, n) {
  $('empty').hidden = true;
  if ($('title').textContent === 'Nowy czat') $('title').textContent = q.length > 60 ? q.slice(0, 59).trim() + '…' : q;
  const pill = n ? el('span', { class: 'ctx-pill' }, icon('doc', 12), el('span', null, 'Ogłoszenie ' + n)) : null;
  log.append(el('div', { class: 'msg user' }, el('div', { class: 'bubble' }, q, pill)));
  const status = el('span', { class: 'st' }, el('i', { class: 'pulse' }), 'szukam w dokumentach… ', el('span', { class: 'secs', 'aria-hidden': 'true' }, '0 s'));
  const chip = el('div', { class: 'toolchip', 'data-rec': 'ai' }, icon('plug', 14), el('b', null, 'POST /ask'), el('span', null, 'API tender-scout'), status);
  const body = el('div', { class: 'ai-body', 'aria-busy': 'true' }, chip);
  log.append(el('div', { class: 'msg ai' }, el('span', { class: 'ai-av' }, icon('spark', 16)), body));
  log.scrollTop = log.scrollHeight;
  return { body, status };
}

function finishEntry(entry, ok, ...content) {
  entry.status.className = ok ? 'st ok' : 'st';
  entry.status.replaceChildren(...(ok ? [icon('check', 12), 'gotowe'] : ['nie udało się']));
  entry.body.setAttribute('aria-busy', 'false');
  entry.body.append(...content);
  log.scrollTop = log.scrollHeight;
}

function alertBox(kind, [title, detail]) {
  return el('div', { class: kind }, icon('alert', 16), el('div', null, title, el('small', null, detail)));
}

function sourceRow(c, i) {
  const id = 'src-' + (++sourceCount);
  const shown = v => (v === null || v === undefined || v === '' ? '—' : String(v));
  const quote = el('div', { class: 'quote', id, hidden: '' },
    el('small', null, 'Dokument ' + shown(c.docId) + ', strona ' + shown(c.page)),
    el('blockquote', { lang: 'pl' }, shown(c.passage)));
  const button = el('button', { type: 'button', class: 'cite', 'aria-expanded': 'false', 'aria-controls': id },
    el('span', { class: 'sr' }, 'Fragment źródła ' + (i + 1) + ', '), 's. ' + shown(c.page), icon('chev', 12));
  button.addEventListener('click', () => {
    const open = button.getAttribute('aria-expanded') !== 'true';
    button.setAttribute('aria-expanded', String(open));
    quote.hidden = !open;
  });
  return el('div', { class: 'term' },
    el('div', { class: 'term-k' }, 'Źródło ' + (i + 1)),
    el('div', { class: 'term-v' }, shown(c.docId) + ' · ogłoszenie ' + shown(c.publicationNumber)),
    button, quote);
}

function answerContent(reply, n) {
  if (reply.refused) {
    return [alertBox('alert soft', [TEXT.refusedTitle, n ? TEXT.refusedWithNumber : TEXT.refused])];
  }
  const content = [el('p', { class: 'ai-text' }, reply.answer)];
  const citations = reply.citations.filter(c => c && typeof c === 'object');
  if (citations.length > 0) {
    content.push(el('div', { class: 'rich' }, el('h3', null, icon('doc', 14), 'Źródła'),
      el('div', { class: 'terms' }, ...citations.map(sourceRow))));
  }
  return content;
}

const isReply = r => r !== null && typeof r === 'object' && typeof r.refused === 'boolean'
  && typeof r.answer === 'string' && Array.isArray(r.citations);

// A body that is not JSON gives null. A read that stops (the timer aborted it or the connection dropped) throws, so
// ask() reports it as a timeout or a lost connection, not as a wrong reply.
async function readJson(response) {
  const body = await response.text();
  try { return JSON.parse(body); } catch { return null; }
}

// Turns the HTTP response into what the log shows; marks the fields a 400 names.
async function outcome(response, n) {
  if (response.status === 200) {
    const reply = await readJson(response);
    return isReply(reply) ? { ok: true, content: answerContent(reply, n) } : { message: TEXT.failed };
  }
  if (response.status === 400) {
    const errors = (await readJson(response))?.errors ?? {};
    markFields('question' in errors, 'publicationNumber' in errors);
    return { message: TEXT.invalid };
  }
  return { message: response.status === 503 ? TEXT.unavailable : TEXT.failed };
}

async function ask(q, n) {
  const entry = addEntry(q, n);
  question.value = '';
  const started = Date.now();
  const tick = setInterval(() => {
    entry.status.querySelector('.secs').textContent = Math.round((Date.now() - started) / 1000) + ' s';
  }, 1000);
  const request = { controller: new AbortController(), timedOut: false };
  const timer = setTimeout(() => { request.timedOut = true; request.controller.abort(); }, TIMEOUT_MS);
  pending = request;
  send.disabled = true;
  let result;
  try {
    const response = await fetch('ask', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ question: q, publicationNumber: n }),
      signal: request.controller.signal
    });
    result = await outcome(response, n);
  } catch {
    result = { message: request.timedOut ? TEXT.timeout : TEXT.offline };
  } finally {
    clearInterval(tick);
    clearTimeout(timer);
  }
  if (pending !== request) return; // abandoned by "Nowy czat"
  pending = null;
  send.disabled = false;
  // Text typed during the wait stays; a failed question comes back only into an empty field.
  if (result.ok) {
    finishEntry(entry, true, ...result.content);
  } else {
    finishEntry(entry, false, alertBox('alert', result.message));
    if (question.value.trim() === '') question.value = q;
  }
  if (currentView() === 'asystent' && !question.hasAttribute('aria-invalid') && !number.hasAttribute('aria-invalid')) question.focus();
  updateSend();
}

function newChat() {
  if (pending) { const request = pending; pending = null; request.controller.abort(); }
  log.replaceChildren($('empty'));
  $('empty').hidden = false;
  $('title').textContent = 'Nowy czat';
  question.value = '';
  number.value = '';
  markFields(false, false);
  send.disabled = false;
  updateSend();
  question.focus();
}

function updateSend() {
  send.classList.toggle('ready', question.value.trim() !== '' && !pending);
}

form.addEventListener('submit', event => {
  event.preventDefault();
  if (pending) return;
  const { q, n, questionBad, numberBad } = readForm();
  if (questionBad || numberBad) { markFields(questionBad, numberBad); return; }
  markFields(false, false);
  ask(q, n);
});
question.addEventListener('input', () => { setFieldMessage(question, ''); updateSend(); });
number.addEventListener('input', () => setFieldMessage(number, ''));
$('newChat').addEventListener('click', newChat);
$('collapse').addEventListener('click', () => {
  const app = $('app'), button = $('collapse');
  const collapsed = app.dataset.collapsed !== 'true';
  app.dataset.collapsed = String(collapsed);
  button.setAttribute('aria-expanded', String(!collapsed));
  button.setAttribute('aria-label', collapsed ? 'Rozwiń menu' : 'Zwiń menu');
});


// ---------- Views: the part after '#' says which one is shown, so reload and Back keep it ----------

const VIEWS = { pulpit: 'dash', skrzynka: 'inbox', asystent: 'chat' };
const NOTICES_TIMEOUT_MS = 30000;
const LIST_LIMIT = 200;
const SEARCH_DELAY_MS = 300;
const WEEKS = 5;
const NOTICE_TEXT = {
  loading: 'Wczytuję ogłoszenia…',
  failed: 'Nie udało się pobrać ogłoszeń',
  noOpen: 'Nie ma teraz otwartych przetargów w bazie',
  noMatch: 'Nic tu nie pasuje',
  noneChosen: 'Wybierz przetarg z listy, żeby zobaczyć szczegóły.'
};
const FILTERS = [
  { id: 'all', label: 'Wszystkie', params: () => ({}) },
  { id: 'docs', label: 'Z SWZ', params: () => ({ hasDocuments: 'true' }) },
  { id: 'soon', label: 'Do 7 dni', params: () => ({ deadlineTo: addDays(today(), 7) }) },
  { id: '72', label: 'Usługi IT', params: () => ({ cpvPrefix: '72' }) },
  { id: '48', label: 'Oprogramowanie', params: () => ({ cpvPrefix: '48' }) }
];
const WEEKDAYS = ['niedziela', 'poniedziałek', 'wtorek', 'środa', 'czwartek', 'piątek', 'sobota'];

const dash = { loadedOn: null, day: null, dayRequest: 0 };
const inbox = { loadedOn: null, filter: 'all', query: '', selected: null, pinned: false, request: 0, timer: 0 };

const currentView = () => (location.hash.slice(1) in VIEWS ? location.hash.slice(1) : 'pulpit');

// Shows the view at once, so focus can move into it; the hashchange that follows finds it already shown.
function go(view) {
  if (location.hash !== '#' + view) location.hash = view;
  showView();
}

// A view reads "today" when it loads; it loads again when the browser's date has changed since then.
function showView() {
  const view = currentView();
  for (const name of Object.keys(VIEWS)) $('view-' + name).hidden = name !== view;
  $('app').dataset.page = VIEWS[view];
  for (const link of document.querySelectorAll('.nav-item')) {
    if (link.dataset.view === view) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  }
  if (view === 'pulpit' && dash.loadedOn !== today()) loadDashboard();
  if (view === 'skrzynka' && inbox.loadedOn !== today()) loadInbox();
}

// ---------- Dates, as yyyy-MM-dd in the browser's own time zone ----------

const pad = v => String(v).padStart(2, '0');
const isoDate = d => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
const parts = iso => iso.split('-').map(Number);
const today = () => isoDate(new Date());
const addDays = (iso, days) => { const [y, m, d] = parts(iso); return isoDate(new Date(y, m - 1, d + days)); };
const daysBetween = (from, to) => Math.round((Date.UTC(...dayParts(to)) - Date.UTC(...dayParts(from))) / 86400000);
const dayParts = iso => { const [y, m, d] = parts(iso); return [y, m - 1, d]; };
const weekday = iso => new Date(...dayParts(iso)).getDay();
const formatDate = iso => (iso ? iso.split('-').reverse().join('.') : '—');
const shortDate = iso => (iso ? formatDate(iso).slice(0, 5) : '—');
const dueText = days => (days === 0 ? 'dziś' : days === 1 ? 'jutro' : 'za ' + days + ' dni');
const shown = v => (v === null || v === undefined || v === '' ? '—' : String(v));

function pl(n, one, few, many) {
  if (n === 1) return one;
  const n10 = n % 10, n100 = n % 100;
  return n10 >= 2 && n10 <= 4 && !(n100 >= 12 && n100 <= 14) ? few : many;
}

// ---------- GET /notices ----------

// Open notices only: every request starts at today's deadline unless it asks for a later day.
async function fetchNotices(params) {
  const query = new URLSearchParams({ deadlineFrom: today(), ...params });
  const response = await fetch('notices?' + query, { headers: { Accept: 'application/json' }, signal: AbortSignal.timeout(NOTICES_TIMEOUT_MS) });
  const reply = response.status === 200 ? await readJson(response) : null;
  if (!reply || typeof reply.total !== 'number' || !Array.isArray(reply.notices)) throw new Error('unusable reply');
  return reply;
}

// replaceChildren with the children that are present: null stands for "nothing here".
const fill = (node, ...children) => node.replaceChildren(...children.filter(c => c !== null && c !== undefined));

function stateBox(title, detail, button) {
  return el('div', { class: 'empty-list' }, el('b', null, title), detail || null, button || null);
}

function failure(retry) {
  const button = el('button', { type: 'button', class: 'btn ghost retry' }, 'Spróbuj ponownie');
  button.addEventListener('click', retry);
  return stateBox(NOTICE_TEXT.failed, null, button);
}

function svgEl(tag, attrs) {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  return node;
}

// The design's ring: full when the tender's SWZ is stored ("SWZ"), empty when not ("—").
function ring(hasDocuments, size) {
  const svg = svgEl('svg', { viewBox: '0 0 40 40', 'aria-hidden': 'true' });
  svg.append(svgEl('circle', { class: 'ring-track', cx: '20', cy: '20', r: '16' }));
  if (hasDocuments) svg.append(svgEl('circle', { class: 'ring-bar', cx: '20', cy: '20', r: '16', 'stroke-dasharray': '100.5 100.5', transform: 'rotate(-90 20 20)' }));
  return el('span', { class: 'ring', 'data-rec': hasDocuments ? 'go' : 'grey', style: '--sz:' + size + 'px', role: 'img', 'aria-label': hasDocuments ? 'SWZ w bazie' : 'Brak SWZ w bazie' },
    svg, el('span', { class: 'ring-n txt', 'aria-hidden': 'true' }, hasDocuments ? 'SWZ' : '—'));
}

function docChip(hasDocuments) {
  return hasDocuments
    ? el('span', { class: 'pc', 'data-rec': 'go' }, icon('doc', 12), 'Można pytać o SWZ')
    : el('span', { class: 'pc', 'data-rec': 'check' }, 'Brak SWZ');
}

// ---------- Start page ----------

function showDashExamples() {
  $('dashExamples').replaceChildren(...EXAMPLES.slice(0, 3).map(q => {
    const card = el('button', { type: 'button', class: 'card sug s4' },
      el('span', { class: 'card-h' }, icon('spark', 15), 'Zapytaj o SWZ'), el('b', null, q));
    card.addEventListener('click', () => askExample(q));
    return card;
  }));
}

function loadDashboard() {
  dash.loadedOn = today();
  loadCounts();
  loadCalendar();
}

async function loadCounts() {
  const box = $('counts');
  box.replaceChildren(stateBox(NOTICE_TEXT.loading));
  try {
    const [open, soon, docs] = await Promise.all([
      fetchNotices({ limit: '1' }),
      fetchNotices({ deadlineTo: addDays(today(), 7), limit: '1' }),
      fetchNotices({ hasDocuments: 'true', limit: '1' })
    ]);
    box.replaceChildren(el('div', { class: 'rows' },
      countRow('Otwarte przetargi', open.total, open.total, 'all', 'Ogłoszenia IT z TED z terminem ofert dziś lub później'),
      countRow('Z terminem w ciągu 7 dni', soon.total, open.total, 'soon'),
      countRow('Z dokumentami SWZ w bazie', docs.total, open.total, 'docs', 'Tylko o nie asystent odpowie z dokumentów')));
  } catch {
    box.replaceChildren(failure(loadCounts));
  }
}

function countRow(label, value, all, filter, note) {
  const share = all ? (value / all) * 100 : 0;
  const row = el('button', { type: 'button', class: 'brow linkrow', 'data-rec': 'go' },
    el('span', { class: 'brow-h' }, el('span', null, label), el('span', { class: 'v' }, String(value))),
    el('span', { class: 'track', 'aria-hidden': 'true' }, el('i', { style: 'width:' + share.toFixed(1) + '%' })),
    note ? el('small', null, note) : null);
  row.addEventListener('click', () => openInbox({ filter }));
  return row;
}

async function loadCalendar() {
  const box = $('calendar');
  box.replaceChildren(stateBox(NOTICE_TEXT.loading));
  const t = today(), start = addDays(t, -((weekday(t) + 6) % 7));
  try {
    const reply = await fetchNotices({ deadlineTo: addDays(start, WEEKS * 7 - 1), limit: String(LIST_LIMIT) });
    renderCalendar(box, reply, t, start);
  } catch {
    box.replaceChildren(failure(loadCalendar));
  }
}

// The reply is ordered by deadline, so when it is cut, counts are exact up to its last day only.
function renderCalendar(box, reply, t, start) {
  const byDay = new Map();
  for (const n of reply.notices) byDay.set(n.submissionDeadline, [...(byDay.get(n.submissionDeadline) || []), n]);
  const cut = reply.total > reply.notices.length;
  const lastDay = cut ? reply.notices[reply.notices.length - 1].submissionDeadline : null;
  const weekEnd = addDays(t, 7);
  const soon = reply.notices.filter(n => n.submissionDeadline <= weekEnd).length;
  const soonText = (cut && lastDay <= weekEnd ? 'co najmniej ' : '') + soon + ' ' + pl(soon, 'oferta', 'oferty', 'ofert');

  const days = [...byDay.keys()].sort();
  if (!(dash.day >= t && dash.day <= addDays(start, WEEKS * 7 - 1))) dash.day = days[0] || null;
  const cells = [];
  for (let i = 0; i < WEEKS * 7; i++) cells.push(calendarCell(addDays(start, i), t, byDay, lastDay));

  fill(box,
    el('div', { class: 'ratio-big' }, el('b', null, soonText), el('span', null, 'do złożenia w ciągu 7 dni')),
    el('div', { class: 'cal', role: 'group', 'aria-label': 'Kalendarz terminów ofert' },
      ...['Pn', 'Wt', 'Śr', 'Cz', 'Pt', 'So', 'Nd'].map(d => el('div', { class: 'cal-dow', 'aria-hidden': 'true' }, d)), ...cells),
    el('div', { class: 'keys' },
      el('span', null, el('i', { class: 'kd', style: 'background:var(--blue)' }), 'SWZ w bazie'),
      el('span', null, el('i', { class: 'kd', style: 'background:var(--can-no)' }), 'Bez SWZ'),
      el('span', null, el('i', { class: 'kd soon' }), 'Do 7 dni')),
    cut ? el('p', { class: 'fine' }, 'Pokazano ' + reply.notices.length + ' z ' + reply.total + ' terminów w tych tygodniach. Dni po ' + formatDate(lastDay) + ' nie zostały wczytane.') : null,
    el('div', { class: 'cal-day', id: 'calDay' }));
  if (dash.day) loadDay(dash.day); else $('calDay').append(el('p', { class: 'fine' }, 'Brak terminów ofert w tych tygodniach.'));
}

function calendarCell(day, t, byDay, lastDay) {
  const [, m, d] = parts(day);
  const label = el('span', { class: 'n' }, String(d));
  const month = d === 1 ? el('span', { class: 'm' }, ['sty', 'lut', 'mar', 'kwi', 'maj', 'cze', 'lip', 'sie', 'wrz', 'paź', 'lis', 'gru'][m - 1]) : null;
  if (day < t) return el('div', { class: 'cal-cell past', 'aria-hidden': 'true' }, label, month);

  const list = byDay.get(day) || [];
  const unloaded = lastDay !== null && day > lastDay;
  const atLeast = lastDay !== null && day === lastDay;
  const count = (atLeast ? 'co najmniej ' : '') + list.length + ' ' + pl(list.length, 'oferta', 'oferty', 'ofert');
  const dots = unloaded
    ? [el('span', { class: 'cal-more' }, '?')]
    : list.length > 3 || atLeast
      ? [el('span', { class: 'cal-more' }, (atLeast ? '≥' : '') + list.length)]
      : list.map(n => el('i', { class: n.hasDocuments ? 'can' : '' }));
  const days = daysBetween(t, day);
  const cell = el('button', {
    type: 'button',
    class: 'cal-cell' + (day === t ? ' today' : '') + (days <= 7 ? ' soon' : '') + (unloaded ? ' unloaded' : ''),
    'aria-pressed': String(day === dash.day),
    'aria-label': d + '.' + pad(m) + ': ' + (unloaded ? 'nie wczytano' : list.length ? count : 'brak ofert'),
    'data-day': day
  }, label, month, el('span', { class: 'cal-dots' }, ...dots));
  cell.addEventListener('click', () => {
    dash.day = day;
    for (const c of document.querySelectorAll('.cal-cell[data-day]')) c.setAttribute('aria-pressed', String(c.dataset.day === day));
    loadDay(day);
  });
  return cell;
}

// The list of one day comes from its own request, so it is complete up to the limit.
async function loadDay(day) {
  const box = $('calDay'), request = ++dash.dayRequest;
  const title = el('h3', null, WEEKDAYS[weekday(day)] + ' ' + shortDate(day) + ' · ' + dueText(daysBetween(today(), day)));
  box.replaceChildren(title, stateBox(NOTICE_TEXT.loading));
  try {
    const reply = await fetchNotices({ deadlineFrom: day, deadlineTo: day, limit: String(LIST_LIMIT) });
    if (request !== dash.dayRequest) return;
    fill(box, title,
      reply.notices.length ? tenderList(reply.notices) : el('p', { class: 'fine' }, 'Brak terminów tego dnia.'),
      reply.total > reply.notices.length ? el('p', { class: 'fine' }, 'Pokazano ' + reply.notices.length + ' z ' + reply.total + '.') : null);
  } catch {
    if (request === dash.dayRequest) box.replaceChildren(title, failure(() => loadDay(day)));
  }
}

function tenderList(notices) {
  return el('ul', { class: 'tq' }, ...notices.map(n => {
    const open = el('button', { type: 'button', class: 'open', 'aria-label': 'Otwórz w skrzynce: ' + shown(n.title) }, icon('open', 14));
    open.addEventListener('click', () => openInbox({ notice: n }));
    return el('li', null,
      el('span', { class: 'dot-s', 'data-rec': n.hasDocuments ? 'go' : 'grey', 'aria-hidden': 'true' }),
      el('div', null, el('div', { class: 'tq-t' }, shown(n.title)), el('div', { class: 'tq-s' }, shown(n.buyerName))),
      docChip(n.hasDocuments), open);
  }));
}

// ---------- Inbox ----------

// From a count or the calendar: the filter, an empty search and, from the calendar, the chosen notice.
function openInbox({ filter = 'all', notice = null }) {
  inbox.filter = filter;
  inbox.query = '';
  $('search').value = '';
  inbox.selected = notice;
  inbox.pinned = notice !== null;
  renderFilters();
  renderDetail();
  if (notice) showDetailOnNarrowScreen();
  inbox.loadedOn = null;
  go('skrzynka');
}

function renderFilters() {
  $('filters').replaceChildren(...FILTERS.map(f => {
    const button = el('button', { type: 'button', 'aria-pressed': String(f.id === inbox.filter) }, f.label);
    button.addEventListener('click', () => { inbox.filter = f.id; inbox.pinned = false; renderFilters(); loadInbox(); });
    return button;
  }));
}

function applySearch() {
  clearTimeout(inbox.timer);
  const query = $('search').value.trim();
  if (query === inbox.query) return;
  inbox.query = query;
  inbox.pinned = false;
  loadInbox();
}

// Latest wins: a reply to an older request never replaces the list.
async function loadInbox() {
  inbox.loadedOn = today();
  const request = ++inbox.request;
  $('inboxSummary').textContent = '';
  $('list').replaceChildren(stateBox(NOTICE_TEXT.loading));
  const params = { ...FILTERS.find(f => f.id === inbox.filter).params(), limit: String(LIST_LIMIT) };
  if (inbox.query) params.query = inbox.query;
  try {
    const reply = await fetchNotices(params);
    if (request === inbox.request) renderInbox(reply);
  } catch {
    if (request === inbox.request) $('list').replaceChildren(failure(loadInbox));
  }
}

function renderInbox(reply) {
  const chosen = inbox.selected && reply.notices.find(n => n.publicationNumber === inbox.selected.publicationNumber);
  if (chosen) inbox.selected = chosen; else if (!inbox.pinned) inbox.selected = null;
  renderDetail();

  const cut = reply.total > reply.notices.length;
  $('inboxSummary').textContent = 'Pasujące otwarte przetargi: ' + reply.total + '.' + (cut ? ' Pokazano pierwsze ' + reply.notices.length + '.' : '');
  if (reply.total === 0) {
    const clear = el('button', { type: 'button', class: 'btn ghost retry' }, 'Wyczyść wyszukiwanie i filtr');
    clear.addEventListener('click', () => openInbox({}));
    const nothing = !inbox.query && inbox.filter === 'all';
    $('list').replaceChildren(nothing ? stateBox(NOTICE_TEXT.noOpen) : stateBox(NOTICE_TEXT.noMatch, 'Zmień filtr albo wyczyść wyszukiwanie.', clear));
    return;
  }
  const t = today();
  $('list').replaceChildren(el('table', { class: 'tbl' },
    el('caption', { class: 'sr' }, 'Otwarte przetargi, najbliższy termin pierwszy'),
    el('thead', null, el('tr', null,
      el('th', { scope: 'col' }, 'Przetarg'), el('th', { scope: 'col', class: 'n col-d' }, 'Termin'),
      el('th', { scope: 'col', class: 'n col-v' }, 'CPV'), el('th', { scope: 'col', class: 'col-n' }, 'SWZ'))),
    el('tbody', null, ...reply.notices.map(n => noticeRow(n, t)))));
}

function noticeRow(n, t) {
  const days = daysBetween(t, n.submissionDeadline);
  const date = el('span', days <= 7 ? { class: 'bad' } : null, shortDate(n.submissionDeadline));
  const row = el('tr', { class: 'trow', tabindex: '0', 'data-number': n.publicationNumber, 'aria-current': String(inbox.selected?.publicationNumber === n.publicationNumber) },
    el('td', null, el('div', { class: 'tcell' }, ring(n.hasDocuments, 44),
      el('div', { class: 'tmain' },
        el('div', { class: 'row-title' }, shown(n.title)),
        el('div', { class: 'row-buyer' }, shown(n.buyerName)),
        el('div', { class: 'meta-m', 'aria-hidden': 'true' }, el('span', days <= 7 ? { class: 'bad' } : null, shortDate(n.submissionDeadline) + ' · ' + dueText(days)), docChip(n.hasDocuments))))),
    el('td', { class: 'n col-d' }, date, el('small', null, dueText(days))),
    el('td', { class: 'n col-v mono' }, shown(n.mainCpvCode)),
    el('td', { class: 'col-n', 'aria-hidden': 'true' }, docChip(n.hasDocuments)));
  row.addEventListener('click', () => select(n));
  row.addEventListener('keydown', event => {
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(n); }
  });
  return row;
}

function select(n) {
  inbox.selected = n;
  inbox.pinned = false;
  for (const row of document.querySelectorAll('.trow')) row.setAttribute('aria-current', String(row.dataset.number === n.publicationNumber));
  renderDetail();
  showDetailOnNarrowScreen();
}

function showDetailOnNarrowScreen() {
  if (!window.matchMedia('(max-width: 860px)').matches) return;
  $('app').dataset.mobile = 'detail';
  $('detailTitle')?.focus();
}

function fact(label, value, note, warn) {
  return el('div', { class: 'fact' }, el('dt', null, label), el('dd', null, value),
    note ? el('small', warn ? { class: 'warn' } : null, note) : null);
}

function renderDetail() {
  const n = inbox.selected, pane = $('detail');
  if (!n) {
    pane.replaceChildren(el('div', { class: 'd-body' }, stateBox(NOTICE_TEXT.noneChosen)));
    return;
  }
  const days = n.submissionDeadline ? daysBetween(today(), n.submissionDeadline) : null;
  const back = el('button', { type: 'button', class: 'back' }, icon('arrow', 16), 'Skrzynka');
  back.addEventListener('click', () => { $('app').dataset.mobile = 'list'; });
  // Only a web address becomes a link; the page never follows another scheme from the data.
  const link = /^https?:\/\//i.test(n.link || '')
    ? el('a', { class: 'open-offer', href: n.link, target: '_blank', rel: 'noopener noreferrer' }, 'Otwórz w TED', icon('open', 14), el('span', { class: 'sr' }, ' (nowa karta)'))
    : null;
  pane.replaceChildren(el('div', { class: 'd-body' },
    el('div', { class: 'd-top' }, back, el('span', { class: 'mono' }, n.publicationNumber), el('span', { 'aria-hidden': 'true' }, '·'),
      el('span', { class: 'mono' }, 'CPV ' + shown(n.mainCpvCode)), link),
    el('div', null, el('h2', { class: 'd-title', id: 'detailTitle', tabindex: '-1' }, shown(n.title)), el('p', { class: 'd-buyer' }, shown(n.buyerName))),
    el('dl', { class: 'facts' },
      fact('Termin ofert', formatDate(n.submissionDeadline), days === null ? null : dueText(days), days !== null && days <= 7),
      fact('Opublikowano', formatDate(n.publicationDate)),
      fact('Kody CPV', n.cpvCodes && n.cpvCodes.length ? n.cpvCodes.join(', ') : '—')),
    n.hasDocuments ? askBlock(n) : el('div', { class: 'alert soft' }, icon('alert', 16),
      el('div', null, 'Nie mamy SWZ tego przetargu',
        el('small', null, 'Asystent nie odpowie na pytania o jego dokumenty. Dokumenty trafiają do bazy przez import z manifestu.')))));
}

function askBlock(n) {
  const examples = EXAMPLES.map(q => {
    const chip = el('button', { type: 'button', class: 'sug-chip' }, q);
    chip.addEventListener('click', () => { setNumber(n.publicationNumber); askExample(q); });
    return chip;
  });
  const button = el('button', { type: 'button', class: 'btn' }, icon('spark', 15), 'Zapytaj o SWZ');
  button.addEventListener('click', () => { setNumber(n.publicationNumber); go('asystent'); question.focus(); });
  return el('div', { class: 'verdict', 'data-rec': 'go' }, ring(true, 56),
    el('div', null,
      el('div', { class: 'verdict-t' }, 'Zapytaj o dokumenty'),
      el('p', null, 'Asystent odpowie z fragmentów SWZ tego przetargu i poda dokument oraz stronę.'),
      el('div', { class: 'sugs' }, ...examples), button));
}

// ---------- Hand-off to the assistant ----------

function setNumber(publicationNumber) {
  number.value = publicationNumber;
  setFieldMessage(number, '');
}

// One rule for every example question: it replaces the question field and is sent if nothing is pending.
function askExample(q) {
  question.value = q;
  setFieldMessage(question, '');
  go('asystent');
  question.focus();
  if (pending) updateSend(); else form.requestSubmit();
}

// ---------- Wiring ----------

$('search').addEventListener('input', () => { clearTimeout(inbox.timer); inbox.timer = setTimeout(applySearch, SEARCH_DELAY_MS); });
$('search').addEventListener('keydown', event => { if (event.key === 'Enter') applySearch(); });
document.addEventListener('keydown', event => {
  const typing = event.target.closest('input, textarea, select, [contenteditable="true"]');
  if (event.key === '/' && !typing && currentView() === 'skrzynka') { event.preventDefault(); $('search').focus(); }
});
window.addEventListener('hashchange', showView);

showExamples();
showDashExamples();
renderFilters();
renderDetail();
showView();
