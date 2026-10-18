# 本地 MITM 拦截代理（Windows）

把指定程序的 `github.com` 请求 302 到镜像站，其余域名原样 TCP 隧道转发。
装在独立目录，不碰软件本身的文件。

## 原理

```
程序 ──CONNECT github.com:443──► 本地代理 127.0.0.1:8899
                                      │
                                      ├─ 本地 CA 现场签发 github.com 证书，终结 TLS
                                      ├─ 读出请求路径
                                      └─ 302 → https://<镜像>/https://github.com/<原路径>
                                                    │
                                           程序跟随重定向，从镜像流式下载
```

证书要能被程序信任，所以安装时现场签发一个本地 CA，并把它拼进
`REQUESTS_CA_BUNDLE` 指向的证书底座（取软件自带的 certifi 底座 + 本机 CA）。
签完 CA 私钥立即从证书库删除，不在磁盘留离线私钥。

## 目录

```
local-proxy/
├── deploy.ps1              部署 / 自检 / 回滚
├── install.bat             双击入口（自动 UAC 提权）
└── src/
    ├── MirrorProxy.cs      拦截与转发核心
    ├── Program.cs          参数入口
    └── TrayApp.cs          托盘监控器（独立程序）
```

包里不含任何 exe，需要自己编译（Windows 自带编译器，无第三方依赖）：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# 代理
& $csc /target:exe /optimize+ /r:System.dll /out:m7a-mirror-proxy.exe `
    src\MirrorProxy.cs src\Program.cs

# 托盘
& $csc /target:winexe /optimize+ /r:System.dll /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll /out:m7a-mirror-tray.exe src\TrayApp.cs
```

## 部署

```powershell
# 自检，不改任何东西
powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -CheckOnly

# 部署
powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1

# 指定软件目录和端口
powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 `
    -AppDir 'C:\APP\March7thAssistant' -Port 8899
```

装完后还要手工做一步：软件里 设置 →「关于」→「更新源」→「下载代理」填 `127.0.0.1:8899`。
这一步故意不自动做，避免脚本擅自改软件配置。要代填加 `-SetConfig`（会先备份 config.yaml）。

## 镜像优先级

`-Mirror` 接受逗号分隔的列表，靠前的优先：

```powershell
powershell -File deploy.ps1 -Mirror "https://gh.example.com/<your-token>,https://gh-proxy.com,https://ghproxy.net"
```

代理每 3 分钟对每个站做一次探活（Range 取 64 字节），永远选第一个健康的：

```
health mirror[0] UP   https://gh.example.com/<your-token>
health mirror[2] DOWN Timeout https://ghproxy.net
```

自己搭的端点挂了会自动退到公共镜像，恢复后自动切回。

## 托盘监控

代理跑在 SYSTEM 计划任务里（session 0，没有桌面），**无法自己显示托盘图标**。
所以托盘是一个独立程序，跑在登录用户的交互式会话，只读状态、不改代理行为：

| 颜色 | 含义 |
|---|---|
| 绿 | 进程在，`127.0.0.1:8899` 在监听 |
| 黄 | 进程在但端口连不上 |
| 红 | 进程不存在 |

右键菜单：查看日志 / 打开安装目录 / 重启代理 / 开机自动启动 / 退出。双击图标打开日志。
部署脚本会注册计划任务 `M7A Tray`（登录时启动、交互式）并建桌面快捷方式。

退出托盘不影响加速。

## 验证

点软件的「检查更新」，然后看 `C:\M7A-UpdateProxy\mirror_proxy.log`：

```
tls ok protocol=Tls12
intercept GET /<owner>/<repo>/releases/download/... via https://gh.example.com/<your-token>
302 -> https://gh.example.com/<your-token>/https://github.com/<owner>/<repo>/...
CONNECT gh.example.com:443
```

## 回滚

```powershell
# 换镜像
powershell -File deploy.ps1 -Mirror "https://gh-proxy.com"

# 重签证书
powershell -File deploy.ps1 -RegenerateCert

# 完全卸载（停代理、删计划任务、清环境变量、恢复 config.yaml 备份）
powershell -File deploy.ps1 -Rollback

# 只去掉托盘
schtasks /Delete /TN "M7A Tray" /F
```

## 改动清单

脚本只做这几件事，全部可撤销：

1. 新建 `C:\M7A-UpdateProxy` 目录（exe + pfx + ca_bundle.pem + 日志）
2. 机器级环境变量 `REQUESTS_CA_BUNDLE` → `C:\M7A-UpdateProxy\ca_bundle.pem`
3. 计划任务 `M7A Update Proxy`：SYSTEM 身份、开机启动
4. 计划任务 `M7A Tray`：登录时启动托盘
5. 公共桌面快捷方式 `M7A 更新加速状态.lnk`
6. 仅 `-SetConfig` 时改 `config.yaml` 的 `update_download_proxy` 一行（含备份）

不动防火墙、网络适配器、系统证书库，不替换软件任何文件。

## 限制

只对「填了下载代理的那个程序里的 `github.com` 请求」生效。
浏览器开 github.com 网页、`git clone` 都不走这条路。

需要 Windows 10/11 或 Server 2016+（`New-SelfSignedCertificate` 与 .NET 4.x）。
