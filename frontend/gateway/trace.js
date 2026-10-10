// Gives every request a trace id, so its log lines can be found together. The id travels in the `traceparent` header
// (a W3C standard: "00-<trace id>-<parent id>-<flags>"), added before the request is proxied. The API puts that trace
// id on every log line it writes for the request, and the gateway writes it into its access log, so one search by
// trace id in Grafana shows the gateway's line and all of the API's lines.
//
// A valid incoming traceparent is kept (a caller already tracing); otherwise one is made from $request_id, which is
// already 32 random hex characters: exactly a W3C trace id. $request_id must also appear in the nginx configuration
// (the access log): nginx caches a variable per request only when the configuration references it; otherwise every
// read here would return a new random id, and the logged id would differ from the one sent to the API.
const VALID = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/;

function traceparent(r) {
  const incoming = r.headersIn['traceparent'];
  const match = incoming && VALID.exec(incoming);
  if (match && !/^0+$/.test(match[1]) && !/^0+$/.test(match[2])) {
    return incoming;
  }
  const id = r.variables.request_id;
  return '00-' + id + '-' + id.slice(16) + '-01';
}

// The id of the traceparent actually sent upstream (the $traceparent variable), never a second computation.
function traceId(r) {
  return r.variables.traceparent.split('-')[1];
}

export default { traceparent, traceId };
