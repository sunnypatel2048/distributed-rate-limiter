// Load test for the nginx -> 3 replicas -> Redis cluster.
//
// Three scenarios run back to back (never overlapping, so they don't compete for CPU):
//
//   baseline      /health at RATE req/s. Same app, same nginx hop, NO rate limiter.
//   rate_limited  /api/orders, /api/search, /api/products at RATE req/s, spread over
//                 100k random clients so almost every request is allowed. Measures the
//                 cost of the limiter's Redis round trip; compared with baseline, that's
//                 the limiter's overhead. Also broken down per algorithm.
//   burst         BURST_CLIENTS "hot" clients hammering /api/orders far above their limit.
//                 Checks correctness under load: total allowed must match what the
//                 Token Bucket math says, no matter which replica handled each request.
//
// Run via Docker (no k6 install needed):   docker compose run --rm k6
// Tune:  docker compose run --rm -e RATE=1000 -e DURATION_SECONDS=60 k6

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const RATE = parseInt(__ENV.RATE || '500', 10);
const DURATION_SECONDS = parseInt(__ENV.DURATION_SECONDS || '30', 10);
const BURST_CLIENTS = parseInt(__ENV.BURST_CLIENTS || '20', 10);
const BURST_RATE = parseInt(__ENV.BURST_RATE || '200', 10);
const P99_TARGET_MS = parseInt(__ENV.P99_TARGET_MS || '100', 10);
const GAP_SECONDS = 5;

// Must match the "orders" policy in src/RateLimiter.Demo/appsettings.json.
const ORDERS_CAPACITY = 10;
const ORDERS_WINDOW_SECONDS = 60;

// Each hot client starts with a full bucket, then earns tokens for the length of the burst.
const tokensEarnedPerClient = Math.floor((DURATION_SECONDS * ORDERS_CAPACITY) / ORDERS_WINDOW_SECONDS);
const BURST_MIN_ALLOWED = BURST_CLIENTS * ORDERS_CAPACITY;
const BURST_MAX_ALLOWED = BURST_CLIENTS * (ORDERS_CAPACITY + tokensEarnedPerClient + 1); // +1: timing slack

const ENDPOINTS = [
  { name: 'orders', path: '/api/orders', algorithm: 'Token Bucket' },
  { name: 'search', path: '/api/search?q=k6', algorithm: 'Sliding Window' },
  { name: 'products', path: '/api/products', algorithm: 'Fixed Window' },
];

const allowed = new Counter('rate_limit_allowed');
const rejected = new Counter('rate_limit_rejected');

// 429 is a correct answer from a rate limiter, not an error. Anything else is.
http.setResponseCallback(http.expectedStatuses(200, 429));

function scenario(exec, rate, index) {
  return {
    executor: 'constant-arrival-rate',
    exec,
    rate,
    timeUnit: '1s',
    duration: `${DURATION_SECONDS}s`,
    preAllocatedVUs: 50,
    maxVUs: 1000,
    startTime: `${index * (DURATION_SECONDS + GAP_SECONDS)}s`,
  };
}

const latencyTarget = [`p(99)<${P99_TARGET_MS}`];

export const options = {
  scenarios: {
    baseline: scenario('baseline', RATE, 0),
    rate_limited: scenario('rateLimited', RATE, 1),
    burst: scenario('burst', BURST_RATE, 2),
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    // Only 200/429 are acceptable responses.
    http_req_failed: ['rate<0.01'],
    // If k6 couldn't start iterations on time, the machine couldn't sustain RATE.
    dropped_iterations: ['count==0'],

    'http_req_duration{scenario:baseline}': latencyTarget,
    'http_req_duration{scenario:rate_limited}': latencyTarget,
    'http_req_duration{scenario:rate_limited,endpoint:orders}': latencyTarget,
    'http_req_duration{scenario:rate_limited,endpoint:search}': latencyTarget,
    'http_req_duration{scenario:rate_limited,endpoint:products}': latencyTarget,
    'http_req_duration{scenario:burst}': latencyTarget,

    // Thresholds also make k6 keep per-scenario counts for the summary below.
    'http_reqs{scenario:baseline}': ['count>0'],
    'http_reqs{scenario:rate_limited}': ['count>0'],
    'http_reqs{scenario:burst}': ['count>0'],
    'rate_limit_rejected{scenario:rate_limited}': ['count>=0'],

    // Correctness under load: one combined limit across all replicas.
    'rate_limit_allowed{scenario:burst}': [`count>=${BURST_MIN_ALLOWED}`, `count<=${BURST_MAX_ALLOWED}`],
  },
};

// Runs once; the run id makes every run's API keys fresh, so an earlier run's
// spent quota can't skew this one.
export function setup() {
  return { runId: `${Date.now()}` };
}

function get(path, apiKey, endpoint) {
  const res = http.get(`${BASE_URL}${path}`, {
    headers: { 'X-Api-Key': apiKey },
    tags: { endpoint },
  });

  check(res, { 'status is 200 or 429': (r) => r.status === 200 || r.status === 429 });
  if (res.status === 200) allowed.add(1);
  if (res.status === 429) rejected.add(1);
  return res;
}

export function baseline(data) {
  get('/health', `${data.runId}-baseline`, 'health');
}

export function rateLimited(data) {
  const endpoint = ENDPOINTS[Math.floor(Math.random() * ENDPOINTS.length)];
  const client = Math.floor(Math.random() * 100000);
  get(endpoint.path, `${data.runId}-client-${client}`, endpoint.name);
}

export function burst(data) {
  const client = Math.floor(Math.random() * BURST_CLIENTS);
  get('/api/orders', `${data.runId}-hot-${client}`, 'orders');
}

// ---------- summary ----------

function stat(data, metric, key) {
  const m = data.metrics[metric];
  return m && m.values[key] !== undefined ? m.values[key] : undefined;
}

function ms(value) {
  return value === undefined ? '-' : `${value.toFixed(2)} ms`;
}

function row(label, data, durationMetric, countMetric) {
  const count = countMetric ? stat(data, countMetric, 'count') : undefined;
  const cells = [
    label.padEnd(34),
    (count === undefined ? '' : String(count)).padStart(9),
    (count === undefined ? '' : (count / DURATION_SECONDS).toFixed(0)).padStart(8),
    ms(stat(data, durationMetric, 'med')).padStart(11),
    ms(stat(data, durationMetric, 'p(95)')).padStart(11),
    ms(stat(data, durationMetric, 'p(99)')).padStart(11),
    ms(stat(data, durationMetric, 'max')).padStart(11),
  ];
  return cells.join(' ');
}

export function handleSummary(data) {
  const header = [
    'Scenario'.padEnd(34), 'Requests'.padStart(9), 'req/s'.padStart(8),
    'p50'.padStart(11), 'p95'.padStart(11), 'p99'.padStart(11), 'max'.padStart(11),
  ].join(' ');

  const lines = [
    '',
    `Rate limiter benchmark: ${RATE} req/s target, ${DURATION_SECONDS}s per scenario, via ${BASE_URL}`,
    '',
    header,
    '-'.repeat(header.length),
    row('baseline (/health, no limiter)', data, 'http_req_duration{scenario:baseline}', 'http_reqs{scenario:baseline}'),
    row('rate_limited (all 3 algorithms)', data, 'http_req_duration{scenario:rate_limited}', 'http_reqs{scenario:rate_limited}'),
    ...ENDPOINTS.map((e) =>
      row(`  ${e.name} (${e.algorithm})`, data, `http_req_duration{scenario:rate_limited,endpoint:${e.name}}`)),
    row(`burst (${BURST_CLIENTS} hot clients)`, data, 'http_req_duration{scenario:burst}', 'http_reqs{scenario:burst}'),
    '',
  ];

  const baseP99 = stat(data, 'http_req_duration{scenario:baseline}', 'p(99)');
  const limitedP99 = stat(data, 'http_req_duration{scenario:rate_limited}', 'p(99)');
  const baseMed = stat(data, 'http_req_duration{scenario:baseline}', 'med');
  const limitedMed = stat(data, 'http_req_duration{scenario:rate_limited}', 'med');
  if (baseP99 !== undefined && limitedP99 !== undefined) {
    const signed = (d) => `${d >= 0 ? '+' : ''}${d.toFixed(2)} ms`;
    lines.push(`Rate limiter overhead: p50 ${signed(limitedMed - baseMed)}, p99 ${signed(limitedP99 - baseP99)} (rate_limited vs baseline)`);
    lines.push('  p50 is the reliable figure; at low load, p99 differences of a fraction of a ms are run-to-run noise.');
  }

  const limitedRejected = stat(data, 'rate_limit_rejected{scenario:rate_limited}', 'count') || 0;
  lines.push(`Rejected in rate_limited scenario: ${limitedRejected} (expected ~0; 100k clients each stay far under their limit)`);

  const burstAllowed = stat(data, 'rate_limit_allowed{scenario:burst}', 'count') || 0;
  const burstTotal = stat(data, 'http_reqs{scenario:burst}', 'count') || 0;
  const burstOk = burstAllowed >= BURST_MIN_ALLOWED && burstAllowed <= BURST_MAX_ALLOWED;
  lines.push(`Burst correctness: ${burstAllowed} of ${burstTotal} allowed, expected ${BURST_MIN_ALLOWED}-${BURST_MAX_ALLOWED} -> ${burstOk ? 'PASS' : 'FAIL'}`);

  const dropped = stat(data, 'dropped_iterations', 'count') || 0;
  const failedRate = stat(data, 'http_req_failed', 'rate') || 0;
  lines.push(`Dropped iterations: ${dropped}${dropped > 0 ? '  <- machine could not sustain the target rate; lower RATE' : ''}`);
  lines.push(`Unexpected statuses (not 200/429): ${(failedRate * 100).toFixed(2)}%`);
  lines.push('');

  const failedThresholds = Object.entries(data.metrics)
    .filter(([, m]) => m.thresholds && Object.values(m.thresholds).some((t) => !t.ok))
    .map(([name]) => name);
  lines.push(failedThresholds.length === 0
    ? 'All thresholds passed.'
    : `Thresholds FAILED: ${failedThresholds.join(', ')}`);
  lines.push('');

  return {
    stdout: lines.join('\n'),
    [__ENV.SUMMARY_PATH || 'loadtest/results/latest-summary.json']: JSON.stringify(data, null, 2),
  };
}
