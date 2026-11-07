<#
============================================================================
 风灵月影宗藏宝阁 · 一键构建打包脚本
----------------------------------------------------------------------------
 双击 publish.bat 即可运行（失败会停住等你看日志）。

 流程：
   1. 读 .pro\version.txt 显示上一版本
   2. 输入新版本号（强制 x.x.x）
   3. dotnet publish 到 .releases\<版本号>\publish\
   4. NSIS 打包成 .releases\<版本号>\<软件名>-<版本号>.exe
   5. 回写 version.txt

 构建产物统一放在项目根目录的 .releases\ 下，和脚本目录分开。
 历史版本目录不会被删除，每次只新增。
============================================================================
#>

# ------------------------- 需要改就改这里 -------------------------
$SoftwareName   = '风灵月影宗藏宝阁'                              # 软件名（安装包命名 / 快捷方式 / 注册表都用它）
$Publisher      = '风灵月影宗'                                    # 发行者
$ProjectFile    = '..\FlingTrainerX\FlingTrainerX.csproj'         # 相对 .pro 目录
$TargetFramework = 'net10.0-windows'
$RuntimeId      = 'win-x64'
$AppIcon        = '..\FlingTrainerX\Assets\app.ico'               # 安装包图标 / 安装界面 Logo 来源
$ReleaseDir     = '..\.releases'                                  # 构建产物根目录（相对 .pro），下面按版本号分目录
$NsisExe        = 'D:\DevelopApps\NSIS\makensis.exe'              # makensis 路径，留空则从 PATH 找

# --- 体积相关（想更小就动这两行）---
# ReadyToRun 预编译：启动快一点，但会把 IL 再编一份原生码塞进 exe，
# 单文件从 ~181MB 涨到 ~262MB，安装包从 ~54MB 涨到 ~73MB。改成 $false 可省这 19MB。
$ReadyToRun     = $true
# NSIS 的 LZMA 字典大小（MB）。越大压得越小，但安装器解压时要占同样多的内存。
# 实测 64 → 72.8MB，128 → 72.6MB（只省 0.2MB，内存翻倍），所以 64 最划算。
$CompressDictMB = 64
# ------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ============================== 基础路径 ==============================
$Root        = $PSScriptRoot
$VersionFile = Join-Path $Root 'version.txt'
$NsiScript   = Join-Path $Root 'installer.nsi'
$NsiConfig   = Join-Path $Root 'installer-config.nsh'
$TempDir     = Join-Path $env:TEMP 'FlingTrainerX-setup'
$ProjectFull = [System.IO.Path]::GetFullPath((Join-Path $Root $ProjectFile))
$IconFull    = [System.IO.Path]::GetFullPath((Join-Path $Root $AppIcon))
$ReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $Root $ReleaseDir))

# ============================== 输出小工具 ==============================
$script:StepNo = 0
$script:TotalSteps = 3
$script:Clock = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Banner {
    Write-Host ''
    Write-Host '  ================================================' -ForegroundColor DarkGray
    Write-Host ("   {0} · 构建打包" -f $SoftwareName) -ForegroundColor White
    Write-Host '  ================================================' -ForegroundColor DarkGray
    Write-Host ''
}

function Write-Step {
    param([string]$Text)
    $script:StepNo++
    Write-Host ''
    Write-Host ("  [{0}/{1}] {2}" -f $script:StepNo, $script:TotalSteps, $Text) -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host ("        √ {0}" -f $Text) -ForegroundColor Green
}

function Write-Info {
    param([string]$Text)
    Write-Host ("        · {0}" -f $Text) -ForegroundColor DarkGray
}

function Write-Bar {
    param([string]$Label, [int]$Percent, [TimeSpan]$Elapsed)
    if ($Percent -lt 0) { $Percent = 0 }
    if ($Percent -gt 100) { $Percent = 100 }
    $width = 28
    $done  = [int][Math]::Floor($width * $Percent / 100)
    $bar   = ('#' * $done) + ('-' * ($width - $done))
    $line  = '        {0} {1,3}%  {2,-10}  {3:mm\:ss}' -f $bar, $Percent, $Label, $Elapsed
    Write-Host ("`r" + $line.PadRight(80)) -NoNewline
}

function Write-BarDone {
    param([string]$Label, [TimeSpan]$Elapsed)
    Write-Bar -Label $Label -Percent 100 -Elapsed $Elapsed
    Write-Host ''
}

function Fail {
    param([string]$Message, [string]$Detail = '')
    Write-Host ''
    Write-Host ("  × 失败：{0}" -f $Message) -ForegroundColor Red
    if ($Detail -ne '') {
        Write-Host ("    {0}" -f $Detail) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host '  按回车键关闭窗口…' -ForegroundColor Yellow
    [void](Read-Host)
    exit 1
}

# ============================== 1. 版本号 ==============================
function Read-PreviousVersion {
    if (-not (Test-Path $VersionFile)) { return $null }
    $v = [System.IO.File]::ReadAllText($VersionFile)
    if ($null -eq $v) { return $null }
    $v = $v.Trim()
    if ($v -match '^\d+\.\d+\.\d+$') { return $v }
    return $null
}

function Ask-Version {
    param([string]$Previous)
    $blank = 0
    while ($true) {
        Write-Host ''
        Write-Host '  请输入新版本号（格式 x.x.x，例如 1.2.3）' -ForegroundColor White
        Write-Host '  > ' -NoNewline -ForegroundColor Green
        $verInput = Read-Host

        if ([string]::IsNullOrWhiteSpace($verInput)) {
            # 正常双击运行时这是手滑回车；但输入被重定向（脚本/CI 里 echo 喂参数）时
            # Read-Host 读到 EOF 会一直返回空串，这里必须退出，否则死循环刷屏
            $blank++
            if ($blank -ge 5) {
                Fail '没有读到版本号' '连续 5 次输入为空。请双击 publish.bat 手动运行，或确认管道输入里带上了版本号'
            }
            Write-Host '  × 版本号不能为空' -ForegroundColor Red
            continue
        }
        $blank = 0

        $verInput = $verInput.Trim()
        if ($verInput -notmatch '^\d+\.\d+\.\d+$') {
            Write-Host '  × 格式不对，必须是 三个数字用点连接，例如 1.2.3' -ForegroundColor Red
            continue
        }

        if ($null -ne $Previous -and $verInput -eq $Previous) {
            Write-Host ("  × 和上一版本（{0}）一样，请换一个" -f $Previous) -ForegroundColor Red
            continue
        }

        Write-Host ("  将构建版本 {0}，确认继续吗？(Y/N)" -f $verInput) -ForegroundColor Yellow
        Write-Host '  > ' -NoNewline -ForegroundColor Green
        $yes = Read-Host
        if ($yes -match '^(y|yes|是|好|确认)$') { return $verInput }
        if ($yes -match '^(n|no|否|不)$') { continue }
    }
}

# ============================== 2. 外部进程 + 进度条 ==============================
function Invoke-WithProgress {
    param(
        [string]$Label,
        [string]$FilePath,
        [string]$Arguments,
        [string]$LogFile
    )

    if (Test-Path $LogFile) { Remove-Item $LogFile -Force -ErrorAction SilentlyContinue }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $Root

    $log = New-Object System.Text.StringBuilder
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$proc.Start()

    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    # 估算进度：3 秒内匀速爬到 90%，剩下的等进程真正结束
    while (-not $proc.HasExited) {
        $ratio = $timer.Elapsed.TotalSeconds / 3.0
        if ($ratio -gt 0.9) { $ratio = 0.9 }
        Write-Bar -Label $Label -Percent ([int]($ratio * 100)) -Elapsed $timer.Elapsed
        Start-Sleep -Milliseconds 120
    }

    $proc.WaitForExit()
    $stdout = $outTask.Result
    $stderr = $errTask.Result
    [void]$log.AppendLine($stdout)
    [void]$log.AppendLine($stderr)

    $exit = $proc.ExitCode
    if ($exit -eq 0) {
        Write-BarDone -Label $Label -Elapsed $timer.Elapsed
    } else {
        Write-Host ''
    }

    if ($stdout.Trim() -ne '') { Set-Content -Path $LogFile -Value $stdout -Encoding UTF8 }
    if ($stderr.Trim() -ne '') { Add-Content -Path $LogFile -Value $stderr -Encoding UTF8 }

    return @{ ExitCode = $exit; Output = ($stdout + $stderr); Log = $LogFile }
}

# ============================== 3. 写安装脚本参数 ==============================
function Write-InstallerConfig {
    param(
        [string]$Path,
        [string]$Name,
        [string]$Version,
        [string]$Publisher,
        [string]$SourceDir,
        [string]$OutputDir,
        [string]$OutputFile,
        [string]$IconPath
    )

    $lines = @(
        '; 本文件由 publish.ps1 自动生成，每次构建都会覆盖，不要手改'
        ('!define APP_NAME "{0}"'     -f $Name)
        ('!define APP_VERSION "{0}"'  -f $Version)
        ('!define APP_VERSION4 "{0}.0"' -f $Version)
        ('!define APP_PUBLISHER "{0}"' -f $Publisher)
        ('!define SOURCE_DIR "{0}"'   -f $SourceDir)
        ('!define OUTPUT_DIR "{0}"'   -f $OutputDir)
        ('!define OUTPUT_FILE "{0}"'  -f $OutputFile)
        ('!define APP_ICON "{0}"'     -f $IconPath)
    )

    # NSIS 只认带 BOM 的 UTF-8，否则中文会报 Bad text encoding
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllLines($Path, $lines, $utf8Bom)
}

# ============================== 主流程 ==============================
Write-Banner

# --- 上一版本 ---
$prev = Read-PreviousVersion
if ($null -eq $prev) {
    Write-Host '  上一版本：无' -ForegroundColor DarkGray
} else {
    Write-Host ("  上一版本：{0}" -f $prev) -ForegroundColor DarkGray
}

$version = Ask-Version -Previous $prev
$version4 = "$version.0"

$versionDir    = Join-Path $ReleaseRoot $version
$publishDir    = Join-Path $versionDir 'publish'
$installerName = '{0}-{1}' -f $SoftwareName, $version
$installerPath = Join-Path $versionDir ($installerName + '.exe')

Write-Host ''
Write-Host ("  输出目录：{0}" -f $versionDir) -ForegroundColor DarkGray

# --- 前置检查 ---
if (-not (Test-Path $ProjectFull)) { Fail '找不到项目文件' $ProjectFull }
if (-not (Test-Path $NsiScript))   { Fail '找不到安装脚本 installer.nsi' $NsiScript }

if ([string]::IsNullOrWhiteSpace($NsisExe)) {
    $cmd = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $NsisExe = $cmd.Source }
}
if ([string]::IsNullOrWhiteSpace($NsisExe) -or -not (Test-Path $NsisExe)) {
    Fail '找不到 makensis.exe' '请在脚本顶部 $NsisExe 填入 NSIS 的 makensis 完整路径'
}

$dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail '找不到 dotnet 命令' '请确认已安装 .NET SDK 并加入 PATH' }

if (-not (Test-Path $TempDir)) { New-Item -ItemType Directory -Path $TempDir -Force | Out-Null }

# --- 1. 编译 ---
Write-Step '编译（dotnet publish）'
Write-Info $publishDir

if (Test-Path $publishDir) {
    # 只清本次版本的 publish 产物，历史版本目录一概不碰
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    'publish'
    ('"{0}"' -f $ProjectFull)
    '-c', 'Release'
    '-f', $TargetFramework
    '-r', $RuntimeId
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    ('-p:PublishReadyToRun={0}' -f $ReadyToRun.ToString().ToLower())
    ('-p:Version={0}' -f $version)
    '-p:DebugType=none'
    '-o', ('"{0}"' -f $publishDir)
) -join ' '

$dotnetExe = $dotnet.Source
$r = Invoke-WithProgress -Label '编译中' -FilePath $dotnetExe -Arguments $publishArgs -LogFile (Join-Path $TempDir 'dotnet.log')

if ($r.ExitCode -ne 0) {
    $tail = ($r.Output -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 6) -join "`n"
    Fail 'dotnet publish 返回错误' ("完整日志：{0}`n{1}" -f $r.Log, $tail)
}

$mainExe = Join-Path $publishDir ($SoftwareName + '.exe')
if (-not (Test-Path $mainExe)) {
    $anyExe = Get-ChildItem $publishDir -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($anyExe) {
        Fail ("发布目录里没有 {0}.exe" -f $SoftwareName) ("找到的是 {0}，请确认 csproj 的 AssemblyName 与脚本顶部的软件名一致" -f $anyExe.Name)
    } else {
        Fail '发布目录里没有任何 exe' $publishDir
    }
}

$sizeMb = [Math]::Round(((Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Ok ("编译完成 · {0} MB" -f $sizeMb)

# --- 2. 打包 ---
Write-Step '打包（NSIS）'
# 先写安装脚本要的参数（毫秒级，不单独占一步）
Write-InstallerConfig -Path $NsiConfig -Name $SoftwareName -Version $version -Publisher $Publisher `
    -SourceDir $publishDir -OutputDir $versionDir -OutputFile $installerName `
    -IconPath $IconFull

$r = Invoke-WithProgress -Label '打包中' -FilePath $NsisExe -Arguments ('/V2 /DCOMPRESS_DICT_MB={0} "{1}"' -f $CompressDictMB, $NsiScript) -LogFile (Join-Path $TempDir 'makensis.log')

if ($r.ExitCode -ne 0 -or -not (Test-Path $installerPath)) {
    $tail = ($r.Output -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 8) -join "`n"
    Fail 'NSIS 打包失败' ("完整日志：{0}`n{1}" -f $r.Log, $tail)
}
$setupMb = [Math]::Round(((Get-Item $installerPath).Length / 1MB), 1)
$ratio   = if ($sizeMb -gt 0) { [Math]::Round($setupMb * 100 / $sizeMb) } else { 0 }
Write-Ok ("打包完成 · {0} MB（原始 {1} MB，压到 {2}%）" -f $setupMb, $sizeMb, $ratio)

# --- 3. 记录版本 ---
Write-Step '记录版本号'
[System.IO.File]::WriteAllText($VersionFile, $version, (New-Object System.Text.UTF8Encoding($false)))
Write-Ok ("version.txt → {0}" -f $version)

# --- 收尾 ---
$script:Clock.Stop()
Write-Host ''
Write-Host '  ================================================' -ForegroundColor DarkGray
Write-Host '   全部完成' -ForegroundColor Green
Write-Host '  ================================================' -ForegroundColor DarkGray
Write-Host ("   安装包 : {0}" -f $installerPath) -ForegroundColor White
Write-Host ("   发布文件: {0}" -f $publishDir) -ForegroundColor DarkGray
Write-Host ("   总耗时 : {0:mm\:ss}" -f $script:Clock.Elapsed) -ForegroundColor DarkGray
Write-Host ''
Write-Host '  按回车键关闭窗口…' -ForegroundColor Yellow
[void](Read-Host)
exit 0
