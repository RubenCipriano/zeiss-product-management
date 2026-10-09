// Many teams at once on the large catalogue (load-tests/seed-bulk.sh): ten API consumers (stores, warehouses, the
// online shop) browse, search and change stock concurrently, while the API autoscales.
//
// It also proves that retries are never applied twice. A share of the stock changes is sent again with the same
// Idempotency-Key, right after the first attempt (a client retrying after a timeout) or at the same moment (a double
// click). The gateway balances to the least busy replica, so the copies regularly land on different replicas. The
// summary prints how many changes the API applied; run.sh compares that with the rows the ledger gained during the
// run. They must be equal: one applied change, one ledger row, however many copies were sent.
//
// Like a correct client, it retries a change whose outcome it does not know (a timeout, a 5xx: the server may have
// applied it) with the same key, until it gets an answer. Without that retry, a change applied by the server after
// the client gave up would be in the ledger but unknown to the client; with it, the outcome is always known.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { BASE_URL } from './lib/config.js';
import { api, signIn, tokenForVu } from './lib/api.js';

const RATE = Number(__ENV.RATE || 300); // iterations per second at the plateau (stays within the per-consumer limits)
// PEAK set: a capacity staircase instead of a plateau, in five one-minute steps up to PEAK (run.sh lifts the rate
// limits for it). Where latency and errors start to climb is the system's capacity.
const PEAK = Number(__ENV.PEAK || 0);
const STAGES = PEAK
  ? [0.2, 0.4, 0.6, 0.8, 1].flatMap((share) => [
      { target: Math.round(PEAK * share), duration: '15s' },
      { target: Math.round(PEAK * share), duration: '45s' },
    ])
  : [
      { target: RATE, duration: '1m' },
      { target: RATE, duration: '4m' },
    ];
const ID_MIN = Number(__ENV.ID_MIN || 100000);
const ID_MAX = Number(__ENV.ID_MAX || 950000);
const HOT_PRODUCTS = 50; // products every team changes: maximum contention on the same rows

// Per logical stock change (one Idempotency-Key):
const applied = new Counter('stock_changes_applied');
const rejected = new Counter('stock_changes_rejected'); // not enough stock, or an id gap
// Per copy: answered from the stored response, or refused because the first copy was still running.
const replayed = new Counter('stock_changes_replayed');
const inProgress = new Counter('stock_changes_in_progress');
const unknown = new Counter('stock_changes_unknown'); // no answer even after retries: may or may not have applied

export const options = {
  scenarios: {
    teams: {
      executor: 'ramping-arrival-rate',
      startRate: 20,
      timeUnit: '1s',
      preAllocatedVUs: PEAK ? 500 : 100,
      maxVUs: PEAK ? 3000 : 600,
      stages: [...STAGES, { target: 0, duration: '30s' }],
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{name:GET /products/{id}}': ['p(95)<200'],
    'http_req_duration{name:GET /products}': ['p(95)<500'],
    'http_req_duration{name:GET /products/search}': ['p(95)<800'],
    'http_req_duration{name:GET /products/stock-level}': ['p(95)<500'],
    'http_req_duration{name:GET /products/{id}/stock-movements}': ['p(95)<300'],
    'http_req_duration{name:POST stock change}': ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

const SEARCH_TERMS = ['Progressive', 'Horizon Titanium', 'Toric', 'M-482', 'Aviator', 'Saline', 'Clarity Office', 'Kids'];

export function setup() {
  const tokens = signIn();
  // Hot products with plenty of stock, so concurrent decrements are rarely refused.
  const response = api.stockLevel(tokens.manager, 500, 1000000);
  const hot = response.json('items').slice(0, HOT_PRODUCTS).map((product) => product.id);
  return { tokens, hot };
}

const randomId = () => ID_MIN + Math.floor(Math.random() * (ID_MAX - ID_MIN + 1));
const ok = (r) => r.status === 200;
const okOrMissing = (r) => r.status === 200 || r.status === 404; // ids deleted by earlier tests are gaps

export default function ({ tokens, hot }) {
  const token = tokenForVu(tokens);
  const roll = Math.random();

  if (roll < 0.3) {
    // Browsing: the first pages most of the time, sometimes deep into the catalogue.
    const page = Math.random() < 0.8 ? 1 + Math.floor(Math.random() * 5) : 1 + Math.floor(Math.random() * 2000);
    check(api.list(token, page), { 'list ok': ok });
  } else if (roll < 0.55) {
    check(get(token, `/api/products/${randomId()}`, 'GET /products/{id}'), { 'get ok': okOrMissing });
  } else if (roll < 0.7) {
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    check(api.search(token, term), { 'search ok': ok });
  } else if (roll < 0.75) {
    check(api.stockLevel(token, 0, 10), { 'stock-level ok': ok });
  } else if (roll < 0.8) {
    check(get(token, `/api/products/${randomId()}/stock-movements`, 'GET /products/{id}/stock-movements'), {
      'history ok': okOrMissing,
    });
  } else {
    changeStock(token, hot);
  }
}

function changeStock(token, hot) {
  const id = Math.random() < 0.5 ? hot[Math.floor(Math.random() * hot.length)] : randomId();
  const add = Math.random() < 0.4;
  const url = `${BASE_URL}/api/products/${id}/${add ? 'add-to-stock' : 'decrement-stock'}/${1 + Math.floor(Math.random() * 5)}`;
  const params = {
    headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': uuid() },
    tags: { name: 'POST stock change' },
    // 404: a gap in the ids. 409: not enough stock, or (for a simultaneous copy) the first copy is still running.
    responseCallback: http.expectedStatuses(200, 404, 409),
  };

  const duplicate = Math.random();
  let responses;
  if (duplicate < 0.1) {
    // Double click: two copies at the same instant.
    responses = http.batch([['POST', url, null, params], ['POST', url, null, params]])
      .map((r) => untilKnown(r, url, params));
  } else if (duplicate < 0.4) {
    // A retry after the first response (as if it had been lost).
    responses = [untilKnown(http.post(url, null, params), url, params)];
    responses.push(untilKnown(http.post(url, null, params), url, params));
  } else {
    responses = [untilKnown(http.post(url, null, params), url, params)];
  }

  // One logical change, however many copies: a 200 from any copy (fresh or replayed) means it was applied, once.
  const all = responses.map(({ response }) => response);
  if (all.some((r) => r.status === 200)) applied.add(1);
  else if (all.some((r) => r.status === 404 || (r.status === 409 && !inProgressAnswer(r)))) rejected.add(1);
  else unknown.add(1);
  for (const r of all) {
    if (r.headers['Idempotent-Replayed'] === 'true') replayed.add(1);
    else if (inProgressAnswer(r)) inProgress.add(1);
  }
  // A retry must return exactly what the first attempt returned.
  if (responses.length === 2 && responses.every(({ response }) => response.status === 200)) {
    check(responses, { 'retry returns the same result': ([a, b]) => a.response.body === b.response.body });
  }
  check(responses[0].response, { 'stock change handled': (r) => [200, 404, 409].includes(r.status) });
}

const outcomeUnknown = (r) => r.status === 0 || r.status >= 500;
const inProgressAnswer = (r) => r.status === 409 && r.json('errorCode') === 'idempotency_key_in_progress';

/**
 * Retries with the same Idempotency-Key while the outcome is unknown: safe, because the server applies a key at most
 * once. After a lost answer, "in progress" only means the first attempt is still running: keep asking.
 */
function untilKnown(response, url, params) {
  let r = response;
  let retried = false;
  for (let attempt = 1; attempt <= 8 && (outcomeUnknown(r) || (retried && inProgressAnswer(r))); attempt++) {
    sleep(0.25 * attempt);
    r = http.post(url, null, params);
    retried = true;
  }
  return { response: r, retried };
}

function get(token, path, name) {
  return http.get(`${BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { name },
    responseCallback: http.expectedStatuses(200, 404),
  });
}

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export function handleSummary(data) {
  const m = data.metrics;
  const count = (name) => (m[name] ? m[name].values.count : 0);
  const line = (name) => {
    const v = m[`http_req_duration{name:${name}}`]?.values;
    return v ? `  ${name.padEnd(36)} p50 ${v.med.toFixed(1)} ms   p95 ${v['p(95)'].toFixed(1)} ms` : '';
  };
  const thresholds = Object.entries(m)
    .flatMap(([name, metric]) => Object.entries(metric.thresholds || {}).map(([t, r]) => `  ${r.ok ? 'ok  ' : 'FAIL'} ${name} ${t}`));
  return {
    stdout: [
      '',
      `requests        ${m.http_reqs.values.count} (${m.http_reqs.values.rate.toFixed(0)}/s), failed ${(m.http_req_failed.values.rate * 100).toFixed(2)}%`,
      `latency         p50 ${m.http_req_duration.values.med.toFixed(1)} ms   p95 ${m.http_req_duration.values['p(95)'].toFixed(1)} ms   p99 ${m.http_req_duration.values['p(99)'].toFixed(1)} ms`,
      ...['GET /products', 'GET /products/{id}', 'GET /products/search', 'GET /products/stock-level',
        'GET /products/{id}/stock-movements', 'POST stock change'].map(line).filter(Boolean),
      `stock changes   applied ${count('stock_changes_applied')}, replayed ${count('stock_changes_replayed')}, ` +
        `in progress ${count('stock_changes_in_progress')}, refused ${count('stock_changes_rejected')}, ` +
        `unknown ${count('stock_changes_unknown')}`,
      'thresholds',
      ...thresholds,
      `TEAMS_RESULT applied=${count('stock_changes_applied')} unknown=${count('stock_changes_unknown')}`,
      '',
    ].join('\n'),
  };
}
