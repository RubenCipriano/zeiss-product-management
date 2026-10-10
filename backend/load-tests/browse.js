// Read capacity: catalogue browsing as it really happens, with a hot set. Most reads go to the first pages of the
// list, the most viewed products, popular searches and the low-stock view; these are what the gateway's microcache
// and Redis are for. Read-only, on the bulk dataset (load-tests/seed-bulk.sh). Compare with `teams`, the worst
// case: uniform random reads over 850,000 products plus 25 % writes.
//
//   PEAK=12000 load-tests/run.sh browse      # staircase in five steps up to PEAK requests/s (limits lifted)
import { check } from 'k6';
import { Rate } from 'k6/metrics';
import { api, readToken, signIn } from './lib/api.js';

const PEAK = Number(__ENV.PEAK || 2000);
// RATE set: hold that many requests/s for HOLD (default 1m) instead of the staircase, for a clean sustained number.
const RATE = Number(__ENV.RATE || 0);
const HOLD = __ENV.HOLD || '1m';
const HOT_PRODUCTS = 500;
const gatewayCacheHit = new Rate('gateway_cache_hit'); // answered by nginx without reaching the API
const SEARCH_TERMS = ['Progressive', 'Titanium', 'Toric', 'Aviator', 'Saline', 'Daily', 'Kids', 'Office'];

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  scenarios: RATE
    ? {
        browse: {
          executor: 'constant-arrival-rate',
          rate: RATE,
          timeUnit: '1s',
          duration: HOLD,
          preAllocatedVUs: 1000,
          maxVUs: 3000,
        },
      }
    : {
    browse: {
      executor: 'ramping-arrival-rate',
      startRate: 100,
      timeUnit: '1s',
      preAllocatedVUs: 500,
      maxVUs: 3000,
      stages: [0.2, 0.4, 0.6, 0.8, 1].flatMap((share) => [
        { target: Math.round(PEAK * share), duration: '15s' },
        { target: Math.round(PEAK * share), duration: '45s' },
      ]),
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<250'],
  },
};

export function setup() {
  const tokens = signIn();
  // The hot set: the products on the first pages of the low-stock view, which a team checks all day.
  const ids = [];
  for (let page = 1; ids.length < HOT_PRODUCTS; page++) {
    const response = api.stockLevel(tokens.manager, 0, 1000000, page);
    ids.push(...response.json('items').map((p) => p.id));
  }
  return { tokens, hot: ids.slice(0, HOT_PRODUCTS) };
}

export default function ({ tokens, hot }) {
  const token = readToken(tokens);
  const roll = Math.random();
  let response;
  if (roll < 0.4) {
    response = api.list(token, 1 + Math.floor(Math.random() * 20));
  } else if (roll < 0.7) {
    response = api.get(token, hot[Math.floor(Math.random() * hot.length)]);
  } else if (roll < 0.9) {
    response = api.search(token, SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)]);
  } else {
    response = api.stockLevel(token, 0, 10);
  }
  check(response, { ok: (r) => r.status === 200 });
  gatewayCacheHit.add(response.headers['X-Cache-Status'] === 'HIT');
}

export function handleSummary(data) {
  const m = data.metrics;
  const v = m.http_req_duration.values;
  return {
    stdout: [
      '',
      `requests        ${m.http_reqs.values.count} (${m.http_reqs.values.rate.toFixed(0)}/s average), failed ${(m.http_req_failed.values.rate * 100).toFixed(2)}%`,
      `latency         p50 ${v.med.toFixed(1)} ms   p95 ${v['p(95)'].toFixed(1)} ms   p99 ${v['p(99)'].toFixed(1)} ms`,
      `not sent        ${m.dropped_iterations?.values.count ?? 0} (k6 could not start them: load generator saturated)`,
      `thresholds      ${Object.values(m).every((x) => Object.values(x.thresholds || {}).every((t) => t.ok)) ? 'ok' : 'crossed'}`,
      `gateway cache   ${((m.gateway_cache_hit?.values.rate ?? 0) * 100).toFixed(1)} % of requests answered by nginx`,
    ].join('\n'),
  };
}
