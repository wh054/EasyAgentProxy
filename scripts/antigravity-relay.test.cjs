const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { test } = require('node:test');
const path = require('node:path');

// Exercise the production embedded script, with controlled upstream failures.
const source = fs.readFileSync(path.join(__dirname, '../AppProxyHelper/AntigravityCloudCodeRelay.cs'), 'utf8')
  .split('public const string ScriptContent = """')[1].split('""";')[0]
  .replace(/^        /gm, '');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const reset = () => Object.assign(new Error('socket hang up'), { code: 'ECONNRESET' });

async function fixture(t, behavior) {
  const attempts = [];
  let server;
  vm.runInNewContext(source, {
    require(name) {
      if (name === 'http') return { createServer(handler) { server = http.createServer(handler); return server; } };
      if (name === 'fs') return { appendFile(_path, _line, callback) { callback(); } };
      if (name === 'https') return { request(options, respond) {
        const req = new EventEmitter();
        req.setTimeout = () => {};
        req.destroy = () => {};
        req.end = body => {
          attempts.push({ options, body: Buffer.from(body) });
          req.emit('finish');
          setImmediate(() => behavior(req, respond, attempts.length));
        };
        return req;
      } };
      return require(name);
    },
    process: { env: { RELAY_PORT: '0', EASYPROXY_RETRY_BASE_DELAY_MS: '20' } },
    __dirname, Buffer, URL, setTimeout, clearTimeout,
  });
  if (!server.listening) await new Promise(resolve => server.once('listening', resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  function request(route = '/v1internal:streamGenerateContent?alt=sse', body = '{"prompt":"test"}') {
    return new Promise((resolve, reject) => {
      const req = http.request({ host: '127.0.0.1', port: server.address().port, path: route, method: 'POST' }, res => {
        let data = '';
        res.on('data', chunk => { data += chunk; });
        res.on('end', () => resolve({ status: res.statusCode, data }));
        res.on('error', reject);
      });
      req.on('error', reject);
      req.end(body);
    });
  }
  return { attempts, request, server };
}
function success(respond, body = 'data: done\n\n', statusCode = 200) {
  const res = new PassThrough();
  res.statusCode = statusCode;
  res.headers = { 'content-type': 'text/event-stream' };
  respond(res);
  res.end(body);
}

test('committed generation is replayed identically after reset before headers', async t => {
  const f = await fixture(t, (req, respond, n) => n === 1 ? req.emit('error', reset()) : success(respond));
  assert.equal((await f.request()).status, 200);
  assert.equal(f.attempts.length, 2);
  assert.deepEqual(f.attempts[0].body, f.attempts[1].body);
});
test('persistent reset stops after three attempts and returns 502', async t => {
  const f = await fixture(t, req => req.emit('error', reset()));
  assert.equal((await f.request()).status, 502);
  assert.equal(f.attempts.length, 3);
});
test('unknown mutation and query-string lookalikes are not replayed', async t => {
  const f = await fixture(t, req => req.emit('error', reset()));
  for (const route of ['/v1internal:deleteModels', '/v1internal:mutate?op=generateContent', '/v1internal:onboardUser']) {
    assert.equal((await f.request(route)).status, 502);
  }
  assert.equal(f.attempts.length, 3);
});
test('upstream HTTP error is preserved without replay', async t => {
  const f = await fixture(t, (_req, respond) => success(respond, 'quota', 429));
  assert.deepEqual(await f.request(), { status: 429, data: 'quota' });
  assert.equal(f.attempts.length, 1);
});
test('a partial stream is terminated without replay or appended 502 text', async t => {
  const f = await fixture(t, (_req, respond) => {
    const res = new PassThrough();
    res.statusCode = 200;
    res.headers = {};
    respond(res);
    res.write('partial');
    setTimeout(() => res.destroy(reset()), 10);
  });
  await assert.rejects(f.request());
  await pause(80);
  assert.equal(f.attempts.length, 1);
});
test('client cancellation during retry delay cancels pending replay', async t => {
  let client;
  const f = await fixture(t, req => {
    req.emit('error', reset());
    client.destroy();
  });
  client = http.request({ host: '127.0.0.1', port: f.server.address().port, path: '/v1internal:streamGenerateContent', method: 'POST' });
  client.on('error', () => {});
  client.end('test');
  await new Promise(resolve => client.once('close', resolve));
  await pause(100);
  assert.equal(f.attempts.length, 1);
});
