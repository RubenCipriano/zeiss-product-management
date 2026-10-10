// Shared settings. Override with environment variables: k6 run -e BASE_URL=http://localhost:8080 smoke.js
export const BASE_URL = (__ENV.BASE_URL || 'http://gateway.product-management.svc.cluster.local:8080').replace(/\/$/, '');
export const TOKEN_URL = `${BASE_URL}/api/auth/token`;
export const LOW_STOCK_THRESHOLD = 10;
