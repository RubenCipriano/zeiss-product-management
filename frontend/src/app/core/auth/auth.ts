import { computed, inject, InjectionToken, Service, signal } from '@angular/core';
import { User, UserManager, UserManagerSettings } from 'oidc-client-ts';
import { environment } from '../../../environments/environment';

/** Permissions the UI can check. They mirror the API's authorization policies. */
export type Permission = 'products:read' | 'stock:write' | 'products:write';

/**
 * Role → permission mapping, identical to the API's `Policies`. The UI uses it only to hide actions the user cannot
 * perform; the API enforces it on every request.
 */
const PERMISSIONS_BY_ROLE: Readonly<Record<string, readonly Permission[]>> = {
  viewer: ['products:read'],
  'stock-clerk': ['products:read', 'stock:write'],
  manager: ['products:read', 'stock:write', 'products:write'],
};

export const SIGN_IN_CALLBACK_PATH = '/signin-callback';

/** The OIDC client. A token so tests can substitute it; the app uses oidc-client-ts's `UserManager`. */
export const USER_MANAGER = new InjectionToken<UserManager>('USER_MANAGER', {
  factory: () => {
    const origin = window.location.origin;
    const settings: UserManagerSettings = {
      authority: new URL(environment.auth.authority, origin).toString().replace(/\/$/, ''),
      client_id: environment.auth.clientId,
      redirect_uri: `${origin}${SIGN_IN_CALLBACK_PATH}`,
      post_logout_redirect_uri: origin,
      response_type: 'code', // Authorization Code flow; oidc-client-ts always adds PKCE.
      scope: 'openid profile',
      automaticSilentRenew: true, // refreshes the access token before it expires
    };
    return new UserManager(settings);
  },
});

/**
 * The signed-in user, as signals. Sign-in happens at the identity provider (Keycloak): this app never sees
 * passwords, it only receives tokens.
 */
@Service()
export class Auth {
  private readonly userManager = inject(USER_MANAGER);
  private readonly user = signal<User | null>(null);

  readonly isAuthenticated = computed(() => {
    const user = this.user();
    return user !== null && !user.expired;
  });

  readonly userName = computed(() => {
    const profile = this.user()?.profile;
    return profile?.preferred_username ?? profile?.name ?? '';
  });

  readonly roles = computed<readonly string[]>(() => {
    const roles = this.user()?.profile['roles'];
    return Array.isArray(roles)
      ? roles.filter((role): role is string => typeof role === 'string')
      : [];
  });

  private readonly permissions = computed(
    () => new Set(this.roles().flatMap((role) => PERMISSIONS_BY_ROLE[role] ?? [])),
  );

  constructor() {
    this.userManager.events.addUserLoaded((user) => this.user.set(user));
    this.userManager.events.addUserUnloaded(() => this.user.set(null));
    this.userManager.events.addAccessTokenExpired(() => this.user.set(null));
  }

  /** Reactive when called from a template or `computed`. */
  can(permission: Permission): boolean {
    return this.permissions().has(permission);
  }

  accessToken(): string | null {
    return this.isAuthenticated() ? this.user()!.access_token : null;
  }

  /**
   * Runs before the app renders: completes a sign-in redirect if we are on the callback URL, otherwise restores a
   * session from storage.
   */
  async initialize(): Promise<void> {
    if (window.location.pathname === SIGN_IN_CALLBACK_PATH) {
      try {
        const user = await this.userManager.signinRedirectCallback();
        this.user.set(user);
        const returnUrl =
          typeof user.state === 'string' && user.state.startsWith('/') ? user.state : '/';
        // Replace the callback URL (it contains the one-time code) before the router reads it.
        window.history.replaceState(null, '', returnUrl);
      } catch (error) {
        console.error('Sign-in failed', error);
        window.history.replaceState(null, '', '/');
      }
      return;
    }

    this.user.set(await this.userManager.getUser());
  }

  /** Redirects to the identity provider and comes back to `returnUrl`. */
  login(returnUrl: string): Promise<void> {
    return this.userManager.signinRedirect({ state: returnUrl });
  }

  logout(): Promise<void> {
    return this.userManager.signoutRedirect();
  }
}
