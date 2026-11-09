<#
============================================================================
 风灵月影宗藏宝阁 · 发布到 Gitee Releases（全自动）

 双击 release.bat 就行，中途基本不用按键（只有第一次要粘一次令牌，
 之后自动从 gitee.token.txt 读）。它会做完这四件事：
   1. 读 .pro\version.txt 的版本号，在 .releases\<版本号>\ 下找安装包
   2. 读 FlingTrainerX\Assets\changelog.txt 当发行版描述
   3. 建发行版（tag = v<版本号>）；已有同名发行版就把描述更新成最新的
   4. 把安装包作为附件传上去（带真实上传进度），附件名沿用 exe 的文件名（中文原样保留，
      不能用 .NET 的 MultipartFormDataContent，它会把中文名转成 =?utf-8?B?..?= 编码字）；
      发行版里的旧附件会先删掉，避免重跑时堆一串

 令牌：脚本同级目录的 gitee.token.txt（已被 .gitignore 忽略）。
   文件在就直接读；没有就当场要你粘贴一次，粘完自动写进这个文件，以后都不用再输。
   换令牌 = 改这个文件；删掉它 = 下次发布重新问一遍。
   文件里只认第一个非空行，前后引号 / 空格 / BOM 都会自动去掉，放心用记事本编辑。

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
$TokenFileName  = 'gitee.token.txt'                 # 私人令牌文件（脚本同级目录，已被 .gitignore 忽略；没有会当场问一次再写回来）
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
$TokenFile   = Join-Path $Root $TokenFileName
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

# ============================== 令牌 ==============================

<#
  令牌文件里只认「第一个非空行」：用记事本随手编辑、留空行、粘成带引号的都认。
  BOM 也在这里一并去掉，否则那一串字符前面会多一个看不见的 U+FEFF，请求就 401。
#>
function Read-TokenFile {
    if (-not (Test-Path $TokenFile)) { return '' }

    try {
        $text = [System.IO.File]::ReadAllText($TokenFile)
    } catch {
        Write-Warn ("读不了 {0}：{1}" -f $TokenFileName, $_.Exception.Message)
        return ''
    }

    foreach ($line in ($text -split "`r?`n")) {
        $candidate = $line.Trim().Trim([char]0xFEFF).Trim()
        if ($candidate -eq '') { continue }

        # 允许写成 token="xxx" / 带引号的形式
        $candidate = $candidate.Trim('"').Trim("'").Trim()
        if ($candidate -ne '') { return $candidate }
    }

    return ''
}

<# 只露头尾，日志里不至于把令牌整串打出来 #>
function Hide-Token {
    param([string]$Token)
    if ($Token.Length -le 10) { return ('*' * $Token.Length) }
    return ('{0}…{1}' -f $Token.Substring(0, 4), $Token.Substring($Token.Length - 4))
}

<# 把 Read-Host -AsSecureString 拿到的密文还原成明文 #>
function ConvertFrom-SecureText {
    param([System.Security.SecureString]$Secure)

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

<#
  取令牌：先读 gitee.token.txt；没有（或内容是空的）就当场问一次，
  拿到后写回文件，下次发布直接读。
#>
function Get-GiteeToken {
    $saved = Read-TokenFile
    if ($saved -ne '') {
        Write-Info ("令牌 {0}（取自 {1}）" -f (Hide-Token $saved), $TokenFileName)
        return $saved
    }

    if (Test-Path $TokenFile) {
        Write-Warn ("{0} 里没有内容，当作没有令牌处理" -f $TokenFileName)
    }

    Write-Host ''
    Write-Host '  首次发布需要 Gitee 私人令牌，只需要输这一次' -ForegroundColor Yellow
    Write-Host '    获取：https://gitee.com/profile/personal_access_tokens' -ForegroundColor DarkGray
    Write-Host '    新建时勾选 projects 权限，复制生成的那串字符' -ForegroundColor DarkGray
    Write-Host ''

    $token = ''
    while ([string]::IsNullOrWhiteSpace($token)) {
        try {
            # 不回显，免得令牌留在屏幕上被截到
            $secure = Read-Host '  粘贴令牌（输入不回显，回车确认）' -AsSecureString
            $token = ConvertFrom-SecureText $secure
        } catch {
            # 少数宿主不支持 SecureString 输入，退回明文读
            $token = Read-Host '  粘贴令牌（回车确认）'
        }

        if ([string]::IsNullOrWhiteSpace($token)) { Write-Warn '没读到内容，再试一次（Ctrl+C 可退出）' }
    }

    $token = $token.Trim().Trim('"').Trim("'").Trim()

    try {
        [System.IO.File]::WriteAllText($TokenFile, $token, (New-Object System.Text.UTF8Encoding($false)))
        Write-Ok ("令牌已保存到 {0}（{1}），以后发布会直接用它" -f $TokenFile, (Hide-Token $token))
    } catch {
        Write-Warn ("令牌没能写进 {0}（{1}）；这次仍可继续，只是下次还得再输一遍" -f $TokenFile, $_.Exception.Message)
    }

    return $token
}

# ============================== 上传（带真实进度） ==============================
# 为什么整段放在 C# 里：
#   · 一次性把整个文件塞进去，70MB 要卡好几分钟毫无反馈；
#   · 而拿 PowerShell 脚本块当进度回调又会炸——回调跑在 IO 线程，那儿没有 Runspace，
#     报「此线程中没有可用于运行脚本的运行空间」。
#   所以「边读边发 + 画进度条」全放 C# 做，PowerShell 只调一行。
#
# 为什么不用 MultipartFormDataContent：
#   .NET 会把非 ASCII 文件名转成 RFC2047 编码字（filename="=?utf-8?B?6aOO...?="），
#   而 Gitee 不还原它，直接把这一长串当成真实文件名存下来。结果附件名变成
#   =?utf-8?B?6aOO54G15pyI5b2x5a6X6JeP5a6d6ZiBLTEuMC4xLmV4ZQ==?=
#   ——后缀 .exe 被吃掉，客户端 PickAsset 认不出安装包，退而去下源码 zip。
#   这里手写 multipart 头和尾，文件名按 UTF-8 原样写，Gitee 就能存成中文名。
if (-not ('GiteeUpload.Uploader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
            string boundary = "----FlingTrainerX" + Guid.NewGuid().ToString("N");

            // 文件名直接以 UTF-8 写进头部，不做任何编码转换
            byte[] head = Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"file\"; filename=\"" + fileName + "\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n");
            byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            using (var stream = File.OpenRead(filePath))
            using (var content = new ProgressContent(stream, head, tail, total, start))
            using (var client = new HttpClient())
            {
                content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("User-Agent", "FlingTrainerX-release");

                var response = client.PostAsync(url, content).GetAwaiter().GetResult();
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
        private readonly byte[] _head;
        private readonly byte[] _tail;
        private readonly long _fileLength;
        private readonly DateTime _start;
        private int _lastPct = -1;

        public ProgressContent(Stream source, byte[] head, byte[] tail, long fileLength, DateTime start)
        {
            _source = source;
            _head = head;
            _tail = tail;
            _fileLength = fileLength;
            _start = start;
            Headers.ContentLength = head.Length + fileLength + tail.Length;
        }

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            await target.WriteAsync(_head, 0, _head.Length).ConfigureAwait(false);

            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                sent += read;
                int pct = _fileLength > 0 ? (int)(sent * 100 / _fileLength) : 0;
                if (pct != _lastPct) { _lastPct = pct; Draw(pct, sent); }
            }

            await target.WriteAsync(_tail, 0, _tail.Length).ConfigureAwait(false);
        }

        private void Draw(int pct, long sent)
        {
            const int width = 40;
            int done = pct * width / 100;
            string bar = new string('#', done) + new string('-', width - done);
            string line = string.Format(
                "        {0} {1,3}%  uploading     {2:mm\\:ss}   {3,6:N1} / {4,6:N1} MB",
                bar, pct, DateTime.Now - _start, sent / 1048576.0, _fileLength / 1048576.0);
            Console.Write("\r" + line.PadRight(96));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _head.Length + _fileLength + _tail.Length;
            return true;
        }
    }
}
'@ -ReferencedAssemblies 'System.Net.Http' -ErrorAction Stop
}

# ============================== 主流程 ==============================
Write-Banner

# --- 0. 令牌 ---
# 放在最前面：缺令牌就直接问，别等读完版本、传了一半才发现认证过不去
$GiteeToken = Get-GiteeToken

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
# 开头在 Get-GiteeToken 里已经取好了（读 gitee.token.txt，没有就当场问一次并写回去），
# 这里只做最后一道保险：真的空着就别发请求
if ([string]::IsNullOrWhiteSpace($GiteeToken)) {
    Fail '没有 Gitee 私人令牌' ("把令牌写进 {0}，或删掉这个文件让脚本重新问一次`n获取地址：https://gitee.com/profile/personal_access_tokens（要勾 projects 权限）" -f $TokenFile)
}

$tag        = $TagPrefix + $version
$name       = "$SoftwareName $version"
$tokenParam = [Uri]::EscapeDataString($GiteeToken)
$headers    = @{ 'User-Agent' = 'FlingTrainerX-release' }

# --- 3. 建 / 更新发行版 ---
Write-Step '创建或更新发行版'

$existing = $null
try {
    $found = Invoke-RestMethod -Method Get -Uri ("{0}/releases/tags/{1}?access_token={2}" -f $ApiBase, $tag, $tokenParam) `
                               -Headers $headers -TimeoutSec 30

    # Gitee 在「这个 tag 下还没有发行版」时返回的是 HTTP 200 + 字面量 null，
    # 而 Windows PowerShell 5.1 会把它反序列化成字符串 "null"（是真的），
    # 直接 if ($found) 会走进「已有发行版」分支，接着拿不到 id，
    # 就拼出 /releases/ （末尾没有 id）→ PATCH 报 405、DELETE 报 404。
    # 所以这里只认「带纯数字 id 的对象」。
    $foundId = if ($null -ne $found) { $found.id } else { $null }
    if ("$foundId" -match '^\d+$') { $existing = $found }
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

# 旧附件先全删掉，再传新的：
#   · 早期版本上传时文件名被 .NET 转成了 =?utf-8?B?...?=，和 package.Name 永远不相等，
#     按名字挑根本删不掉，会在页面上留一堆带问号的垃圾附件；
#   · attach_files 只列真实上传的文件（Gitee 自动生成的源码 zip/tar.gz 不在里面），
#     所以「全删」不会误伤。
#   · 每一步失败都不致命，最后以新附件是否传成功为准。
try {
    $old = Invoke-RestMethod -Method Get -Uri ("{0}/releases/{1}/attach_files?access_token={2}&per_page=100" -f $ApiBase, $releaseId, $tokenParam) `
                             -Headers $headers -TimeoutSec 30
    foreach ($asset in @($old)) {
        if ($null -eq $asset -or "$($asset.id)" -notmatch '^\d+$') { continue }
        Invoke-RestMethod -Method Delete -Uri ("{0}/releases/{1}/attach_files/{2}?access_token={3}" -f $ApiBase, $releaseId, $asset.id, $tokenParam) `
                          -Headers $headers -TimeoutSec 30 | Out-Null
        Write-Info ("已移除旧附件 {0}" -f $asset.name)
    }
} catch {
    Write-Warn ("清理旧附件时出错，继续上传：{0}" -f $_.Exception.Message)
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
