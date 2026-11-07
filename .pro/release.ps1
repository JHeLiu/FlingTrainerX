<#
============================================================================
 风灵月影宗藏宝阁 · 发布到 Gitee Releases（全自动）

 双击 release.bat 就行，中途不用按任何键。它会做完这四件事：
   1. 读 .pro\version.txt 的版本号，在 .releases\<版本号>\ 下找安装包
   2. 读 FlingTrainerX\Assets\changelog.txt 当发行版描述
   3. 建发行版（tag = v<版本号>）；已有同名发行版就把描述更新成最新的
   4. 把安装包作为附件传上去（带真实上传进度），同名旧附件会先删掉

 接口（Gitee OpenAPI v5，见 https://gitee.com/api/v5/swagger）：
   创建发行版  POST   /repos/{owner}/{repo}/releases                JSON
   更新发行版  PATCH  /repos/{owner}/{repo}/releases/{id}           form
   查发行版    GET    /repos/{owner}/{repo}/releases/tags/{tag}
   列附件      GET    /repos/{owner}/{repo}/releases/{id}/attach_files
   删附件      DELETE /repos/{owner}/{repo}/releases/{id}/attach_files/{file_id}
   传附件      POST   /repos/{owner}/{repo}/releases/{id}/attach_files   multipart/form-data

 重复运行是安全的：同一个版本号再跑一次，只会把描述刷新、附件换掉。
============================================================================
#>

# ------------------------- 需要改就改这里 -------------------------
$GiteeOwner     = 'misslius'                        # 仓库拥有者
$GiteeRepo      = 'FlingTrainerX'                   # 仓库名
$GiteeToken     = ''                                # 私人令牌；留空则依次尝试环境变量 GITEE_TOKEN、.pro\.gitee-token
$SoftwareName   = '风灵月影宗藏宝阁'                 # 与 publish.ps1 保持一致
$ReleaseDir     = '..\.releases'                    # 构建产物根目录（与 publish.ps1 保持一致）
$PackagePattern = '*-*.exe'                         # 在版本目录里找哪个文件当附件
$ChangelogFile  = '..\FlingTrainerX\Assets\changelog.txt'   # 发行版描述（更新内容）的来源
$TagPrefix      = 'v'                               # tag 前缀，例如 v1.2.4（程序端会自动去掉 v）
$TargetBranch   = 'master'                          # 建 tag 时挂在哪个分支上
# ------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ============================== 基础路径 ==============================
$Root        = $PSScriptRoot
$VersionFile = Join-Path $Root 'version.txt'
$TokenFile   = Join-Path $Root '.gitee-token'
$ReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $Root $ReleaseDir))
$ChangelogPath = [System.IO.Path]::GetFullPath((Join-Path $Root $ChangelogFile))
$ApiBase     = "https://gitee.com/api/v5/repos/$GiteeOwner/$GiteeRepo"
$WebBase     = "https://gitee.com/$GiteeOwner/$GiteeRepo"

$script:Clock      = [System.Diagnostics.Stopwatch]::StartNew()
$script:StepNo     = 0
$script:TotalSteps = 4

# ============================== 输出小工具 ==============================
function Write-Banner {
    Write-Host ''
    Write-Host '  ================================================' -ForegroundColor DarkGray
    Write-Host ("   {0} · 发布到 Gitee" -f $SoftwareName) -ForegroundColor White
    Write-Host '  ================================================' -ForegroundColor DarkGray
}

function Write-Step {
    param([string]$Text)
    $script:StepNo++
    Write-Host ''
    Write-Host ("  [{0}/{1}] {2}" -f $script:StepNo, $script:TotalSteps, $Text) -ForegroundColor Cyan
}

function Write-Ok   { param([string]$Text) Write-Host ("        √ {0}" -f $Text) -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host ("        · {0}" -f $Text) -ForegroundColor DarkGray }
function Write-Warn { param([string]$Text) Write-Host ("        ! {0}" -f $Text) -ForegroundColor Yellow }

function Fail {
    param([string]$Reason, [string]$Detail = '')
    Write-Host ''
    Write-Host ("  × {0}" -f $Reason) -ForegroundColor Red
    if ($Detail -ne '') { Write-Host ("{0}" -f $Detail) -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host '  按回车键关闭窗口…' -ForegroundColor Yellow
    [void](Read-Host)
    exit 1
}

function Exit-Gracefully {
    Write-Host ''
    Write-Host '  按回车键关闭窗口…' -ForegroundColor Yellow
    [void](Read-Host)
    exit 0
}

# ============================== 上传（带真实进度） ==============================
# 为什么整段放在 C# 里：
#   · MultipartFormDataContent 会一次性把整个文件塞进去，70MB 要卡好几分钟毫无反馈；
#   · 而拿 PowerShell 脚本块当进度回调又会炸——回调跑在 IO 线程，那儿没有 Runspace，
#     报「此线程中没有可用于运行脚本的运行空间」。
#   所以「边读边发 + 画进度条」全放 C# 做，PowerShell 只调一行。
if (-not ('GiteeUpload.Uploader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace GiteeUpload
{
    public static class Uploader
    {
        /// <summary>把文件以 multipart/form-data 上传到 url，返回响应正文；失败抛异常。</summary>
        public static string Upload(string url, string filePath)
        {
            long total = new FileInfo(filePath).Length;
            string fileName = Path.GetFileName(filePath);
            DateTime start = DateTime.Now;

            using (var stream = File.OpenRead(filePath))
            using (var content = new ProgressContent(stream, total, start))
            using (var form = new MultipartFormDataContent())
            using (var client = new HttpClient())
            {
                form.Add(content, "file", fileName);
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("User-Agent", "FlingTrainerX-release");

                var response = client.PostAsync(url, form).GetAwaiter().GetResult();
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.WriteLine();

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        string.Format("HTTP {0}: {1}", (int)response.StatusCode, text));
                }
                return text;
            }
        }
    }

    internal sealed class ProgressContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _total;
        private readonly DateTime _start;
        private int _lastPct = -1;

        public ProgressContent(Stream source, long total, DateTime start)
        {
            _source = source;
            _total = total;
            _start = start;
            Headers.ContentLength = total;
        }

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                sent += read;
                int pct = _total > 0 ? (int)(sent * 100 / _total) : 0;
                if (pct != _lastPct) { _lastPct = pct; Draw(pct, sent); }
            }
        }

        private void Draw(int pct, long sent)
        {
            const int width = 40;
            int done = pct * width / 100;
            string bar = new string('#', done) + new string('-', width - done);
            string line = string.Format(
                "        {0} {1,3}%  uploading     {2:mm\\:ss}   {3,6:N1} / {4,6:N1} MB",
                bar, pct, DateTime.Now - _start, sent / 1048576.0, _total / 1048576.0);
            Console.Write("\r" + line.PadRight(96));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _total;
            return true;
        }
    }
}
'@ -ReferencedAssemblies 'System.Net.Http' -ErrorAction Stop
}

# ============================== 主流程 ==============================
Write-Banner

# --- 1. 版本号 + 安装包 ---
Write-Step '读取版本与安装包'

if (-not (Test-Path $VersionFile)) {
    Fail '找不到 version.txt' '先双击 publish.bat 构建一次，再发布'
}
$version = ([System.IO.File]::ReadAllText($VersionFile)).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Fail ("version.txt 里的版本号不合法：{0}" -f $version) '应该是 x.x.x，例如 1.2.4'
}

$versionDir = Join-Path $ReleaseRoot $version
$package    = Get-ChildItem -Path $versionDir -Filter $PackagePattern -File -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -notlike '*uninst*' } |
              Sort-Object Length -Descending |
              Select-Object -First 1

if (-not $package) {
    Fail '找不到要上传的安装包' ("期望在 {0} 下找到 {1}`n先双击 publish.bat 构建 {2} 版本" -f $versionDir, $PackagePattern, $version)
}

Write-Ok ("版本 {0}" -f $version)
Write-Info ("安装包 {0}（{1:N1} MB）" -f $package.Name, ($package.Length / 1MB))

# --- 2. 更新内容 ---
Write-Step '读取更新内容'

$notes = ''
if (Test-Path $ChangelogPath) {
    $notes = [System.IO.File]::ReadAllText($ChangelogPath).Trim()
    # 有些编辑器会在开头留 BOM 字符，发上去会变成正文里的一个怪符号
    $notes = $notes.TrimStart([char]0xFEFF).Trim()
}

if ([string]::IsNullOrWhiteSpace($notes)) {
    # 没写更新内容也不至于发不出去，自动生成一段保底
    $notes = @"
### ${SoftwareName} $version

- 版本：$version
- 构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm')
- 安装包：$($package.Name)

下载下面的附件，双击即可安装。
"@
    Write-Warn ("{0} 是空的，先用自动生成的说明顶上" -f $ChangelogPath)
} else {
    Write-Ok ("描述取自 {0}（{1} 行）" -f (Split-Path $ChangelogPath -Leaf), ($notes -split "`n").Count)
}

# --- 3. 令牌 ---
if ([string]::IsNullOrWhiteSpace($GiteeToken)) { $GiteeToken = [Environment]::GetEnvironmentVariable('GITEE_TOKEN') }
if ([string]::IsNullOrWhiteSpace($GiteeToken) -and (Test-Path $TokenFile)) {
    $GiteeToken = ([System.IO.File]::ReadAllText($TokenFile)).Trim()
}
if ([string]::IsNullOrWhiteSpace($GiteeToken)) {
    Fail '没有 Gitee 私人令牌' @'
  去 https://gitee.com/profile/personal_access_tokens 新建一个，
  勾选 projects 权限，然后把令牌填到下面任意一处：
    1) 本脚本顶部的 $GiteeToken
    2) 环境变量 GITEE_TOKEN
    3) .pro\.gitee-token 文件（一行令牌，已被 .gitignore 忽略）

  注意：仓库设成公开也不能省掉令牌。公开只免掉「读」
  （程序检查更新、用户下载安装包都不用令牌），而发布要建发行版、
  传附件，属于写操作，任何仓库都要求令牌。
'@
}

$tag        = $TagPrefix + $version
$name       = "$SoftwareName $version"
$tokenParam = [Uri]::EscapeDataString($GiteeToken)
$headers    = @{ 'User-Agent' = 'FlingTrainerX-release' }

# --- 3. 建 / 更新发行版 ---
Write-Step '创建或更新发行版'

$existing = $null
try {
    $existing = Invoke-RestMethod -Method Get -Uri ("{0}/releases/tags/{1}?access_token={2}" -f $ApiBase, $tag, $tokenParam) `
                                  -Headers $headers -TimeoutSec 30
} catch {
    $code = $null
    if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    if ($code -ne 404) {
        Fail '查询 Gitee 发行版失败' ("HTTP {0}：{1}" -f $code, $_.Exception.Message)
    }
}

function New-Release {
    $payload = @{
        tag_name         = $tag
        name             = $name
        body             = $notes
        target_commitish = $TargetBranch
        prerelease       = $false
    } | ConvertTo-Json -Depth 4

    # 显式按 UTF-8 发字节，否则中文描述会变乱码
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

    try {
        return Invoke-RestMethod -Method Post -Uri ("{0}/releases?access_token={1}" -f $ApiBase, $tokenParam) `
                                 -Body $bodyBytes `
                                 -ContentType 'application/json; charset=utf-8' `
                                 -Headers $headers -TimeoutSec 60
    } catch {
        $code = $null
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $hint = ''
        if ($code -eq 401 -or $code -eq 403) { $hint = '令牌无效，或没有 projects 权限' }
        elseif ($code -eq 404) { $hint = '仓库不存在，或令牌账号没有该仓库权限' }
        elseif ($code -eq 422) { $hint = 'tag 已存在但发行版没建成，可以先去 Gitee 手动删掉同名发行版' }
        Fail '创建发行版失败' ("HTTP {0} {1}`n    {2}" -f $code, $hint, $_.Exception.Message)
    }
}

if ($existing) {
    # 已有同名 tag：把描述刷新成最新的更新内容（重跑同一版本时用得上）
    $patched = $false
    try {
        $form = @{
            access_token = $GiteeToken
            tag_name     = $tag
            name         = $name
            body         = $notes
            prerelease   = 'false'
        }
        Invoke-RestMethod -Method Patch -Uri ("{0}/releases/{1}" -f $ApiBase, $existing.id) `
                          -Body $form `
                          -ContentType 'application/x-www-form-urlencoded; charset=utf-8' `
                          -Headers $headers -TimeoutSec 60 | Out-Null
        $patched = $true
    } catch {
        Write-Warn ("更新描述失败（{0}），改为删掉重建" -f $_.Exception.Message)
    }

    if ($patched) {
        $releaseId = $existing.id
        Write-Ok ("沿用发行版 {0}（id={1}），描述已刷新" -f $tag, $releaseId)
    } else {
        try {
            Invoke-RestMethod -Method Delete -Uri ("{0}/releases/{1}?access_token={2}" -f $ApiBase, $existing.id, $tokenParam) `
                              -Headers $headers -TimeoutSec 30 | Out-Null
        } catch {
            Fail '删除旧发行版失败' $_.Exception.Message
        }
        $created = New-Release
        $releaseId = $created.id
        Write-Ok ("重建发行版 {0}（id={1}）" -f $tag, $releaseId)
    }
} else {
    $created = New-Release
    $releaseId = $created.id
    Write-Ok ("发行版已创建 {0}（id={1}）" -f $tag, $releaseId)
}

# --- 4. 上传附件 ---
Write-Step ("上传安装包（{0:N1} MB）" -f ($package.Length / 1MB))

# 同名旧附件先删掉，否则重复发布会堆一串同名文件
try {
    $old = Invoke-RestMethod -Method Get -Uri ("{0}/releases/{1}/attach_files?access_token={2}&per_page=100" -f $ApiBase, $releaseId, $tokenParam) `
                             -Headers $headers -TimeoutSec 30
    foreach ($asset in @($old)) {
        if ($null -eq $asset -or $asset.name -ne $package.Name) { continue }
        Invoke-RestMethod -Method Delete -Uri ("{0}/releases/{1}/attach_files/{2}?access_token={3}" -f $ApiBase, $releaseId, $asset.id, $tokenParam) `
                          -Headers $headers -TimeoutSec 30 | Out-Null
        Write-Info ("已移除同名旧附件 {0}" -f $asset.name)
    }
} catch {
    Write-Warn ("查/删旧附件时出错，继续上传：{0}" -f $_.Exception.Message)
}

$uploadUri = "{0}/releases/{1}/attach_files?access_token={2}" -f $ApiBase, $releaseId, $tokenParam

try {
    $text = [GiteeUpload.Uploader]::Upload($uploadUri, $package.FullName)
} catch {
    Fail '上传附件失败' ("{0}`n    如果提示 413，说明文件超过了 Gitee 的附件大小上限" -f $_.Exception.Message)
}

$asset = $text | ConvertFrom-Json
Write-Host '        √ 上传完成' -ForegroundColor Green
Write-Info ("附件：{0}" -f $asset.name)
Write-Info ("直链：{0}" -f $asset.browser_download_url)

# --- 收尾 ---
$script:Clock.Stop()
Write-Host ''
Write-Host '  ================================================' -ForegroundColor DarkGray
Write-Host '   发布完成' -ForegroundColor Green
Write-Host '  ================================================' -ForegroundColor DarkGray
Write-Host ("   发行版 : {0}/releases/tag/{1}" -f $WebBase, $tag) -ForegroundColor White
Write-Host ("   描述   : 来自 Assets\{0}" -f (Split-Path $ChangelogPath -Leaf)) -ForegroundColor DarkGray
Write-Host ("   总耗时 : {0:mm\:ss}" -f $script:Clock.Elapsed) -ForegroundColor DarkGray
Write-Host ''
Write-Host '  程序里的「检查更新」现在就能看到这个版本了。' -ForegroundColor DarkGray
Exit-Gracefully
