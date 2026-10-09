/**
 * `ng serve`. The app runs on :4200 while Keycloak is reached through the cluster gateway on :8080
 * (`kubectl port-forward`), so the authority is absolute and must match the issuer Keycloak puts in tokens.
 */
export const environment = {
  auth: {
    authority: 'http://localhost:8080/auth/realms/product-management',
    clientId: 'product-management-web',
  },
};
