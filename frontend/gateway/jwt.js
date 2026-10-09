// Verifies the API's access tokens at the edge (nginx njs), so that:
//  - requests with a missing, forged or expired token are rejected here, without reaching the API;
//  - the microcache can be shared by everyone with the same role (cache key = role, not token), because the
//    gateway now knows the role is genuine.
//
// The API signs tokens with ES256 and publishes only its public key (JWKS). The gateway holds no secret: it can
// verify tokens but never issue one. The API still validates every token it receives (defence in depth).
//
// Called through `auth_request /_jwt` for every /api request: answers 204 with `X-Jwt-Scope: <roles>` when the
// token is valid, 401 otherwise.

const KEYS = ngx.shared.jwt_keys; // kid -> public JWK (JSON), from the API's JWKS; expires after 1 h
const TOKENS = ngx.shared.jwt_tokens; // token -> "roles|exp" for tokens already verified (skip the crypto)
const CLOCK_SKEW_SECONDS = 30; // same tolerance as the API
const JWKS_REFRESH_MS = 30000; // an unknown kid triggers at most one JWKS download per 30 s

async function verify(r) {
  const match = /^Bearer ([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]+)$/.exec(
    r.headersIn['Authorization'] || '',
  );
  if (!match) {
    return deny(r, 'missing or malformed bearer token');
  }

  const token = match[0].slice('Bearer '.length);
  const now = Math.floor(Date.now() / 1000);
  const known = TOKENS.get(token);
  if (known) {
    const parts = known.split('|');
    return Number(parts[1]) + CLOCK_SKEW_SECONDS > now ? allow(r, parts[0]) : deny(r, 'token expired');
  }

  let header;
  let claims;
  try {
    header = JSON.parse(Buffer.from(match[1], 'base64url').toString());
    claims = JSON.parse(Buffer.from(match[2], 'base64url').toString());
  } catch (e) {
    return deny(r, 'malformed token');
  }

  // ES256 only: "none", HS256 (signed with the public key) and anything else are refused before any crypto.
  if (header.alg !== 'ES256' || typeof header.kid !== 'string') {
    return deny(r, 'unsupported algorithm');
  }

  const jwk = await publicKey(r, header.kid);
  if (!jwk) {
    return deny(r, 'unknown signing key');
  }

  const key = await crypto.subtle.importKey('jwk', jwk, { name: 'ECDSA', namedCurve: 'P-256' }, false, [
    'verify',
  ]);
  const signatureIsValid = await crypto.subtle.verify(
    { name: 'ECDSA', hash: 'SHA-256' },
    key,
    Buffer.from(match[3], 'base64url'),
    Buffer.from(match[1] + '.' + match[2]),
  );
  if (!signatureIsValid) {
    return deny(r, 'invalid signature');
  }

  const audiences = [].concat(claims.aud || []);
  if (claims.iss !== r.variables.jwt_issuer || audiences.indexOf(r.variables.jwt_audience) < 0) {
    return deny(r, 'wrong issuer or audience');
  }
  if (typeof claims.exp !== 'number' || claims.exp + CLOCK_SKEW_SECONDS <= now) {
    return deny(r, 'token expired');
  }
  if (typeof claims.nbf === 'number' && claims.nbf - CLOCK_SKEW_SECONDS > now) {
    return deny(r, 'token not valid yet');
  }

  const roles = [].concat(claims.roles || []).filter((role) => /^[a-z-]+$/.test(role));
  if (roles.length === 0) {
    return deny(r, 'no roles');
  }
  const scope = roles.sort().join(',');

  // Remember it until it expires (at most 10 minutes): the next requests with this token skip the signature check.
  TOKENS.set(token, scope + '|' + claims.exp, Math.max(1, Math.min(claims.exp - now, 600)) * 1000);
  return allow(r, scope);
}

/** The public key for `kid`, from the shared cache or, if unknown, from a fresh download of the API's JWKS. */
async function publicKey(r, kid) {
  const cached = KEYS.get(kid);
  if (cached) {
    return JSON.parse(cached);
  }

  // Once keys are loaded, an unknown kid (a rotation, or an attacker's random value) triggers at most one download per
  // 30 s. Before the first successful download (gateway just started) every request may try.
  const lastDownload = Number(KEYS.get('_downloaded_at') || 0);
  if (KEYS.get('_loaded') && Date.now() - lastDownload < JWKS_REFRESH_MS) {
    return null;
  }
  KEYS.set('_downloaded_at', String(Date.now()));

  try {
    const response = await ngx.fetch(r.variables.jwks_url, { max_response_body_size: 16384 });
    if (!response.ok) {
      ngx.log(ngx.WARN, 'JWKS download failed: HTTP ' + response.status);
      return null;
    }
    const jwks = await response.json();
    (jwks.keys || []).forEach((k) => {
      if (k.kty === 'EC' && k.crv === 'P-256' && typeof k.kid === 'string') {
        // Public members only: whatever else the document contains is never stored or used.
        KEYS.set(k.kid, JSON.stringify({ kty: k.kty, crv: k.crv, x: k.x, y: k.y }));
        KEYS.set('_loaded', '1');
      }
    });
  } catch (e) {
    ngx.log(ngx.WARN, 'JWKS download failed: ' + e);
    return null;
  }

  const downloaded = KEYS.get(kid);
  return downloaded ? JSON.parse(downloaded) : null;
}

function allow(r, scope) {
  r.headersOut['X-Jwt-Scope'] = scope;
  r.return(204);
}

function deny(r, reason) {
  r.headersOut['X-Jwt-Error'] = reason;
  r.return(401);
}

export default { verify };
