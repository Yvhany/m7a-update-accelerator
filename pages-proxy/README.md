# Cloudflare Pages GitHub 反代

一个部署在自己域名下的 GitHub 前缀式反代，流式透传、支持 Range 续传。

```
https://<your-domain>/https://github.com/<owner>/<repo>/releases/download/<tag>/<file>
```

## 为什么是 Pages 而不是 Worker

两者运行时（workerd）、免费额度（10 万请求/天、CPU 10ms/请求）、流式响应、
Range 透传能力完全相同，代码只差入口签名。区别只在**免费子域当前能不能连通**。

某次从受限网络实测（同一台机器同一时段，各 6 次）：

| 域名 | 结果 |
|---|---|
| `*.workers.dev` | 6 次全超时，10s 无响应 ❌ |
| `*.pages.dev` | 6/6 全通，CF anycast，TLS 0.44s ✅ |

所以选了 Pages。**这个结论会随时间变化**，部署前请自己测一遍：

```powershell
curl.exe -s -o NUL -w "%{http_code} %{time_total}s`n" --max-time 10 https://your-project.pages.dev/
```

> Worker 同样支持绑自定义域名（Settings → Domains & Routes → Add Custom Domain），
> 两者绑域名的前提都是「域名 NS 托管在同一个 Cloudflare 账号下」，
> 或者用 CNAME 指向 `<project>.pages.dev` 走外部 DNS。

## 目录

```
pages-proxy/
├── _worker.js        反代本体（Advanced Mode 单文件 worker，无 import，不需要打包）
├── deploy_api.mjs    走 HTTP API 部署，不需要 wrangler
├── test_worker.mjs   本地单元测试，19 项
└── README.md
```

## 部署

### 方式一：API 脚本（推荐，可脚本化）

```powershell
$env:CF_TOKEN   = '<API token，需要 Account > Cloudflare Pages > Edit>'
$env:CF_ACCOUNT = '<Account ID>'
$env:CF_PROJECT = 'm7a-gh'          # 项目名，全局唯一，决定 <name>.pages.dev
node deploy_api.mjs
```

脚本自己会：创建项目（已存在则跳过）→ 签发上传 JWT → 查缺失 blob → 上传 →
构造 `_worker.bundle` → 创建部署。改完 `_worker.js` 重跑即可，几秒上线。

### 方式二：Dashboard 手动上传

Workers & Pages → Create → Pages → Upload assets → 把 `_worker.js` 拖进去。

> 注：Dashboard 上传能正确处理 worker bundle；但如果你**自己调 API**，
> 见下面「五个坑」第 4 条，务必传 `_worker.bundle` 而不是把 `_worker.js` 当静态文件。

## 路径暗号

公开子域会被扫到当免费下载代理刷流量。在项目 Settings → Variables and Secrets
加 `GH_PATH_TOKEN=<随机串>`，之后路径必须带上它：

```
https://<your-domain>/<token>/https://github.com/<owner>/<repo>/...
```

不带暗号一律 403，根路径 `/` 仍返回说明页、不泄露配置。

改完环境变量必须**重新部署一次**，变量才会绑定到新部署。

## 五个坑

都是踩过的：

1. **签发 JWT 的端点是 `/upload-token`，不是 `/upload`**
   访问 `/pages/projects/{p}/upload` 会静默返回项目对象，让你以为拿到了 JWT。

2. **查缺失用 `/pages/assets/check-missing`**（返回缺失的 hash 数组），
   `/pages/assets/upsert-hashes` 是收尾登记，两者别搞反。

3. **创建部署的请求体是 multipart/form-data，不是 JSON**
   字段：`manifest`(JSON 字符串)、`branch`、`commit_message`、`commit_hash`、`commit_dirty`。
   用 JSON 会得到 400 `A "manifest" field was expected`。

4. **`_worker.js` 只当静态资产上传，平台不会把它当 worker 运行**
   表现为：`/` 返回 404，`/_worker.js` 返回 500。
   必须额外用 `_worker.bundle` 字段上传**嵌套 multipart**——内层是
   `metadata`（含 `main_module`）+ 以 `main_module` 为文件名的源码
   （type `application/javascript+module`），外层把这个 multipart 的字节
   作为 `_worker.bundle` 文件传上去。

5. **在 Electron 内核的 node 下 wrangler 会读错 argv**
   报 `Unknown argument: ...cli.js`，3.x 和 4.x 都一样。
   这就是为什么仓库里给的是直接调 API 的脚本，而不是依赖 wrangler。

## 测试

```powershell
node test_worker.mjs
```

19 项：健康检查、上游 URL 拼接、Range/UA 透传、206 透传、流式（非缓冲）、
域名白名单、路径校验、方法限制、token 鉴权、HEAD 无 body、query 保留、上游异常 502。

## 实测

20 MB 分片，取一个大 release 文件前 20 MB：

| 路径 | 速度 | 耗时 |
|---|---|---|
| 经本反代（自建端点） | 7.12 / 6.37 / 5.51 MB/s | 2.95 / 3.29 / 3.80 s |
| 经公共镜像 `gh-proxy.com` | 5.42 MB/s | 3.87 s |
| Cloudflare 边缘吞吐基线 | 5.51 MB/s | 3.63 s |

Range 续传验证：`Range: bytes=1048576-1048675` → `206` +
`Content-Range: bytes 1048576-1048675/<total>` + `Accept-Ranges: bytes`。

## 排障

| 现象 | 原因 | 处理 |
|---|---|---|
| `404` 空响应 | worker 没挂上（漏了 `_worker.bundle`） | 重跑 `deploy_api.mjs` |
| `500` 空响应 | worker 代码报错，或直接访问了 `/_worker.js` | 重跑部署；该路径本不该直接访问 |
| `403 host not allowed` | 目标域名不在白名单 | 白名单只有 GitHub 系域名 |
| `403 forbidden` | 开了 token 但路径没带 | 路径前面加 `/<token>/` |
| `502 upstream fetch failed` | CF 边缘拉 GitHub 失败 | 重试 |
| 速度掉到几百 KB/s | CF 到 GitHub 路由变差 | 换公共镜像 |

## 免费额度

10 万请求/天。一次完整包下载约 1～3 个请求，每周一次更新，两个月也用不完一天的额度。
