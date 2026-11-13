// Ask page: sends one question at a time to POST /ask and shows the answer, a refusal or an error in the log.
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
  chev: '<path d="M4 6l4 4 4-4"/>'
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
    card.addEventListener('click', () => { question.value = q; form.requestSubmit(); });
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

async function readJson(response) {
  try { return await response.json(); } catch { return null; }
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
  if (result.ok) {
    finishEntry(entry, true, ...result.content);
    question.value = '';
    question.focus();
  } else {
    finishEntry(entry, false, alertBox('alert', result.message));
    question.value = q;
    if (!question.hasAttribute('aria-invalid') && !number.hasAttribute('aria-invalid')) question.focus();
  }
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
$('navAsk').addEventListener('click', () => question.focus());
$('collapse').addEventListener('click', () => {
  const app = $('app'), button = $('collapse');
  const collapsed = app.dataset.collapsed !== 'true';
  app.dataset.collapsed = String(collapsed);
  button.setAttribute('aria-expanded', String(!collapsed));
  button.setAttribute('aria-label', collapsed ? 'Rozwiń menu' : 'Zwiń menu');
});

showExamples();
