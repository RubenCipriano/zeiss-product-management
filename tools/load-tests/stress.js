// Stress test: keep increasing the request rate to find where latency and errors start to degrade.
// No hard thresholds: the result is the curve, read alongside `kubectl top pods`.
import { check } from 'k6';
import { api, createProducts, deleteProducts, readToken, signIn, writeToken } from './lib/api.js';

const PEAK = Number(__ENV.PEAK || 800); // requests per second at the last step

export const options = {
  scenarios: {
    stress: {
      executor: 'ramping-arrival-rate',
      startRate: 50,
      timeUnit: '1s',
      preAllocatedVUs: 200,
      maxVUs: 1500,
      stages: [
        { target: Math.round(PEAK * 0.25), duration: '1m' },
        { target: Math.round(PEAK * 0.5), duration: '1m' },
        { target: Math.round(PEAK * 0.75), duration: '1m' },
        { target: PEAK, duration: '1m' },
        { target: 0, duration: '20s' },
      ],
    },
  },
  thresholds: {
    // Reported, not enforced: tells us whether the system stayed within its SLO under stress.
    http_req_duration: [{ threshold: 'p(95)<1000', abortOnFail: false }],
    http_req_failed: [{ threshold: 'rate<0.05', abortOnFail: false }],
  },
};

export function setup() {
  const tokens = signIn();
  return { tokens, ids: createProducts(tokens.manager, 40, 1000000, 'Stress') };
}

export default function ({ tokens, ids }) {
  const token = readToken(tokens);
  const id = ids[Math.floor(Math.random() * ids.length)];
  const roll = Math.random();
  const response =
    roll < 0.5 ? api.list(token) : roll < 0.8 ? api.get(token, id) : roll < 0.9 ? api.search(token, 'lens') : api.decrement(writeToken(tokens), id, 1);
  check(response, { 'not a server error': (r) => r.status < 500 });
}

export function teardown({ tokens, ids }) {
  deleteProducts(tokens.manager, ids);
}
