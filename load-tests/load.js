// Load test: realistic traffic mix at a steady, expected peak rate, checked against service level objectives.
import { check } from 'k6';
import { api, createProducts, deleteProducts, signIn, tokenForVu } from './lib/api.js';

const RATE = Number(__ENV.RATE || 150); // requests per second at the plateau

export const options = {
  scenarios: {
    catalogue: {
      executor: 'ramping-arrival-rate',
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 300,
      stages: [
        { target: RATE, duration: '30s' }, // ramp up
        { target: RATE, duration: '2m' }, // plateau
        { target: 0, duration: '15s' }, // ramp down
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<300', 'p(99)<800'],
    'http_req_duration{name:GET /products}': ['p(95)<250'],
    'http_req_duration{name:POST /products/{id}/decrement-stock}': ['p(95)<400'],
    checks: ['rate>0.99'],
  },
};

const SEARCH_TERMS = ['lens', 'frame', 'load', 'product', 'case', 'spray'];

export function setup() {
  const tokens = signIn();
  // Plenty of stock so decrements don't run out during the test.
  return { tokens, ids: createProducts(tokens.manager, 40, 100000, 'Load') };
}

export default function ({ tokens, ids }) {
  const token = tokenForVu(tokens);
  const id = ids[Math.floor(Math.random() * ids.length)];
  const roll = Math.random();

  if (roll < 0.35) {
    check(api.list(token, 1 + Math.floor(Math.random() * 3)), { 'list ok': (r) => r.status === 200 });
  } else if (roll < 0.55) {
    check(api.get(token, id), { 'get ok': (r) => r.status === 200 });
  } else if (roll < 0.7) {
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    check(api.search(token, term), { 'search ok': (r) => r.status === 200 });
  } else if (roll < 0.8) {
    check(api.stockLevel(token, 0, 10), { 'stock-level ok': (r) => r.status === 200 });
  } else if (roll < 0.92) {
    check(api.decrement(token, id, 1), { 'decrement ok': (r) => r.status === 200 });
  } else {
    check(api.add(token, id, 1), { 'add ok': (r) => r.status === 200 });
  }
}

export function teardown({ tokens, ids }) {
  deleteProducts(tokens.manager, ids);
}
