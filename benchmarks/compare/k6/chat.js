// One chat message per iteration, against the Ume gateway, eneo, or the fake LLM directly (the floor).
//
//   TARGET   ume | eneo | fake
//   STREAM   true | false
//   VUS      concurrent clients        DURATION  e.g. 30s
//   OUT      results file name         KEY       Ume virtual key (TARGET=ume)
//
// Every response is checked: status 200 and the fake LLM's answer in the body, so a platform that fails fast (or
// returns an error inside an SSE stream) cannot look faster than it is.
import http from 'k6/http';
import { check, fail } from 'k6';

const TARGET = __ENV.TARGET;
const STREAM = __ENV.STREAM === 'true';
const QUESTION = 'Vilka handlingar behöver jag skicka in för att ansöka om bygglov för ett uterum, och hur lång är handläggningstiden?';
const ANSWER_MARKER = 'testsvar'; // one word: streamed answers arrive one word per event

const BASE = {
  ume: 'http://ume-gateway:8080',
  eneo: 'http://eneo-backend:8000/api/v1',
  fake: 'http://fake-llm:8080',
}[TARGET];

export const options = {
  scenarios: {
    load: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 1),
      duration: __ENV.DURATION || '30s',
      gracefulStop: '30s',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  discardResponseBodies: false,
};

export function setup() {
  if (!BASE) {
    fail(`Unknown TARGET '${TARGET}'`);
  }

  if (TARGET !== 'eneo') {
    return { headers: TARGET === 'ume' ? { Authorization: `Bearer ${__ENV.KEY}` } : {} };
  }

  // eneo: one password login (OAuth2 password flow), then the user's personal assistant.
  const login = http.post(`${BASE}/users/login/token/`, { username: 'compare@example.com', password: 'ComparePassword123!' });
  if (login.status !== 200) {
    fail(`eneo login failed: ${login.status} ${login.body}`);
  }

  const headers = { Authorization: `Bearer ${login.json('access_token')}` };
  const space = http.get(`${BASE}/spaces/type/personal/`, { headers });
  const assistantId = space.json('default_assistant.id');
  if (!assistantId) {
    fail(`eneo personal assistant not found: ${space.status} ${space.body}`);
  }

  return { headers, assistantId };
}

export default function (data) {
  const headers = { ...data.headers, 'Content-Type': 'application/json' };
  const body = TARGET === 'eneo'
    // A new conversation per message, like a single gateway call (continuing one would grow the prompt every turn).
    ? { assistant_id: data.assistantId, question: QUESTION, stream: STREAM }
    : { model: TARGET === 'ume' ? 'ume/chat' : 'fake-chat', messages: [{ role: 'user', content: QUESTION }], max_tokens: 400, stream: STREAM };
  const path = TARGET === 'eneo' ? '/conversations/' : '/v1/chat/completions';

  const res = http.post(`${BASE}${path}`, JSON.stringify(body), { headers, timeout: '60s' });
  check(res, {
    'status 200': (r) => r.status === 200,
    'answer from fake LLM': (r) => typeof r.body === 'string' && r.body.includes(ANSWER_MARKER),
  });
}

export function handleSummary(data) {
  const trend = (name) => {
    const m = data.metrics[name];
    return m ? m.values : null;
  };
  const result = {
    target: TARGET,
    stream: STREAM,
    vus: Number(__ENV.VUS || 1),
    duration: __ENV.DURATION || '30s',
    requests: data.metrics.http_reqs ? data.metrics.http_reqs.values.count : 0,
    rps: data.metrics.http_reqs ? data.metrics.http_reqs.values.rate : 0,
    checksFailed: data.metrics.checks ? data.metrics.checks.values.fails : 0,
    // Share of requests that failed a check (wrong status, missing answer, or timed out after 60 s).
    errorRate: data.metrics.checks ? 1 - data.metrics.checks.values.rate : 0,
    duration_ms: trend('http_req_duration'),
    ttfb_ms: trend('http_req_waiting'),
  };
  return {
    [`/results/${__ENV.OUT}.json`]: JSON.stringify(result, null, 2),
    stdout: `${__ENV.OUT}: ${result.requests} requests, ${result.rps.toFixed(1)} req/s, median ${result.duration_ms.med.toFixed(2)} ms, p95 ${result.duration_ms['p(95)'].toFixed(2)} ms, failed checks ${result.checksFailed}\n`,
  };
}
