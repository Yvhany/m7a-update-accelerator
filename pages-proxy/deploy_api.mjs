/**
 * Cloudflare Pages Direct Upload 部署脚本（走 HTTP API，不需要 wrangler）
 *
 * 用法：
 *   $env:CF_TOKEN = '...'        # 需要 Account > Cloudflare Pages > Edit
 *   $env:CF_ACCOUNT = '<account_id>'
 *   & $env:MIMO_NODE deploy_api.mjs
 *
 * 可选：CF_PROJECT 覆盖项目名（默认 m7a-gh）、CF_BRANCH 覆盖分支（默认 main）
 *
 * 流程（Cloudflare 官方 Direct Upload 三步）：
 *   1. GET  /accounts/{a}/pages/projects/{p}/upload      → 取上传用 JWT
 *   2. POST /pages/assets/upsert-hashes                  → 告知已存在的 blob，省流量
 *      POST /pages/assets/upload                          → 上传缺失的 blob（base64）
 *   3. POST /accounts/{a}/pages/projects/{p}/deployments → 用 manifest 建部署
 */

import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const TOKEN = process.env.CF_TOKEN;
const ACCOUNT = process.env.CF_ACCOUNT;
const PROJECT = process.env.CF_PROJECT || 'm7a-gh';
const BRANCH = process.env.CF_BRANCH || 'main';
const API = 'https://api.cloudflare.com/client/v4';

if (!TOKEN) { console.error('缺 CF_TOKEN 环境变量'); process.exit(1); }
if (!ACCOUNT) { console.error('缺 CF_ACCOUNT 环境变量'); process.exit(1); }

// Advanced Mode：单文件 _worker.js 替代整个 functions/ 目录
const FILES = [
  { path: '/_worker.js', file: '_worker.js', contentType: 'application/javascript' },
];

async function call(path, init = {}, auth = TOKEN) {
  const isForm = typeof FormData !== 'undefined' && init.body instanceof FormData;
  const headers = { Authorization: `Bearer ${auth}`, ...(init.headers || {}) };
  if (!isForm) headers['Content-Type'] = 'application/json';
  const res = await fetch(API + path, { ...init, headers });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* 非 JSON */ }
  return { ok: res.ok && json && json.success === true, status: res.status, json, text };
}

async function api(path, init = {}, auth = TOKEN) {
  const r = await call(path, init, auth);
  if (!r.ok) {
    console.error(`\n[失败] HTTP ${r.status}  ${init.method || 'GET'} ${path}`);
    console.error(r.text.slice(0, 2000));
    process.exit(1);
  }
  return r.json;
}

const entries = [];
for (const f of FILES) {
  const buf = await readFile(resolve(f.file));
  const hash = createHash('sha256').update(buf).digest('hex');
  entries.push({ ...f, buf, hash });
  console.log(`  文件 ${f.path.padEnd(16)} ${String(buf.length).padStart(6)} B  ${hash.slice(0, 16)}…`);
}

console.log(`\n项目 ${PROJECT} / 分支 ${BRANCH} / 账号 ${ACCOUNT}`);

console.log('\n[1/5] 取上传 JWT …');
const jwt = (await api(`/accounts/${ACCOUNT}/pages/projects/${PROJECT}/upload-token`)).result.jwt;
console.log('      ok');

console.log('\n[2/5] check-missing …');
let missing = entries;
const chk = await call('/pages/assets/check-missing',
  { method: 'POST', body: JSON.stringify({ hashes: entries.map((e) => e.hash) }) }, jwt);
if (chk.ok) {
  const need = new Set(chk.json.result || []);
  missing = entries.filter((e) => need.has(e.hash));
  console.log(`      已存在 ${entries.length - missing.length} 个，需上传 ${missing.length} 个`);
} else {
  console.log(`      跳过（HTTP ${chk.status} ${chk.json?.errors?.[0]?.message || ''}），全量上传`);
}

if (missing.length) {
  console.log('\n[3/5] 上传 blob …');
  const payload = missing.map((e) => ({
    key: e.hash,
    value: e.buf.toString('base64'),
    metadata: { contentType: e.contentType },
    base64: true,
  }));
  await api('/pages/assets/upload',
    { method: 'POST', body: JSON.stringify(payload) }, jwt);
  console.log('      ok');
} else {
  console.log('\n[3/5] 无需上传，跳过');
}

console.log('\n[4/5] upsert-hashes …');
const up2 = await call('/pages/assets/upsert-hashes',
  { method: 'POST', body: JSON.stringify({ hashes: entries.map((e) => e.hash) }) }, jwt);
console.log(up2.ok ? '      ok' : `      跳过（HTTP ${up2.status}，不影响本次部署）`);

console.log('\n[5/6] 构造 _worker.bundle …');
// Pages Advanced Mode：worker 通过嵌套 multipart 上传，
// 内层是 { metadata: {main_module}, <main_module>: 源码 }，外层作为 _worker.bundle 文件
const workerEntry = entries.find((e) => e.path === '/_worker.js');
const inner = new FormData();
inner.set('metadata', JSON.stringify({
  main_module: '_worker.js',
  compatibility_date: '2026-10-18',
}));
inner.set('_worker.js', new File([workerEntry.buf], '_worker.js', {
  type: 'application/javascript+module',
}));
const bundleBytes = new Uint8Array(await new Response(inner).arrayBuffer());
console.log(`      bundle ${bundleBytes.byteLength} B`);

console.log('\n[6/6] 创建部署 …');
const manifest = Object.fromEntries(entries.map((e) => [e.path, e.hash]));
const digest = createHash('sha1').update(String(Date.now())).digest('hex');
const fd = new FormData();
fd.append('manifest', JSON.stringify(manifest));
fd.append('branch', BRANCH);
fd.append('commit_message', 'deploy m7a-gh github proxy');
fd.append('commit_hash', digest);
fd.append('commit_dirty', 'true');
fd.append('_worker.bundle', new File([bundleBytes], '_worker.bundle'));
const dep = await api(`/accounts/${ACCOUNT}/pages/projects/${PROJECT}/deployments`, {
  method: 'POST',
  body: fd,
});

console.log('\n部署完成');
console.log('  id          : ' + dep.result.id);
console.log('  environment : ' + dep.result.environment);
console.log('  url         : ' + dep.result.url);
console.log('  aliases     : ' + JSON.stringify(dep.result.aliases || []));
console.log('  stages      : ' + JSON.stringify(dep.result.latest_stage || {}));
