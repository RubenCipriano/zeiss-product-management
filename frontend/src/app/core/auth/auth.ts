// Sign-in and the user's session (Auth), plus the role → permission table the UI uses to hide actions.
import { HttpClient } from '@angular/common/http';
import { computed, inject, InjectionToken, Service, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { API_BASE_URL } from '../config/api-config';
import { ResponseCache } from '../http/response-cache';

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

/**
 * Where the session is kept: `sessionStorage` survives a reload and is gone when the tab closes. An InjectionToken (a
 * named value Angular can inject) so that tests can use a fake storage.
 */
export const SESSION_STORAGE = new InjectionToken<Storage>('SESSION_STORAGE', {
  factory: () => window.sessionStorage,
});

const STORAGE_KEY = 'product-management.session';

interface Session {
  accessToken: string;
  /** Epoch milliseconds. */
  expiresAt: number;
  userName: string;
  roles: string[];
}

interface TokenResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
}

/**
 * The user's session: signs in, keeps the access token, tells the UI who the user is and what they may do, and signs
 * out (also by itself when the token expires). One shared instance for the whole app (`@Service()`).
 *
 * Sign-in calls the API's own token endpoint (`POST /api/auth/token`). The token is a JWT signed by the API; this class
 * only reads its claims to show the user and their roles. The signature is checked by the gateway and the API on every
 * request (docs/guide/04-security.md).
 *
 * The session is a signal: a value that templates and `computed` values re-read automatically when it changes. So
 * signing in or out updates the top bar and every permission check at once.
 */
@Service()
export class Auth {
  private readonly http = inject(HttpClient);
  private readonly storage = inject(SESSION_STORAGE);
  private readonly responseCache = inject(ResponseCache);
  private readonly tokenUrl = `${inject(API_BASE_URL)}/auth/token`;

  private readonly session = signal<Session | null>(this.restore());
  private expiryTimer?: ReturnType<typeof setTimeout>;

  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly userName = computed(() => this.session()?.userName ?? '');
  readonly roles = computed<readonly string[]>(() => this.session()?.roles ?? []);
  private readonly permissions = computed(
    () => new Set(this.roles().flatMap((role) => PERMISSIONS_BY_ROLE[role] ?? [])),
  );

  constructor() {
    this.scheduleExpiry(this.session());
  }

  /** Whether the user may do something. Called from a template or `computed`, it is re-evaluated on sign-in/out. */
  can(permission: Permission): boolean {
    return this.permissions().has(permission);
  }

  accessToken(): string | null {
    return this.session()?.accessToken ?? null;
  }

  isTokenRequest(url: string): boolean {
    return url === this.tokenUrl;
  }

  login(clientId: string, clientSecret: string): Observable<void> {
    return this.http.post<TokenResponse>(this.tokenUrl, { clientId, clientSecret }).pipe(
      map((response) => {
        const claims = decodeClaims(response.accessToken);
        this.start({
          accessToken: response.accessToken,
          expiresAt: Date.now() + response.expiresIn * 1000,
          userName: typeof claims['sub'] === 'string' ? claims['sub'] : clientId,
          roles: toRoles(claims['roles']),
        });
      }),
    );
  }

  logout(): void {
    clearTimeout(this.expiryTimer);
    // Cached responses belong to this session: the next user must not see them.
    this.responseCache.clear();
    this.storage.removeItem(STORAGE_KEY);
    this.session.set(null);
  }

  private start(session: Session): void {
    this.storage.setItem(STORAGE_KEY, JSON.stringify(session));
    this.session.set(session);
    this.scheduleExpiry(session);
  }

  /** Ends the session when the token expires, so the UI never keeps using a token the API will reject. */
  private scheduleExpiry(session: Session | null): void {
    clearTimeout(this.expiryTimer);
    if (session) {
      this.expiryTimer = setTimeout(
        () => this.logout(),
        Math.max(0, session.expiresAt - Date.now()),
      );
    }
  }

  private restore(): Session | null {
    try {
      const stored = JSON.parse(this.storage.getItem(STORAGE_KEY) ?? 'null') as Session | null;
      return stored && stored.expiresAt > Date.now() ? stored : null;
    } catch {
      return null;
    }
  }
}

/** Reads (does not verify) the JWT payload. */
function decodeClaims(token: string): Record<string, unknown> {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))) as Record<
      string,
      unknown
    >;
  } catch {
    return {};
  }
}

function toRoles(claim: unknown): string[] {
  if (typeof claim === 'string') {
    return [claim];
  }
  return Array.isArray(claim)
    ? claim.filter((role): role is string => typeof role === 'string')
    : [];
}
