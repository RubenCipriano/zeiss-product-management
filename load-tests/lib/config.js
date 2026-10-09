// Shared settings. Override with environment variables: k6 run -e BASE_URL=http://localhost:8080 smoke.js
export const BASE_URL = (__ENV.BASE_URL || 'http://gateway.product-management.svc.cluster.local').replace(/\/$/, '');
export const TOKEN_URL = `${BASE_URL}/auth/realms/product-management/protocol/openid-connect/token`;
export const LOAD_TEST_USERS = Number(__ENV.LOAD_TEST_USERS || 10);
export const LOW_STOCK_THRESHOLD = 10;
