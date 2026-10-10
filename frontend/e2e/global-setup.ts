import { mkdirSync, writeFileSync } from 'node:fs';
import { FullConfig } from '@playwright/test';

/**
 * Signs each role in once, through the API's token endpoint, and saves the browser session the app would create.
 * Tests then start already signed in (see `signIn` in support.ts) instead of filling the login form every time: the
 * form has its own tests, and the token endpoint is rate limited per client IP (brute-force protection), which a
 * whole suite signing in from one machine would otherwise trip.
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseURL = config.projects[0].use.baseURL!;
  mkdirSync('e2e/.auth', { recursive: true });
  for (const role of ['viewer', 'clerk', 'manager']) {
    const secret = process.env[`E2E_${role.toUpperCase()}_SECRET`] ?? role;
    const response = await fetch(`${baseURL}/api/auth/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ clientId: role, clientSecret: secret }),
    });
    if (!response.ok) {
      throw new Error(`Cannot sign in as ${role}: ${response.status} ${await response.text()}`);
    }
    const { accessToken, expiresIn } = (await response.json()) as {
      accessToken: string;
      expiresIn: number;
    };
    const claims = JSON.parse(Buffer.from(accessToken.split('.')[1], 'base64url').toString()) as {
      sub: string;
      roles: string | string[];
    };
    // Same shape as the session the app stores after a sign-in (core/auth/auth.ts).
    const session = {
      accessToken,
      expiresAt: Date.now() + expiresIn * 1000,
      userName: claims.sub,
      roles: ([] as string[]).concat(claims.roles),
    };
    writeFileSync(`e2e/.auth/${role}.json`, JSON.stringify(session));
  }
}
