/**
 * Production build. The identity provider is served by the same gateway as the app (`/auth`), so the authority is
 * resolved against the page's origin at runtime.
 */
export const environment = {
  auth: {
    authority: '/auth/realms/product-management',
    clientId: 'product-management-web',
  },
};
