<#
.SYNOPSIS
    风灵月影宗储物袋 · 生产构建 / 打包脚本（双击 .pro\发布.bat 运行）。

.DESCRIPTION
    流程：读取/输入版本号 → .NET 生产构建 → 生成安装界面资源 → NSIS 打包安装包。

    构建命令（按指定参数，不再增删开关）：
        dotnet publish -c Release -f net10.0-windows -r win-x64
            --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o <产物目录>
    -o 直接指向 .pro\<版本号>\，构建产物就落在版本号目录下，且不删除该目录里已有的文件。

    安装包为自绘 QQ 风格界面（nsDialogs 自绘整窗，不用 NSIS 自带向导，无免责声明页），
    文件名：风灵月影宗储物袋-安装包-<版本号>.exe，同样放在 .pro\<版本号>\ 下。

    控制台只输出简单流程，不打印 dotnet / NSIS 的逐文件详细信息（失败时才回显末尾几行）。

.PARAMETER Version
    指定版本号（x.x.x），给了就不再交互询问。

.PARAMETER SkipInstaller
    只做构建，不打 NSIS 安装包。

.PARAMETER NoPause
    结束后不等待按键（给其它脚本调用时用）。

.PARAMETER KeepStage
    保留 .stage 中间文件（nsi / 界面位图 / 日志），排查问题用。
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipInstaller,
    [switch]$NoPause,
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ==================== 常量 ====================

$AppName     = '风灵月影宗储物袋'
$Publisher   = '风灵月影宗'
$Website     = 'https://flingtrainer.com'
$AppExe      = "$AppName.exe"
$ProDir      = $PSScriptRoot
$RepoRoot    = Split-Path $ProDir -Parent
$VersionFile = Join-Path $ProDir 'version.txt'

$Csproj = Join-Path $RepoRoot 'FlingTrainerX\FlingTrainerX.csproj'
if (-not (Test-Path $Csproj)) {
    $found = Get-ChildItem -Path $RepoRoot -Filter 'FlingTrainerX.csproj' -Recurse -File -ErrorAction SilentlyContinue |
             Select-Object -First 1
    if (-not $found) { throw '找不到 FlingTrainerX.csproj，确认脚本放在项目根目录的 .pro 里' }
    $Csproj = $found.FullName
}
$ProjectDir = Split-Path $Csproj -Parent

# ==================== 输出样式（只走简单流程） ====================

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

function Write-Step([string]$No, [string]$Text) {
    Write-Host "  [$No] " -NoNewline -ForegroundColor Cyan
    Write-Host $Text -NoNewline -ForegroundColor White
}

function Write-StepDone([string]$Text) {
    Write-Host (' ' * [Math]::Max(1, 26 - $Text.Length)) -NoNewline
    Write-Host '✓ ' -NoNewline -ForegroundColor Green
    Write-Host $Text -ForegroundColor DarkGray
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f ($Bytes / 1KB))
}

# ==================== 版本号 ====================

# 是否处于非交互环境（后台 / 管道调用）：Read-Host 拿不到输入，提示要能自动跳过
function Test-NonInteractive {
    try { return [Console]::IsInputRedirected } catch { return $false }
}

function Get-PreviousVersion {
    if (Test-Path $VersionFile) {
        $saved = (Get-Content $VersionFile -Raw -Encoding UTF8).Trim()
        if ($saved -match '^\d+\.\d+\.\d+$') { return $saved }
    }
    try {
        $text = Get-Content $Csproj -Raw -Encoding UTF8
        if ($text -match '<Version>\s*(\d+\.\d+\.\d+)\s*</Version>') { return $Matches[1] }
    } catch { }
    return '1.0.0'
}

function Read-TargetVersion([string]$Previous) {
    if (Test-NonInteractive) {
        Write-Info "非交互模式，沿用上一版本号：$Previous"
        return $Previous
    }
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

# ==================== 安装界面资源（自绘 QQ 风格，NSIS 只认 24 位 BMP） ====================

# 圆角矩形填充
function Fill-RoundRect($g, $brush, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $d = 2 * $r
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $g.FillPath($brush, $p)
    $p.Dispose()
}

# 画布：24bpp、抗锯齿、高质量文字
function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @{ Bmp = $bmp; G = $g }
}

function New-InstallerArt {
    param(
        [string]$OutDir,
        [string]$LogoPath
    )

    Add-Type -AssemblyName System.Drawing | Out-Null

    $W = 680; $H = 460
    $HeroH = 180
    $FootY = 390
    $FontName = 'Microsoft YaHei'

    $blueTop   = [System.Drawing.Color]::FromArgb(18, 183, 245)
    $blueBot   = [System.Drawing.Color]::FromArgb(10, 122, 214)
    $btnTop    = [System.Drawing.Color]::FromArgb(24, 190, 250)
    $btnBot    = [System.Drawing.Color]::FromArgb(8, 145, 228)
    $white     = [System.Drawing.Color]::FromArgb(255, 255, 255)
    $footColor = [System.Drawing.Color]::FromArgb(246, 249, 252)
    $textDark  = [System.Drawing.Color]::FromArgb(31, 45, 61)
    $textGray  = [System.Drawing.Color]::FromArgb(138, 154, 174)
    $lineColor = [System.Drawing.Color]::FromArgb(230, 238, 246)
    $green     = [System.Drawing.Color]::FromArgb(28, 186, 122)

    $logo = $null
    if ($LogoPath -and (Test-Path $LogoPath)) {
        try { $logo = [System.Drawing.Image]::FromFile($LogoPath) } catch { $logo = $null }
    }

    # 画顶部蓝色渐变 + logo + 软件名，三个页面共用
    $drawHero = {
        param($g)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $HeroH)
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $blueTop, $blueBot, 90.0)
        $g.FillRectangle($grad, $rect)
        $grad.Dispose()

        # 右上角柔和光斑，做出 QQ 安装器那种层次感
        $soft = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(18, 255, 255, 255))
        $g.FillEllipse($soft, 490, -95, 270, 270)
        $g.FillEllipse($soft, 585, 55, 160, 160)
        $soft.Dispose()

        if ($logo) { $g.DrawImage($logo, 40, 46, 62, 62) }

        $fTitle = New-Object System.Drawing.Font($FontName, 21, [System.Drawing.FontStyle]::Bold)
        $fSub   = New-Object System.Drawing.Font($FontName, 10.5)
        $fVer   = New-Object System.Drawing.Font($FontName, 9.5, [System.Drawing.FontStyle]::Bold)
        $bWhite = New-Object System.Drawing.SolidBrush($white)
        $bLite  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 240, 255))

        $g.DrawString($AppName, $fTitle, $bWhite, 116, 46)
        $g.DrawString('FLiNG 修改器库  ·  安装向导', $fSub, $bLite, 118, 88)

        # 版本号胶囊
        $pill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, 255, 255, 255))
        Fill-RoundRect $g $pill 566 136 76 26 13
        $pill.Dispose()
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString("v$Version", $fVer, $bWhite, (New-Object System.Drawing.RectangleF(566, 136, 76, 26)), $fmt)

        $fTitle.Dispose(); $fSub.Dispose(); $fVer.Dispose()
        $bWhite.Dispose(); $bLite.Dispose(); $fmt.Dispose()
    }

    # 画底部浅色条 + 分割线（按钮由 NSIS 控件叠在上面）
    $drawFooter = {
        param($g)
        $bFoot = New-Object System.Drawing.SolidBrush($footColor)
        $g.FillRectangle($bFoot, 0, $FootY, $W, $H - $FootY)
        $bFoot.Dispose()
        $pen = New-Object System.Drawing.Pen($lineColor, 1)
        $g.DrawLine($pen, 40, $FootY, $W - 40, $FootY)
        $pen.Dispose()
        $fSmall = New-Object System.Drawing.Font($FontName, 9)
        $bGray = New-Object System.Drawing.SolidBrush($textGray)
        $g.DrawString("$Publisher  ·  v$Version  ·  $Website", $fSmall, $bGray, 40, 416)
        $fSmall.Dispose(); $bGray.Dispose()
    }

    $bDark = { New-Object System.Drawing.SolidBrush($textDark) }
    $bGray = { New-Object System.Drawing.SolidBrush($textGray) }

    # ---------- 页面 1：安装位置 / 选项 ----------
    $c = New-Canvas $W $H
    $g = $c.G
    $g.Clear($white)
    & $drawHero $g
    & $drawFooter $g
    $fLabel = New-Object System.Drawing.Font($FontName, 12, [System.Drawing.FontStyle]::Bold)
    $fHint  = New-Object System.Drawing.Font($FontName, 9.5)
    $bd = & $bDark; $bg = & $bGray
    $g.DrawString('安装位置', $fLabel, $bd, 40, 206)
    $g.DrawString('请选择有写入权限的目录，程序会在这里保存游戏数据。', $fHint, $bg, 40, 286)
    $g.DrawString('创建桌面快捷方式', (New-Object System.Drawing.Font($FontName, 11)), $bd, 64, 328)
    $bd.Dispose(); $bg.Dispose(); $fLabel.Dispose(); $fHint.Dispose()
    $c.Bmp.Save((Join-Path $OutDir 'page-options.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)

    # 关闭按钮：从顶栏对应位置裁一块，再画白色 X，保证和背景无缝
    $cw = 28; $ch = 24
    $close = New-Object System.Drawing.Bitmap($cw, $ch, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $cg = [System.Drawing.Graphics]::FromImage($close)
    $cg.DrawImage($c.Bmp,
        (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)),
        (New-Object System.Drawing.Rectangle(644, 8, $cw, $ch)),
        [System.Drawing.GraphicsUnit]::Pixel)
    $cg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $penX = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 248, 255), 2)
    $cg.DrawLine($penX, 10, 8, 18, 16)
    $cg.DrawLine($penX, 18, 8, 10, 16)
    $penX.Dispose()
    $close.Save((Join-Path $OutDir 'btn-close.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $cg.Dispose(); $close.Dispose()

    $g.Dispose(); $c.Bmp.Dispose()

    # ---------- 页面 2：安装进度 ----------
    $c = New-Canvas $W $H
    $g = $c.G
    $g.Clear($white)
    & $drawHero $g
    & $drawFooter $g
    $fBig = New-Object System.Drawing.Font($FontName, 18, [System.Drawing.FontStyle]::Bold)
    $fMid = New-Object System.Drawing.Font($FontName, 10.5)
    $fHint = New-Object System.Drawing.Font($FontName, 9.5)
    $bd = & $bDark; $bg = & $bGray
    $g.DrawString('正在安装', $fBig, $bd, 40, 218)
    $g.DrawString('正在将程序安装到你的电脑，请稍候…', $fMid, $bg, 40, 258)
    # 进度条底槽（真进度条控件叠在上面）
    $bTrack = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(228, 239, 249))
    Fill-RoundRect $g $bTrack 40 300 600 14 7
    $bTrack.Dispose()
    $g.DrawString('安装过程中请勿关闭窗口', $fHint, $bg, 40, 330)
    $fBig.Dispose(); $fMid.Dispose(); $fHint.Dispose(); $bd.Dispose(); $bg.Dispose()
    $c.Bmp.Save((Join-Path $OutDir 'page-progress.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $g.Dispose(); $c.Bmp.Dispose()

    # ---------- 页面 3：安装完成 ----------
    $c = New-Canvas $W $H
    $g = $c.G
    $g.Clear($white)
    & $drawHero $g
    & $drawFooter $g
    $bGreen = New-Object System.Drawing.SolidBrush($green)
    $g.FillEllipse($bGreen, 40, 218, 52, 52)
    $bGreen.Dispose()
    $penCheck = New-Object System.Drawing.Pen($white, 4)
    $g.DrawLines($penCheck, @(
        (New-Object System.Drawing.Point(54, 244)),
        (New-Object System.Drawing.Point(63, 253)),
        (New-Object System.Drawing.Point(79, 234))
    ))
    $penCheck.Dispose()
    $fBig  = New-Object System.Drawing.Font($FontName, 20, [System.Drawing.FontStyle]::Bold)
    $fMid  = New-Object System.Drawing.Font($FontName, 10.5)
    $fHint = New-Object System.Drawing.Font($FontName, 9.5)
    $bd = & $bDark; $bg = & $bGray
    $g.DrawString('安装完成', $fBig, $bd, 108, 218)
    $g.DrawString("$AppName v$Version 已经装好，可以开始使用了。", $fMid, $bg, 110, 258)
    $g.DrawString("立即运行 $AppName", (New-Object System.Drawing.Font($FontName, 11)), $bd, 64, 318)
    $g.DrawString('程序会缩到右下角托盘运行，可从开始菜单随时打开。', $fHint, $bg, 40, 352)
    $fBig.Dispose(); $fMid.Dispose(); $fHint.Dispose(); $bd.Dispose(); $bg.Dispose()
    $c.Bmp.Save((Join-Path $OutDir 'page-finish.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $g.Dispose(); $c.Bmp.Dispose()

    # ---------- 图片按钮 ----------
    # 主按钮：蓝色渐变圆角，四角填成底色（成品页底色）
    $mainButtons = @(
        @{ File = 'btn-install.bmp'; Text = '立即安装'; C1 = $btnTop; C2 = $btnBot }
        @{ File = 'btn-finish.bmp';  Text = '完成';     C1 = $btnTop; C2 = $btnBot }
        @{ File = 'btn-wait.bmp';    Text = '安装中…';  C1 = [System.Drawing.Color]::FromArgb(200, 214, 228); C2 = [System.Drawing.Color]::FromArgb(178, 196, 214) }
    )
    foreach ($b in $mainButtons) {
        $bw = 125; $bh = 36
        $c = New-Canvas $bw $bh
        $g = $c.G
        $g.Clear($footColor)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $bw, $bh)
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $b.C1, $b.C2, 90.0)
        Fill-RoundRect $g $grad 0 0 $bw $bh 8
        $grad.Dispose()
        $f = New-Object System.Drawing.Font($FontName, 11, [System.Drawing.FontStyle]::Bold)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString($b.Text, $f, (New-Object System.Drawing.SolidBrush($white)), (New-Object System.Drawing.RectangleF(0, 0, $bw, $bh)), $fmt)
        $f.Dispose(); $fmt.Dispose()
        $c.Bmp.Save((Join-Path $OutDir $b.File), [System.Drawing.Imaging.ImageFormat]::Bmp)
        $g.Dispose(); $c.Bmp.Dispose()
    }

    # 「浏览」小按钮：白底 + 淡蓝描边
    $bw = 68; $bh = 36
    $c = New-Canvas $bw $bh
    $g = $c.G
    $g.Clear($white)
    $bFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(244, 250, 255))
    Fill-RoundRect $g $bFill 0 0 $bw $bh 6
    $bFill.Dispose()
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(190, 220, 245), 1)
    $d = 12
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc(0, 0, $d, $d, 180, 90)
    $p.AddArc($bw - $d, 0, $d, $d, 270, 90)
    $p.AddArc($bw - $d, $bh - $d, $d, $d, 0, 90)
    $p.AddArc(0, $bh - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $g.DrawPath($pen, $p)
    $p.Dispose(); $pen.Dispose()
    $f = New-Object System.Drawing.Font($FontName, 10)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString('浏览', $f, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(10, 140, 220))), (New-Object System.Drawing.RectangleF(0, 0, $bw, $bh)), $fmt)
    $f.Dispose(); $fmt.Dispose()
    $c.Bmp.Save((Join-Path $OutDir 'btn-browse.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $g.Dispose(); $c.Bmp.Dispose()

    if ($logo) { $logo.Dispose() }
}

# ==================== NSIS ====================

function Find-Makensis {
    $candidates = @()
    $cmd = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
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
        "$env:ProgramFiles\NSIS\makensis.exe"
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Build-Installer {
    param(
        [string]$Makensis,
        [string]$PayloadDir,
        [string]$SetupTemp,
        [string]$StageDir
    )

    $layout = Join-Path $StageDir 'installer'
    New-Item -ItemType Directory -Force -Path $layout | Out-Null

    New-InstallerArt -OutDir $layout -LogoPath (Join-Path $ProjectDir 'Assets\logo.png')

    $icon = Join-Path $ProjectDir 'Assets\app.ico'
    if (-not (Test-Path $icon)) { $icon = Join-Path $env:SystemRoot 'System32\imageres.dll' }

    # 编译期参数写成 .nsh，避免命令行传中文 / 空格路径出岔子
    $config = @(
        '; 由 publish.ps1 生成，不要手改'
        "!define APPVERSION `"$Version`""
        "!define SOURCEDIR `"$PayloadDir`""
        "!define OUTFILE `"$SetupTemp`""
        "!define ARTDIR `"$layout`""
        "!define ICONFILE `"$icon`""
        "!define WEBSITE `"$Website`""
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $layout 'build-config.nsh'), $config, (New-Object System.Text.UTF8Encoding($true)))

    # .nsi 复制成带 BOM 的 UTF-8：NSIS 3 靠 BOM 认定编码，中文才不会乱码
    $nsiText = Get-Content (Join-Path $ProDir 'installer.nsi') -Raw -Encoding UTF8
    $nsiPath = Join-Path $layout 'installer.nsi'
    [IO.File]::WriteAllText($nsiPath, $nsiText, (New-Object System.Text.UTF8Encoding($true)))

    $log = Join-Path $StageDir 'nsis.log'
    & $Makensis /V1 $nsiPath *> $log
    if ($LASTEXITCODE -ne 0) {
        $tail = (Get-Content $log -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
        throw "NSIS 打包失败（退出码 $LASTEXITCODE）`n$tail"
    }
    if (-not (Test-Path $SetupTemp)) { throw "NSIS 没有产出安装包：$SetupTemp" }
}

# ==================== 主流程 ====================

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host '  ╔══════════════════════════════════════════════════════════╗' -ForegroundColor DarkCyan
Write-Host '  ║              风灵月影宗储物袋 · 发布                      ║' -ForegroundColor Cyan
Write-Host '  ╚══════════════════════════════════════════════════════════╝' -ForegroundColor DarkCyan

$dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if (-not $dotnet) { throw '没有找到 dotnet 命令，请先安装 .NET SDK' }

$previous = Get-PreviousVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-TargetVersion $previous
} elseif ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号格式不对：$Version（要写成 x.x.x）"
}

$VersionDir = Join-Path $ProDir $Version
$SetupExe   = "$AppName-安装包-$Version.exe"
$stageRoot  = Join-Path $ProDir '.stage'

# 产物直接落在 .pro\<版本号>\；已有文件一律保留，不清空、不覆盖删除
New-Item -ItemType Directory -Force -Path $VersionDir, $stageRoot | Out-Null
Set-Content -Path $VersionFile -Value $Version -Encoding UTF8 -NoNewline

Write-Host ''
Write-Host '  ── 发布流程 ───────────────────────────────────────────────' -ForegroundColor DarkGray

$script:buildFailed = $false

try {
    # ---------- 1/4 版本号 ----------
    Write-Step '1/4' '读取版本号'
    Write-StepDone $Version

    # ---------- 2/4 .NET 生产构建 ----------
    Write-Step '2/4' '.NET 生产构建'
    $publishArgs = @(
        'publish', $Csproj
        '-c', 'Release'
        '-f', 'net10.0-windows'
        '-r', 'win-x64'
        '--self-contained', 'true'
        '-p:PublishSingleFile=true'
        '-p:PublishReadyToRun=true'
        '-o', $VersionDir
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $stageRoot 'publish.log'
    # 详细输出进日志，控制台不刷 dotnet 的逐文件信息
    & dotnet @publishArgs *> $log
    $code = $LASTEXITCODE
    $sw.Stop()
    if ($code -ne 0) {
        $tail = (Get-Content $log -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
        throw "生产构建失败（dotnet publish 退出码 $code）`n$tail"
    }
    if (-not (Test-Path (Join-Path $VersionDir $AppExe))) { throw "构建没有产出 $AppExe" }
    Write-StepDone "$([int]$sw.Elapsed.TotalSeconds) 秒"

    # ---------- 3/4 安装界面资源 ----------
    $layout = Join-Path $stageRoot 'installer'
    New-Item -ItemType Directory -Force -Path $layout | Out-Null
    Write-Step '3/4' '生成安装界面资源'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    New-InstallerArt -OutDir $layout -LogoPath (Join-Path $ProjectDir 'Assets\logo.png')
    $sw.Stop()
    Write-StepDone "$([int]$sw.Elapsed.TotalSeconds) 秒"

    # ---------- 4/4 NSIS 打包 ----------
    if ($SkipInstaller) {
        Write-Step '4/4' 'NSIS 打包安装包'
        Write-StepDone '已跳过'
    } else {
        Write-Step '4/4' 'NSIS 打包安装包'
        $makensis = Find-Makensis
        if (-not $makensis) { throw '没有找到 NSIS（makensis.exe 不在 PATH 里，也没找到常见安装位置）' }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $setupTemp = Join-Path $stageRoot $SetupExe
        Build-Installer -Makensis $makensis `
                        -PayloadDir $VersionDir `
                        -SetupTemp $setupTemp `
                        -StageDir $stageRoot
        # 编译成功再放进版本号目录，避免 NSIS 把正在生成的安装包自己打进去
        Copy-Item $setupTemp (Join-Path $VersionDir $SetupExe) -Force
        $sw.Stop()
        Write-StepDone "$([int]$sw.Elapsed.TotalSeconds) 秒"
    }

    # ---------- 汇总 ----------
    $totalSw.Stop()
    Write-Host '  ──────────────────────────────────────────────────────────' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "  产物目录  .pro\$Version" -ForegroundColor Cyan
    $appFile = Get-Item (Join-Path $VersionDir $AppExe)
    Write-Host ('    ' + $appFile.Name + '  ' + (Format-Size $appFile.Length)) -ForegroundColor Gray
    if (Test-Path (Join-Path $VersionDir $SetupExe)) {
        $setupFile = Get-Item (Join-Path $VersionDir $SetupExe)
        Write-Host ('    ' + $setupFile.Name + '  ' + (Format-Size $setupFile.Length)) -ForegroundColor Gray
    }
    Write-Host ''
    Write-Host "  总用时 $([int]$totalSw.Elapsed.TotalSeconds) 秒" -ForegroundColor DarkGray
    if ($KeepStage) { Write-Info "中间文件保留在：$stageRoot" }
    Write-Host ''
}
catch {
    Write-Host ''
    Write-Host '  发布失败：' -ForegroundColor Red -NoNewline
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    $script:buildFailed = $true
}
finally {
    if ((Test-Path $stageRoot) -and -not $KeepStage) {
        try { Remove-Item $stageRoot -Recurse -Force } catch { }
    }
}

if ($script:buildFailed) { exit 1 }

if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') {
    Write-Host '  按任意键关闭…' -ForegroundColor DarkGray
    try { $null = [Console]::ReadKey($true) } catch { }
}
