// Rate limiting: a single user far above their allowance must be throttled with 429 + Retry-After,
// while the requests that are let through still succeed.
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import http from 'k6/http';
import { api, signIn } from './lib/api.js';

const throttled = new Counter('throttled_429');

export const options = {
  scenarios: {
    burst: {
      executor: 'constant-arrival-rate',
      rate: 300, // one user, well above the API's 50 req/s per user
      timeUnit: '1s',
      duration: '20s',
      preAllocatedVUs: 50,
      maxVUs: 200,
    },
  },
  thresholds: {
    throttled_429: ['count>0'],
    checks: ['rate==1.0'],
  },
};

export function setup() {
  return { token: signIn().viewer };
}

export default function ({ token }) {
  // Different pages, like a client scraping the catalogue. (Repeating one URL would be answered by the gateway's
  // shared microcache without reaching the API: cheap enough not to count against the user's allowance.)
  const response = api.list(token, 1 + Math.floor(Math.random() * 5000), 5);
  if (response.status === 429) {
    throttled.add(1);
  }
  check(response, {
    'either served or throttled': (r) => r.status === 200 || r.status === 429,
    '429 tells when to retry': (r) => r.status !== 429 || !!r.headers['Retry-After'],
  });
}

// 429 is the expected outcome here, not a failure.
http.setResponseCallback(http.expectedStatuses(200, 429));
