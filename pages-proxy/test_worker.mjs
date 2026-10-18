import worker from './_worker.js';

let passed = 0, failed = 0;
function ok(name, cond, extra) {
  if (cond) { passed++; console.log('  PASS  ' + name); }
  else { failed++; console.log('  FAIL  ' + name + (extra ? '  -> ' + extra : '')); }
}

// 打桩 fetch，记录上游请求
let seen = null;
globalThis.fetch = async (url, init) => {
  seen = { url, init };
  const body = new ReadableStream({
    start(c) { c.enqueue(new TextEncoder().encode('x'.repeat(1024))); c.close(); }
  });
  return new Response(body, {
    status: 206,
    headers: { 'content-type': 'application/octet-stream', 'content-range': 'bytes 0-1023/454000000', 'content-length': '1024' }
  });
};

const BASE = 'https://m7a-gh.pages.dev';
const call = (req, env = {}) => worker.fetch(req, env);

// 1 根路径返回说明页
let r = await call(new Request(BASE + '/'));
ok('GET / 返回 200', r.status === 200);
ok('GET / 返回 HTML', (r.headers.get('content-type') || '').includes('text/html'));
ok('GET / 含说明文字', (await r.text()).includes('GitHub 反代'));

// 2 正常代理 + Range 透传
seen = null;
r = await call(new Request(
  BASE + '/https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.16/March7thAssistant_full.7z',
  { headers: { range: 'bytes=1048576-', 'user-agent': 'python-requests/2.31' } }));
ok('上游 URL 正确', seen && seen.url === 'https://github.com/moesnow/March7thAssistant/releases/download/v2026.10.16/March7thAssistant_full.7z', seen && seen.url);
ok('Range 被透传', seen && seen.init.headers.get('range') === 'bytes=1048576-');
ok('UA 被透传', seen && seen.init.headers.get('user-agent').includes('python-requests'));
ok('状态码 206 透传', r.status === 206);
ok('content-range 透传', r.headers.get('content-range') === 'bytes 0-1023/454000000');
ok('返回的是流不是缓冲', r.body !== null && typeof r.body.getReader === 'function');
const n = (await r.arrayBuffer()).byteLength;
ok('响应体完整', n === 1024, 'got ' + n);

// 3 只允许 GitHub 域名
r = await call(new Request(BASE + '/https://evil.example.com/a'));
ok('非 GitHub 域名 403', r.status === 403);

// 4 透明式：/owner/repo/... → github.com
seen = null;
r = await call(new Request(BASE + '/moesnow/repo/releases/download/v1/x.7z'));
ok('透明式映射到 github.com', seen && seen.url === 'https://github.com/moesnow/repo/releases/download/v1/x.7z', seen && seen.url);

// 5 透明式 + query（git 的 info/refs）
seen = null;
r = await call(new Request(BASE + '/a/b.git/info/refs?service=git-upload-pack'));
ok('git info/refs 透传', seen && seen.url === 'https://github.com/a/b.git/info/refs?service=git-upload-pack', seen && seen.url);

// 6 git 的 POST 放行且转发 body
seen = null;
r = await call(new Request(BASE + '/a/b.git/git-upload-pack', {
  method: 'POST',
  headers: { 'content-type': 'application/x-git-upload-pack-request' },
  body: 'pkt',
}));
ok('POST git-upload-pack 放行', r.status === 206 && seen && seen.url === 'https://github.com/a/b.git/git-upload-pack', seen && seen.url);
ok('POST content-type 透传', seen && seen.init.headers.get('content-type') === 'application/x-git-upload-pack-request');
ok('POST body 被转发', seen && seen.init.body !== undefined && seen.init.body !== null);

// 7 其他 POST 仍然拒绝
r = await call(new Request(BASE + '/a/b/raw/main/x', { method: 'POST', body: 'x' }));
ok('POST 非 git 路径 405', r.status === 405);

// 8 透明式也要带暗号
r = await call(new Request(BASE + '/moesnow/repo/x.7z'), { GH_PATH_TOKEN: 's3cret' });
ok('透明式缺暗号 403', r.status === 403);
seen = null;
r = await call(new Request(BASE + '/s3cret/moesnow/repo/x.7z'), { GH_PATH_TOKEN: 's3cret' });
ok('透明式带暗号放行', seen && seen.url === 'https://github.com/moesnow/repo/x.7z', seen && seen.url);

// 6 白名单内其他域名
seen = null;
r = await call(new Request(BASE + '/https://raw.githubusercontent.com/o/r/main/x.py'));
ok('raw.githubusercontent 允许', r.status === 206 && seen.url.startsWith('https://raw.githubusercontent.com/'));

// 7 token 模式
seen = null;
r = await call(new Request(BASE + '/https://github.com/o/r/x'), { GH_PATH_TOKEN: 's3cret' });
ok('token 缺失 403', r.status === 403);
r = await call(new Request(BASE + '/s3cret/https://github.com/o/r/x'), { GH_PATH_TOKEN: 's3cret' });
ok('token 正确放行', r.status === 206 && seen.url === 'https://github.com/o/r/x', seen && seen.url);

// 8 HEAD 不返回 body
r = await call(new Request(BASE + '/https://github.com/o/r/x', { method: 'HEAD' }));
ok('HEAD 无 body', r.body === null);

// 9 query string 保留
seen = null;
r = await call(new Request(BASE + '/https://api.github.com/repos/o/r/releases/latest?foo=1'));
ok('query 保留', seen && seen.url === 'https://api.github.com/repos/o/r/releases/latest?foo=1', seen && seen.url);

// 10 上游异常转 502
const goodFetch = globalThis.fetch;
globalThis.fetch = async () => { throw new Error('boom'); };
r = await call(new Request(BASE + '/https://github.com/o/r/x'));
ok('上游异常 502', r.status === 502);
globalThis.fetch = goodFetch;

console.log('\n结果: ' + passed + ' 通过, ' + failed + ' 失败');
process.exit(failed === 0 ? 0 : 1);
