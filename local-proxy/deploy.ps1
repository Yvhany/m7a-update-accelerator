<#
  March7th Assistant 更新加速 · 部署 / 回滚 / 自检
  以管理员身份运行（或双击 install.bat）：
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -CheckOnly
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1 -Rollback

  设计要点：
    · 默认不改软件配置。装完后由使用者自己在
      设置 →「关于」→「更新源」展开 →「下载代理」里填 127.0.0.1:8899
      需要脚本代填时加 -SetConfig
    · 证书每台机器现场签发，包里不含任何私钥或预置证书；
      CA 私钥用完即从证书库删除，不落盘
    · 代理装在独立目录（默认 C:\M7A-UpdateProxy），软件更新只覆盖自身目录，动不到它
#>
param(
    [string]$AppDir = 'C:\APP\March7thAssistant',
    [string]$InstallDir = 'C:\M7A-UpdateProxy',
    [int]$Port = 8899,
    # 多个镜像用逗号分隔，靠前的优先。可以把自己搭的 Cloudflare Pages 反代放第一个，
    # 例如 'https://gh.example.com/<your-token>,https://gh-proxy.com,https://ghproxy.net'
    [string]$Mirror = 'https://gh-proxy.com,https://ghproxy.net',
    [string]$PfxPassword = 'm7a-mirror',
    [switch]$Rollback,
    [switch]$CheckOnly,
    [switch]$SetConfig,
    [switch]$RegenerateCert
)

$ErrorActionPreference = 'Stop'

$pkg          = Split-Path -Parent $MyInvocation.MyCommand.Path
$exeName      = 'm7a-mirror-proxy.exe'
$exePath      = Join-Path $InstallDir $exeName
$pfxPath      = Join-Path $InstallDir 'github.com.pfx'
$bundlePath   = Join-Path $InstallDir 'ca_bundle.pem'
$logPath      = Join-Path $InstallDir 'mirror_proxy.log'
$configPath   = Join-Path $AppDir 'config.yaml'
$certifiPath  = Join-Path $AppDir 'libraries\certifi\cacert.pem'
$taskName     = 'M7A Update Proxy'
$envName      = 'REQUESTS_CA_BUNDLE'
$caSubject    = 'CN=MiMo LAN Update CA'
$leafSubject  = 'CN=github.com'
$sanList      = @('github.com', '*.github.com', 'api.github.com', 'codeload.github.com',
                  'githubusercontent.com', '*.githubusercontent.com',
                  'objects.githubusercontent.com', '*.githubassets.com')

if (-not $CheckOnly) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw '需要管理员权限：请双击 install.bat，或用管理员身份打开 PowerShell 再运行本脚本' }
}
function Say($message) { Write-Host $message }

function ConvertTo-Pem([byte[]]$raw) {
    return "-----BEGIN CERTIFICATE-----`n" +
           [Convert]::ToBase64String($raw, 'InsertLineBreaks') +
           "`n-----END CERTIFICATE-----`n"
}

function Get-PublicRootCaPem {
    $sb = New-Object System.Text.StringBuilder
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
    $store.Open('ReadOnly')
    try {
        foreach ($c in $store.Certificates) { [void]$sb.Append((ConvertTo-Pem $c.RawData)) }
    } finally { $store.Close() }
    return $sb.ToString()
}

function New-MachineCert {
    Say '[证书] 生成本机专用 CA（私钥不可导出）...'
    $ca = New-SelfSignedCertificate `
        -Subject $caSubject `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -KeyUsage CertSign, CRLSign -KeyUsageProperty Sign `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')

    Say '[证书] 签发 github.com 服务器证书（含 AKI/SAN，OpenSSL 3 必需）...'
    $leaf = New-SelfSignedCertificate `
        -Subject $leafSubject -DnsName $sanList `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment -KeyUsageProperty Sign `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -Signer $ca -NotAfter (Get-Date).AddYears(9) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')

    $sec = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $leaf -FilePath $pfxPath -Password $sec -Force | Out-Null

    if (Test-Path $certifiPath) {
        Say "[证书] 用软件自带 certifi 作为公共 CA 底座: $certifiPath"
        $publicPem = [IO.File]::ReadAllText($certifiPath)
    } else {
        Say '[证书] 未找到 certifi，改用系统根证书库作为公共 CA 底座'
        $publicPem = Get-PublicRootCaPem
    }
    $caPem = ConvertTo-Pem $ca.RawData
    [IO.File]::WriteAllText($bundlePath, ($publicPem.TrimEnd() + "`n" + $caPem),
        (New-Object System.Text.UTF8Encoding($false)))

    $leafThumb = $leaf.Thumbprint
    $caThumb   = $ca.Thumbprint
    Remove-Item "Cert:\LocalMachine\My\$leafThumb" -Force -ErrorAction SilentlyContinue
    Remove-Item "Cert:\LocalMachine\My\$caThumb" -Force -ErrorAction SilentlyContinue
    Say '[证书] 已从证书库删除临时证书，私钥不落盘'

    $check = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $PfxPassword)
    if (-not $check.HasPrivateKey) { throw '导出的 pfx 不含私钥，证书生成失败' }
    Say "[证书] pfx 就绪（$((Get-Item $pfxPath).Length) 字节），bundle 就绪（$((Get-Item $bundlePath).Length) 字节）"
}

function Stop-Proxy {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-Process -Name 'm7a-mirror-proxy' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ── 回滚 ─────────────────────────────────────────────
if ($Rollback) {
    Say '[回滚] 停止代理'
    Stop-Proxy
    Say '[回滚] 删除计划任务'
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Say '[回滚] 清除机器级环境变量'
    [Environment]::SetEnvironmentVariable($envName, $null, 'Machine')
    $backup = Get-ChildItem $AppDir -Filter 'config.yaml.bak-updateproxy-*' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($backup) {
        Copy-Item $backup.FullName $configPath -Force
        Say "  已恢复配置: $($backup.Name)"
    } else {
        Say '  config.yaml 从未被本脚本改过（或找不到备份），无需恢复'
    }
    Say "[回滚] 完成。$InstallDir 已保留（含本机证书），确认无用后可手动删除"
    return
}

# ── 自检 ─────────────────────────────────────────────
if ($CheckOnly) {
    Say "AppDir       = $AppDir"
    Say "  config.yaml  = $(Test-Path $configPath)"
    Say "  certifi      = $(Test-Path $certifiPath)"
    Say "InstallDir   = $InstallDir"
    Say "  exe          = $(Test-Path $exePath)"
    Say "  pfx          = $(Test-Path $pfxPath)（不存在则安装时现场签发）"
    Say "  ca_bundle    = $(Test-Path $bundlePath)"
    Say "Port         = $Port"
    Say "Mirror       = $($Mirror.Split(',')[0])  (优先)"
    $extraMirrors = $Mirror.Split(',') | Select-Object -Skip 1
    foreach ($m in $extraMirrors) { Say "               备用 $($m.Trim())" }
    Say "Task         = $taskName (SYSTEM / 开机启动)"
    Say "EnvVar       = $envName (Machine) -> $bundlePath"
    Say "SetConfig    = $($SetConfig.IsPresent)（默认 $false，装完由使用者自己在界面里填代理）"
    Say '检查结束，未做任何改动'
    return
}

if (-not (Test-Path (Join-Path $pkg $exeName))) { throw "部署包缺少 $exeName" }
if (-not (Test-Path $AppDir)) { throw "找不到软件目录 $AppDir，请用 -AppDir 指定实际路径" }

# 0. 先停掉正在运行的代理，否则 exe 被占用无法覆盖
Stop-Proxy
Start-Sleep -Seconds 1

# 1. 安装代理文件
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $pkg $exeName) $exePath -Force
Say "[1/5] 代理已安装到 $InstallDir"

# 2. 证书（本机现场签发；已存在则复用）
if ((Test-Path $pfxPath) -and (Test-Path $bundlePath) -and -not $RegenerateCert) {
    Say '[2/5] 复用已有本机证书（需要重签时加 -RegenerateCert）'
} else {
    if ($RegenerateCert) {
        Remove-Item $pfxPath, $bundlePath -Force -ErrorAction SilentlyContinue
    }
    New-MachineCert
    Say '[2/5] 本机证书已签发'
}

# 3. 机器级环境变量（软件以登录用户身份运行，用户级环境变量不一定生效）
[Environment]::SetEnvironmentVariable($envName, $bundlePath, 'Machine')
Say "[3/5] 已设置机器级环境变量 $envName"

# 4. 计划任务：SYSTEM 身份、开机启动，不依赖谁登录
$argLine = '--port {0} --pfx "{1}" --password {2} --mirror "{3}" --log "{4}"' -f `
    $Port, $pfxPath, $PfxPassword, $Mirror, $logPath
$action    = New-ScheduledTaskAction -Execute $exePath -Argument $argLine -WorkingDirectory $InstallDir
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Say "[4/5] 已注册计划任务「$taskName」(SYSTEM, 开机启动)"

# 5. 启动并检查端口
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3
$listening = Test-NetConnection -ComputerName 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue
if ($listening) {
    Say "[5/5] 代理已在 127.0.0.1:$Port 监听"
} else {
    Say "[5/5] 警告：端口 $Port 未监听，请查看 $logPath"
}

# 6. 托盘监控程序（跑在登录用户的会话里，只读状态，不影响代理）
$trayName = 'm7a-mirror-tray.exe'
$traySrc  = Join-Path $pkg $trayName
if (Test-Path $traySrc) {
    Get-Process -Name 'm7a-mirror-tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Copy-Item $traySrc (Join-Path $InstallDir $trayName) -Force
    Say "[6/6] 托盘程序已安装：$(Join-Path $InstallDir $trayName)"

    # 桌面快捷方式，方便直接双击（代理是 SYSTEM 身份，托盘必须由登录用户自己启动）
    try {
        $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
        if ($desktop) {
            $ws = New-Object -ComObject WScript.Shell
            $lnk = $ws.CreateShortcut((Join-Path $desktop 'M7A 更新加速状态.lnk'))
            $lnk.TargetPath = Join-Path $InstallDir $trayName
            $lnk.WorkingDirectory = $InstallDir
            $lnk.Description = '查看本地更新加速代理的运行状态'
            $lnk.Save()
            Say "        桌面快捷方式：M7A 更新加速状态"
        }
    } catch {
        Say "        桌面快捷方式创建失败（不影响使用）：$($_.Exception.Message)"
    }
}

# 7. 配置（默认为手动，可选代填）
if ($SetConfig) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item $configPath "$configPath.bak-updateproxy-$stamp" -Force
    $lines   = Get-Content $configPath -Encoding UTF8
    $newLine = "update_download_proxy: 'http://127.0.0.1:$Port'"
    $found   = $false
    $output  = foreach ($line in $lines) {
        if ($line -match '^\s*update_download_proxy\s*:') { $found = $true; $newLine } else { $line }
    }
    if (-not $found) { $output = @($output) + $newLine }
    [IO.File]::WriteAllLines($configPath, [string[]]$output, (New-Object System.Text.UTF8Encoding($false)))
    Say "[配置] 已代填 update_download_proxy（备份: $stamp）"
}

Say ''
Say '部署完成。剩下两步需要使用者手工做（一次性）：'
Say '  1) 启动托盘监控：双击桌面「M7A 更新加速状态」（或 C:\M7A-UpdateProxy\m7a-mirror-tray.exe）'
Say '     桌面上会出现绿点图标，鼠标悬停能看到 "运行中"。第一次启动后可在右键菜单里勾「开机自动启动」。'
Say ''
Say '  2) 打开三月七小助手 → 设置 →「关于」分栏 →「更新源」卡片展开'
Say "     →「下载代理」点「修改」→ 填 127.0.0.1:$Port → 确定"
Say ''
Say "之后软件更新下载会走本机代理：github.com 被 302 到镜像（按顺序优先）："
$mirrorList = $Mirror.Split(',')
for ($i = 0; $i -lt $mirrorList.Count; $i++) {
    $tag = if ($i -eq 0) { '优先' } else { "备用$i" }
    Say ("  [{0}] {1}" -f $tag, $mirrorList[$i].Trim())
}
Say '代理每 3 分钟探一次活，优先那个挂了会自动退到下一个，恢复后自动切回。'
Say "验证：C:\M7A-UpdateProxy\mirror_proxy.log 里出现 intercept ... via <镜像> 与 302 -> <镜像>"
Say ''
Say '  换镜像：deploy.ps1 -Mirror "https://自己的,https://gh-proxy.com"'
Say '  重签证书：deploy.ps1 -RegenerateCert'
Say '  完全回滚：deploy.ps1 -Rollback'
