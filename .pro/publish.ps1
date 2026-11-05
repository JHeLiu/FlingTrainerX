<#
.SYNOPSIS
    风灵月影宗储物袋 · 生产构建 / 打包脚本。

.DESCRIPTION
    一次跑完两件事：
      1) 生产构建：win-x64、单文件、ReadyToRun，出两个版本
           绿色版   = 自包含（自带 .NET 运行时，拷走就能用）  软件名-绿色版-x.x.x
           依赖框架 = 需要用户机器装 .NET 桌面运行时            软件名-x.x.x
      2) 打包安装文件：用 NSIS 把绿色版打成安装包（QQ 风格向导：欢迎 → 许可 → 选项 → 路径 → 安装 → 完成）

    产物统一放在 .pro\<版本号>\ 下，绿色版 / 依赖框架 / 安装包 各占一个文件夹。
    每次运行会提示「上一个版本号」，直接回车沿用，输入新号则按新号发布。

.PARAMETER Version
    指定版本号（x.x.x），给了就不再交互询问。

.PARAMETER SkipInstaller
    只做构建，不打 NSIS 安装包。

.PARAMETER NoPause
    结束后不等待按键（给其它脚本调用时用）。

.EXAMPLE
    双击 .pro\发布.bat

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .pro\publish.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipInstaller,
    [switch]$NoPause,
    # 保留 .stage 里的中间文件（installer.nsi / build-config.nsh / 向导图），排查打包问题用
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ==================== 常量 ====================

$AppName    = '风灵月影宗储物袋'
$Publisher  = '风灵月影宗'
$Website    = 'https://flingtrainer.com'
$RuntimeId  = 'win-x64'

$ProDir     = $PSScriptRoot
$RepoRoot   = Split-Path $ProDir -Parent
$VersionFile = Join-Path $ProDir 'version.txt'

$Csproj = (Join-Path $RepoRoot "FlingTrainerX\FlingTrainerX.csproj")
if (-not (Test-Path $Csproj)) {
    $found = Get-ChildItem -Path $RepoRoot -Filter 'FlingTrainerX.csproj' -Recurse -File -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if (-not $found) { throw "找不到 FlingTrainerX.csproj，确认脚本放在项目根目录的 .pro 里" }
    $Csproj = $found.FullName
}
$ProjectDir = Split-Path $Csproj -Parent

# ==================== 输出样式 ====================

function Write-Title([string]$Text) {
    Write-Host ''
    Write-Host '  ' -NoNewline
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * 58)) -ForegroundColor DarkGray
}

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host '  ▶ ' -NoNewline -ForegroundColor Cyan
    Write-Host $Text -ForegroundColor White
}

function Write-Ok([string]$Text) {
    Write-Host '    ✓ ' -NoNewline -ForegroundColor Green
    Write-Host $Text -ForegroundColor Gray
}

function Write-Info([string]$Text) {
    Write-Host '    · ' -NoNewline -ForegroundColor DarkGray
    Write-Host $Text -ForegroundColor DarkGray
}

function Write-Warn([string]$Text) {
    Write-Host '    ! ' -NoNewline -ForegroundColor Yellow
    Write-Host $Text -ForegroundColor Yellow
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f ($Bytes / 1KB))
}

# ==================== 版本号 ====================

function Get-PreviousVersion {
    if (Test-Path $VersionFile) {
        $saved = (Get-Content $VersionFile -Raw -Encoding UTF8).Trim()
        if ($saved -match '^\d+\.\d+\.\d+$') { return $saved }
    }

    try {
        $text = Get-Content $Csproj -Raw -Encoding UTF8
        if ($text -match '<Version>\s*(\d+\.\d+\.\d+)\s*</Version>') { return $Matches[1] }
    } catch {
        # 读不到就用兜底值
    }

    return '1.0.0'
}

function Read-TargetVersion([string]$Previous) {
    while ($true) {
        Write-Host ''
        Write-Host '  上一个版本号：' -NoNewline -ForegroundColor DarkGray
        Write-Host $Previous -ForegroundColor Yellow
        Write-Host '  输入新版本号（x.x.x），直接回车沿用：' -ForegroundColor Gray
        Write-Host '  > ' -NoNewline -ForegroundColor Cyan

        $typed = (Read-Host).Trim()
        if ([string]::IsNullOrWhiteSpace($typed)) { return $Previous }

        $typed = $typed.TrimStart('v', 'V').Trim()
        if ($typed -match '^\d+\.\d+\.\d+$') { return $typed }

        Write-Host '  格式不对，要写成 1.0.1 这样三段数字' -ForegroundColor Red
    }
}

# ==================== 构建 ====================

function Publish-App {
    param(
        [string]$OutDir,
        [bool]$SelfContained,
        [string]$Label
    )

    if (Test-Path $OutDir) { try { Remove-Item $OutDir -Recurse -Force -ErrorAction Stop } catch { } }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $publishArgs = @(
        'publish', $Csproj
        '-c', 'Release'
        '-r', $RuntimeId
        "-p:SelfContained=$($SelfContained.ToString().ToLower())"
        '-p:PublishSingleFile=true'
        # 原生库（WPF / SQLite）也塞进单文件，发布目录里只留一个 exe
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:PublishReadyToRun=true'
        # WPF 不支持裁剪，显式关掉，免得以后有人手抖打开
        '-p:PublishTrimmed=false'
        '-p:DebugType=none'
        '-p:DebugSymbols=false'
        "-p:Version=$Version"
        '-o', $OutDir
        '--nologo'
        '-v', 'm'
    )

    # 单文件压缩只对自包含版本有效（NETSDK1176），依赖框架版不能带这个参数
    if ($SelfContained) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet @publishArgs
    $code = $LASTEXITCODE
    $sw.Stop()

    if ($code -ne 0) { throw "$Label 构建失败（dotnet publish 退出码 $code）" }

    $exe = Join-Path $OutDir "$AppName.exe"
    if (-not (Test-Path $exe)) { throw "$Label 构建没有产出 $AppName.exe" }

    $size = (Get-Item $exe).Length
    Write-Ok "$Label 完成：$(Format-Size $size)，用时 $([int]$sw.Elapsed.TotalSeconds) 秒"
}

# 把发布目录里除 exe 之外的东西（Assets\免责声明.txt 等）一并搬到成品目录
function Copy-Artifacts {
    param(
        [string]$From,
        [string]$To,
        [string]$ExeName
    )

    New-Item -ItemType Directory -Force -Path $To | Out-Null

    Copy-Item (Join-Path $From "$AppName.exe") (Join-Path $To $ExeName) -Force

    # 目录要一层层拷文件，不能直接 Copy-Item 整个文件夹：
    # 目标已存在时 Copy-Item -Recurse 会把源目录整个塞进去，重跑一次就多套一层 Assets\Assets
    Get-ChildItem -Path $From -Force | Where-Object { $_.Name -ne "$AppName.exe" } | ForEach-Object {
        if ($_.PSIsContainer) {
            $sub = Join-Path $To $_.Name
            New-Item -ItemType Directory -Force -Path $sub | Out-Null
            Get-ChildItem -Path $_.FullName -File -Recurse -Force | ForEach-Object {
                Copy-Item $_.FullName (Join-Path $sub $_.Name) -Force
            }
        } else {
            Copy-Item $_.FullName (Join-Path $To $_.Name) -Force
        }
    }
}

# ==================== NSIS ====================

function Find-Makensis {
    $candidates = @()

    $cmd = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }

    # 安装时选了别的目录也能找到：NSIS 安装程序会写 InstallLocation
    foreach ($key in @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\NSIS'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NSIS'
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NSIS'
    )) {
        try {
            $loc = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallLocation
            if ($loc) { $candidates += (Join-Path $loc 'makensis.exe') }
        } catch { }
    }

    $candidates += @(
        (Join-Path $ProDir 'tools\nsis\makensis.exe')
        "$env:ProgramFiles\NSIS\makensis.exe"
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
        "$env:LOCALAPPDATA\Programs\NSIS\makensis.exe"
    )

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    # tools 下解压出来的任意版本
    $tools = Join-Path $ProDir 'tools'
    if (Test-Path $tools) {
        $hit = Get-ChildItem -Path $tools -Filter 'makensis.exe' -Recurse -File -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    return $null
}

function Ensure-Nsis {
    $found = Find-Makensis
    if ($found) {
        Write-Ok "使用 NSIS：$found"
        return $found
    }

    Write-Warn '没有找到 NSIS（打安装包要用它）'
    Write-Info '可以下载便携版到 .pro\tools，不写注册表、不装系统组件'
    Write-Host ''
    Write-Host '  现在下载吗？[Y/n] ' -NoNewline -ForegroundColor Cyan

    $answer = (Read-Host).Trim().ToLowerInvariant()
    if ($answer -eq 'n' -or $answer -eq 'no') { return $null }

    $tools = Join-Path $ProDir 'tools'
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    $zip = Join-Path $tools 'nsis.zip'
    $url = 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip'

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    Write-Info "下载 $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $tools -Force
    Remove-Item $zip -Force

    $found = Find-Makensis
    if (-not $found) { throw 'NSIS 下载完成但没找到 makensis.exe' }

    Write-Ok "NSIS 就绪：$found"
    return $found
}

# 生成安装向导用的两张图（NSIS 只认 24 位未压缩 BMP）：
#   左侧竖版 164×314 —— 深色背景 + logo + 软件名
#   顶部横版 150×57  —— 白底 + logo + 软件名（跟向导页底色融在一起）
function New-InstallerBitmaps {
    param(
        [string]$OutDir,
        [string]$LogoPath
    )

    Add-Type -AssemblyName System.Drawing | Out-Null

    $navyTop   = [System.Drawing.Color]::FromArgb(9, 18, 30)
    $navyBot   = [System.Drawing.Color]::FromArgb(22, 45, 70)
    $accent    = [System.Drawing.Color]::FromArgb(64, 160, 255)
    $textLight = [System.Drawing.Color]::FromArgb(230, 240, 250)
    $textDim   = [System.Drawing.Color]::FromArgb(140, 165, 190)

    $logo = $null
    if ($LogoPath -and (Test-Path $LogoPath)) {
        try { $logo = [System.Drawing.Image]::FromFile($LogoPath) } catch { $logo = $null }
    }

    $center = New-Object System.Drawing.StringFormat
    $center.Alignment = [System.Drawing.StringAlignment]::Center

    # ---- 竖版 ----
    $w = 164; $h = 314
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $navyTop, $navyBot, 90.0)
    $g.FillRectangle($bg, $rect)
    $bg.Dispose()

    $g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 0, 0, $w, 3)

    if ($logo) { $g.DrawImage($logo, [int](($w - 64) / 2), 44, 64, 64) }

    $fontName = New-Object System.Drawing.Font('Microsoft YaHei', 12, [System.Drawing.FontStyle]::Bold)
    $brushText = New-Object System.Drawing.SolidBrush($textLight)
    $g.DrawString($AppName, $fontName, $brushText, (New-Object System.Drawing.RectangleF(0, 124, $w, 64)), $center)

    $fontSmall = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
    $brushDim = New-Object System.Drawing.SolidBrush($textDim)
    $g.DrawString('FLiNG 修改器库', $fontSmall, $brushDim, (New-Object System.Drawing.RectangleF(0, 178, $w, 24)), $center)
    $g.DrawString("v$Version", $fontSmall, $brushDim, (New-Object System.Drawing.RectangleF(0, 272, $w, 24)), $center)

    $bmp.Save((Join-Path $OutDir 'welcome.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)

    $fontName.Dispose(); $fontSmall.Dispose(); $brushText.Dispose(); $brushDim.Dispose(); $center.Dispose()
    $g.Dispose(); $bmp.Dispose()

    # ---- 横版 ----
    $w = 150; $h = 57
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $g.Clear([System.Drawing.Color]::White)
    if ($logo) { $g.DrawImage($logo, 8, 11, 34, 34) }

    $fontName = New-Object System.Drawing.Font('Microsoft YaHei', 8, [System.Drawing.FontStyle]::Bold)
    $brushDark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32, 56, 84))
    $g.DrawString($AppName, $fontName, $brushDark, 48, 20)

    $bmp.Save((Join-Path $OutDir 'header.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)

    $fontName.Dispose(); $brushDark.Dispose(); $g.Dispose(); $bmp.Dispose()

    if ($logo) { $logo.Dispose() }
}

function Build-Installer {
    param(
        [string]$Makensis,
        [string]$PayloadDir,
        [string]$PayloadExe,
        [string]$SetupFile,
        [string]$StageDir
    )

    $layout = Join-Path $StageDir 'installer'
    if (Test-Path $layout) { try { Remove-Item $layout -Recurse -Force -ErrorAction Stop } catch { } }
    New-Item -ItemType Directory -Force -Path $layout | Out-Null

    New-InstallerBitmaps -OutDir $layout -LogoPath (Join-Path $ProjectDir 'Assets\logo.png')

    # 图标：优先用工程里的 app.ico，没有就用系统默认
    $icon = Join-Path $ProjectDir 'Assets\app.ico'
    if (-not (Test-Path $icon)) { $icon = Join-Path $env:SystemRoot 'System32\shell32.dll' }
    if ($icon -like '*.dll') { $icon = Join-Path $env:SystemRoot 'System32\imageres.dll' }

    $license = Join-Path $ProjectDir 'Assets\免责声明.txt'

    # 本次构建的参数写成 .nsh，避免命令行上传中文 / 空格路径出岔子
    $config = @(
        "; 由 publish.ps1 生成，不要手改"
        "!define APPVERSION `"$Version`""
        "!define SOURCEDIR `"$PayloadDir`""
        "!define SRCEXE `"$PayloadExe`""
        "!define OUTFILE `"$SetupFile`""
        "!define ICONFILE `"$icon`""
        "!define LICENSEFILE `"$license`""
        "!define WELCOMEBMP `"$(Join-Path $layout 'welcome.bmp')`""
        "!define HEADERBMP `"$(Join-Path $layout 'header.bmp')`""
        "!define WEBSITE `"$Website`""
        ''
    ) -join "`r`n"

    [IO.File]::WriteAllText((Join-Path $layout 'build-config.nsh'), $config, (New-Object System.Text.UTF8Encoding($true)))

    # .nsi 也复制成带 BOM 的 UTF-8：NSIS 3 靠 BOM 认定编码，中文才不会乱码
    $nsiText = Get-Content (Join-Path $ProDir 'installer.nsi') -Raw -Encoding UTF8
    $nsiPath = Join-Path $layout 'installer.nsi'
    [IO.File]::WriteAllText($nsiPath, $nsiText, (New-Object System.Text.UTF8Encoding($true)))

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Makensis /V2 $nsiPath
    $code = $LASTEXITCODE
    $sw.Stop()

    if ($code -ne 0) { throw "NSIS 打包失败（makensis 退出码 $code）" }
    if (-not (Test-Path $SetupFile)) { throw "NSIS 没有产出安装包：$SetupFile" }

    $size = (Get-Item $SetupFile).Length
    Write-Ok "安装包完成：$(Format-Size $size)，用时 $([int]$sw.Elapsed.TotalSeconds) 秒"
}

# ==================== 主流程 ====================

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host '  ╔══════════════════════════════════════════════════════════╗' -ForegroundColor DarkCyan
Write-Host '  ║         风灵月影宗储物袋 · 生产构建 / 打包               ║' -ForegroundColor Cyan
Write-Host '  ╚══════════════════════════════════════════════════════════╝' -ForegroundColor DarkCyan
Write-Info "目标运行时：$RuntimeId（64 位） · 单文件 · ReadyToRun"

$dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '没有找到 dotnet 命令，请先安装 .NET SDK' }

$previous = Get-PreviousVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-TargetVersion $previous
} elseif ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号格式不对：$Version（要写成 x.x.x）"
}

$VersionDir = Join-Path $ProDir $Version
$GreenDir   = Join-Path $VersionDir '绿色版'
$FddDir     = Join-Path $VersionDir '依赖框架'
$SetupDir   = Join-Path $VersionDir '安装包'

$GreenExe = "$AppName-绿色版-$Version.exe"
$FddExe   = "$AppName-$Version.exe"
$SetupExe = "$AppName-安装版-$Version.exe"

Set-Content -Path $VersionFile -Value $Version -Encoding UTF8 -NoNewline

Write-Host ''
Write-Host '  版本号：' -NoNewline -ForegroundColor DarkGray
Write-Host $Version -ForegroundColor Green -NoNewline
if ($Version -ne $previous) { Write-Host "（上一版 $previous）" -ForegroundColor DarkGray } else { Write-Host '（沿用上一版）' -ForegroundColor DarkGray }
Write-Host '  产物目录：' -NoNewline -ForegroundColor DarkGray
Write-Host ".pro\$Version" -ForegroundColor Gray

# 直接覆盖同名产物（文件名里本来就带版本号），不整目录删除：
# 有些安全软件 / 同步盘会拦住删除大目录，删失败反而让人以为构建挂了
New-Item -ItemType Directory -Force -Path $GreenDir, $FddDir, $SetupDir | Out-Null
Get-ChildItem -Path $GreenDir, $FddDir, $SetupDir -Filter '*.exe' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$stageRoot = Join-Path $ProDir '.stage'
if (Test-Path $stageRoot) { try { Remove-Item $stageRoot -Recurse -Force -ErrorAction Stop } catch { } }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$script:buildFailed = $false

try {
    # ---------- 1. 绿色版（自包含） ----------
    Write-Step "[1/4] 构建绿色版（自包含，自带运行时，单文件）"
    $greenStage = Join-Path $stageRoot 'green'
    Publish-App -OutDir $greenStage -SelfContained $true -Label '绿色版'
    Copy-Artifacts -From $greenStage -To $GreenDir -ExeName $GreenExe
    Write-Ok "输出：$GreenExe"

    # ---------- 2. 依赖框架版 ----------
    Write-Step "[2/4] 构建依赖框架版（需要机器上装 .NET 桌面运行时，体积小）"
    $fddStage = Join-Path $stageRoot 'fdd'
    Publish-App -OutDir $fddStage -SelfContained $false -Label '依赖框架版'
    Copy-Artifacts -From $fddStage -To $FddDir -ExeName $FddExe
    Write-Ok "输出：$FddExe"

    # ---------- 3. 安装包 ----------
    if ($SkipInstaller) {
        Write-Step '[3/4] 跳过安装包（-SkipInstaller）'
    } else {
        Write-Step '[3/4] 打包安装文件（NSIS）'
        $makensis = Ensure-Nsis
        if (-not $makensis) {
            Write-Warn '没有 NSIS，跳过安装包；单独装好 NSIS 后再跑一次即可'
        } else {
            Build-Installer -Makensis $makensis `
                            -PayloadDir $GreenDir `
                            -PayloadExe $GreenExe `
                            -SetupFile (Join-Path $SetupDir $SetupExe) `
                            -StageDir $stageRoot
            Write-Ok "输出：$SetupExe"
        }
    }

    # ---------- 4. 汇总 ----------
    Write-Step '[4/4] 构建完成'
    $totalSw.Stop()

    foreach ($folder in @($GreenDir, $FddDir, $SetupDir)) {
        $files = Get-ChildItem -Path $folder -File -ErrorAction SilentlyContinue
        if (-not $files) { continue }

        Write-Host ''
        Write-Host "    $([IO.Path]::GetFileName($folder))" -ForegroundColor White
        foreach ($f in $files) {
            Write-Host ('      ' + $f.Name) -ForegroundColor Gray -NoNewline
            Write-Host ('  ' + (Format-Size $f.Length)) -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    Write-Host "  总用时 $([int]$totalSw.Elapsed.TotalSeconds) 秒" -ForegroundColor DarkGray
    Write-Host "  产物位置：$VersionDir" -ForegroundColor Cyan
    if ($KeepStage) { Write-Info "中间文件保留在：$stageRoot" }
    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host '  构建失败：' -ForegroundColor Red -NoNewline
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    $script:buildFailed = $true
}
finally {
    if ((Test-Path $stageRoot) -and -not $KeepStage) {
        try { Remove-Item $stageRoot -Recurse -Force } catch { }
    }
}

# 成功才自己停住等按键；失败时由外面的 发布.bat 停（免得连按两次）
if ($script:buildFailed) { exit 1 }

if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') {
    Write-Host '  按任意键关闭…' -ForegroundColor DarkGray
    try { $null = [Console]::ReadKey($true) } catch { }
}
