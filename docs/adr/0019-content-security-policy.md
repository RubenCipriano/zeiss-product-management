# 0019 — Content Security Policy and no third-party assets

**Status:** accepted

## Context

The web app keeps its access token in `sessionStorage` ([0009](0009-authentication-authorization.md)). Any script
that runs in the page can read it, so cross-site scripting is the main risk to the session. Angular already escapes
everything it renders; a Content Security Policy is the second layer that stops an injected script from running at
all. The app also loaded its font from Google Fonts, which sends every user's IP address to a third party (a GDPR
concern in the EU) and would force the policy to allow that origin.

## Decision

* **The gateway sends a strict policy on every response** (`frontend/gateway/security-headers.conf`, one snippet
  included everywhere):

  ```
  default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;
  font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self';
  frame-ancestors 'none'
  ```

  plus `Permissions-Policy` (camera, microphone, geolocation, payment, USB off), `Cross-Origin-Opener-Policy` and
  `Cross-Origin-Resource-Policy: same-origin`, `nosniff`, `X-Frame-Options: DENY` and a referrer policy.
* **No inline scripts:** the production build no longer inlines critical CSS (`inlineCritical: false`), which used a
  small inline `<script>` to swap the stylesheet in. With one stylesheet the cost is negligible, and `script-src`
  can stay `'self'` without hashes.
* **`'unsafe-inline'` for styles only.** Angular adds component styles as `<style>` elements. Styles cannot execute
  code; removing it needs a per-response nonce (`ngCspNonce`) injected by the server, a possible next step.
* **Self-hosted font** (`@fontsource-variable/inter`, bundled by the build): no request leaves the origin.
* Verified in the browser: the app runs with the policy and the console reports no violations.

## Consequences

* An injected `<script>`, an inline event handler or a script from another origin does not run, and the page cannot
  be framed (clickjacking).
* Any future third-party asset (analytics, a CDN) must be added to the policy deliberately.
* With TLS in front (production), add `Strict-Transport-Security`.
