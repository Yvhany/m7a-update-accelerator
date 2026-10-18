/**
 * GitHub 反代 · Cloudflare Pages (Advanced Mode, _worker.js)
 *
 * 用法（前缀式，与 gh-proxy.com 等公共镜像一致）：
 *   https://m7a-gh.pages.dev/https://github.com/owner/repo/releases/download/tag/file.7z
 *   https://m7a-gh.pages.dev/https://raw.githubusercontent.com/owner/repo/branch/path
 *
 * 设计约束：
 *   · 必须流式透传（await fetch → 直接 return resp.body），绝不缓冲整个响应，
 *     否则 454MB 的文件会撑爆内存上限
 *   · 透传 Range / If-* 头，保证更新器的断点续传（206）不失效
 *   · 只允许 GitHub 域名的白名单路径，避免变成任意站点的开放代理
 *
 * 可选：在 Pages 项目的 Settings → Variables and Secrets 里设置 GH_PATH_TOKEN=任意串，
 *       之后请求路径必须变成 /<token>/https://github.com/...
 *       用来防止陌生人拿你的子域当公共代理刷流量。
 */

const ALLOWED_HOSTS = [
  'github.com',
  'api.github.com',
  'codeload.github.com',
  'raw.githubusercontent.com',
  'objects.githubusercontent.com',
  'gist.githubusercontent.com',
  'release-assets.githubusercontent.com',
];

const PASSTHROUGH_REQUEST_HEADERS = [
  'range',
  'if-range',
  'if-none-match',
  'if-modified-since',
  'user-agent',
  'accept',
  'content-type',
];

const PASSTHROUGH_RESPONSE_HEADERS = [
  'content-type',
  'content-length',
  'content-range',
  'accept-ranges',
  'etag',
  'last-modified',
  'content-disposition',
];

const INFO_HTML = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>GitHub 反代 · Pages</title>
<style>
:root{color-scheme:light dark}
body{margin:0;min-height:100vh;display:grid;place-items:center;
font:15px/1.7 -apple-system,"PingFang SC","Microsoft YaHei",sans-serif;
background:#f6f7f9;color:#1b1f24;padding:32px}
@media(prefers-color-scheme:dark){body{background:#14171a;color:#e6e9ec}}
main{max-width:680px;width:100%}
h1{font-size:20px;font-weight:600;margin:0 0 4px}
p.sub{margin:0 0 24px;opacity:.6;font-size:13px}
.card{border:1px solid rgba(128,128,128,.28);border-radius:10px;
padding:16px 18px;margin-bottom:14px;background:rgba(128,128,128,.05)}
.label{font-size:12px;letter-spacing:.04em;text-transform:uppercase;opacity:.55;margin-bottom:8px}
code,pre{font-family:ui-monospace,"SF Mono",Consolas,monospace;font-size:12.5px}
pre{margin:0;white-space:pre-wrap;word-break:break-all}
ul{margin:0;padding-left:20px}li{margin:2px 0}
.ok{color:#1a7f37}@media(prefers-color-scheme:dark){.ok{color:#4ac26b}}
</style></head><body><main>
<h1>GitHub 反代</h1>
<p class="sub">Cloudflare Pages · Advanced Mode · 流式透传 · Range 续传可用</p>
<div class="card"><div class="label">用法一 · 前缀式（可换任意 GitHub 域名）</div>
<pre>/&lt;暗号&gt;/https://github.com/moesnow/March7thAssistant/releases/download/&lt;tag&gt;/&lt;file&gt;</pre></div>
<div class="card"><div class="label">用法二 · 透明式（固定映射到 github.com）</div>
<pre>/&lt;暗号&gt;/moesnow/March7thAssistant/releases/download/&lt;tag&gt;/&lt;file&gt;
git clone https://&lt;本项目域名&gt;/&lt;暗号&gt;/moesnow/March7thAssistant.git</pre></div>
<div class="card"><div class="label">允许的域名</div><ul>
<li>github.com</li><li>api.github.com</li><li>codeload.github.com</li>
<li>raw.githubusercontent.com</li><li>objects.githubusercontent.com</li>
<li>gist.githubusercontent.com</li><li>release-assets.githubusercontent.com</li>
</ul></div>
<div class="card"><div class="label">状态</div>
<p style="margin:0"><span class="ok">&#9679;</span> 运行中。仅支持 GET / HEAD，仅限 GitHub 域名。</p></div>
</main></body></html>`;

function resolveUpstream(pathname, search, token) {
  let p = pathname.replace(/^\/+/, '');

  if (token) {
    if (!p.startsWith(token + '/')) return { error: 'forbidden' };
    p = p.slice(token.length + 1);
  }

  // 前缀式：/<token>/https://github.com/owner/repo/...
  const m = /^https?:\/\/([^/]*)(\/.*)?$/i.exec(p);
  if (m) {
    const host = (m[1] || '').toLowerCase();
    if (!ALLOWED_HOSTS.includes(host)) return { error: 'bad-host' };
    return { url: 'https://' + host + (m[2] || '/') + (search || '') };
  }

  // 透明式：/<token>/owner/repo/... → github.com/owner/repo/...
  if (p.length === 0) return { error: 'bad-path' };
  return { url: 'https://github.com/' + p + (search || '') };
}

async function handle(request, env) {
  const url = new URL(request.url);

  if (url.pathname === '/' || url.pathname === '') {
    return new Response(INFO_HTML, {
      headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' },
    });
  }

  // git 的 smart HTTP 第二步是 POST .../git-upload-pack，其余 POST 一律拒绝
  const isGitRpc = /\/(git-upload-pack|git-receive-pack)$/.test(url.pathname);
  if (request.method === 'POST') {
    if (!isGitRpc) return new Response('method not allowed', { status: 405 });
  } else if (request.method !== 'GET' && request.method !== 'HEAD') {
    return new Response('method not allowed', { status: 405 });
  }

  const token = (env && env.GH_PATH_TOKEN) ? String(env.GH_PATH_TOKEN) : '';
  const target = resolveUpstream(url.pathname, url.search, token);
  if (target.error === 'forbidden') return new Response('forbidden', { status: 403 });
  if (target.error === 'bad-host') {
    return new Response('host not allowed: only GitHub hosts are proxied', { status: 403 });
  }
  if (target.error) {
    return new Response('bad path: expect /<token>/<owner>/<repo>/... or /<token>/https://github.com/...', { status: 400 });
  }

  const reqHeaders = new Headers();
  for (const name of PASSTHROUGH_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value) reqHeaders.set(name, value);
  }

  let upstream;
  try {
    const init = {
      method: request.method,
      headers: reqHeaders,
      redirect: 'follow',
    };
    if (request.method === 'POST') init.body = request.body;
    upstream = await fetch(target.url, init);
  } catch (err) {
    return new Response('upstream fetch failed: ' + (err && err.message ? err.message : 'unknown'),
      { status: 502 });
  }

  const outHeaders = new Headers();
  for (const name of PASSTHROUGH_RESPONSE_HEADERS) {
    const value = upstream.headers.get(name);
    if (value) outHeaders.set(name, value);
  }
  outHeaders.set('access-control-allow-origin', '*');
  outHeaders.set('cache-control', 'no-store');

  return new Response(request.method === 'HEAD' ? null : upstream.body, {
    status: upstream.status,
    statusText: upstream.statusText,
    headers: outHeaders,
  });
}

export default {
  async fetch(request, env) {
    return handle(request, env);
  },
};
