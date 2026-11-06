# ==================== 可配置项 ====================
$SoftwareName = '风灵月影宗藏宝阁'
$TargetFramework = 'net10.0-windows'
$RuntimeIdentifier = 'win-x64'
$InnoCompiler = 'D:\Apps\Inno Setup 6\ISCC.exe'
$VersionRecordName = 'version.txt'
$InstallerScriptName = 'publish.iss'
$PublishEstimateSeconds = 120 # 仅用于显示估算进度，不影响实际编译。
$PackageEstimateSeconds = 90 # 进度最多到 95%，程序成功退出后才显示 100%。
# ==================================================

$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$ProjectRoot = Join-Path $RepositoryRoot 'FlingTrainerX'
$ProjectFile = Join-Path $ProjectRoot 'FlingTrainerX.csproj'
$VersionRecord = Join-Path $PSScriptRoot $VersionRecordName
$InstallerScript = Join-Path $PSScriptRoot $InstallerScriptName
$TotalTimer = New-Object System.Diagnostics.Stopwatch

function Format-Elapsed {
    # 将耗时格式化为时分秒，供流程节点显示。
    param([Parameter(Mandatory = $true)][TimeSpan]$Elapsed)

    if ($Elapsed.TotalHours -ge 1) {
        return ('{0:00}:{1:00}:{2:00}' -f [math]::Floor($Elapsed.TotalHours), $Elapsed.Minutes, $Elapsed.Seconds)
    }
    return $Elapsed.ToString('mm\:ss')
}

function Invoke-ProcessWithProgress {
    # 启动外部程序，显示估算进度并返回退出码、日志和实际耗时。
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity,
        [Parameter(Mandatory = $true)][int]$EstimateSeconds
    )

    $Timer = [System.Diagnostics.Stopwatch]::StartNew()
    $StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $StartInfo.FileName = $FilePath
    $StartInfo.Arguments = $ArgumentList -join ' '
    $StartInfo.WorkingDirectory = $RepositoryRoot
    $StartInfo.UseShellExecute = $false
    $StartInfo.CreateNoWindow = $true
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    $StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $StartInfo.EnvironmentVariables['DOTNET_CLI_UI_LANGUAGE'] = 'zh-Hans'
    $Process = New-Object System.Diagnostics.Process
    $Process.StartInfo = $StartInfo
    try {
        [void]$Process.Start()
        $StdOutTask = $Process.StandardOutput.ReadToEndAsync()
        $StdErrTask = $Process.StandardError.ReadToEndAsync()
        while (-not $Process.WaitForExit(200)) {
            $Percent = [math]::Min(95, [math]::Max(1, [int](($Timer.Elapsed.TotalSeconds / [math]::Max(1, $EstimateSeconds)) * 95)))
            $Status = "估算进度 $Percent% · 已用时 $(Format-Elapsed $Timer.Elapsed)"
            Write-Progress -Activity $Activity -Status $Status -PercentComplete $Percent
        }
        $ExitCode = $Process.ExitCode
        $Output = ($StdOutTask.GetAwaiter().GetResult() + "`n" + $StdErrTask.GetAwaiter().GetResult()) -split '\r?\n'
        $Timer.Stop()
        if ($ExitCode -eq 0) {
            Write-Progress -Activity $Activity -Status "100% · 用时 $(Format-Elapsed $Timer.Elapsed)" -PercentComplete 100
        }
        return [pscustomobject]@{ ExitCode = $ExitCode; Output = $Output; Elapsed = $Timer.Elapsed }
    }
    catch {
        throw "执行程序 ${FilePath} 失败：$($_.Exception.Message)"
    }
    finally {
        Write-Progress -Activity $Activity -Completed
        $Process.Dispose()
    }
}

function Stop-WithError {
    # 打印错误后交由 publish.bat 统一暂停，避免重复要求按键。
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "`n[失败] $Message" -ForegroundColor Red
    Write-Host "[失败] 已用时 $(Format-Elapsed $TotalTimer.Elapsed)" -ForegroundColor Red
    exit 1
}

try {
    if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
        throw "找不到项目文件：$ProjectFile"
    }
    if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
        throw "找不到 Inno Setup 编译器：$InnoCompiler"
    }
    if (-not (Test-Path -LiteralPath $InstallerScript -PathType Leaf)) {
        throw "找不到 Inno Setup 脚本：$InstallerScript"
    }

    $PreviousVersion = '无'
    if (Test-Path -LiteralPath $VersionRecord -PathType Leaf) {
        $StoredVersion = (Get-Content -LiteralPath $VersionRecord -Raw -Encoding UTF8).Trim()
        if ($StoredVersion) { $PreviousVersion = $StoredVersion }
    }

    Write-Host "上一版本：$PreviousVersion"
    do {
        $NewVersionInput = Read-Host '请输入新版本号（格式 x.x.x，例如 1.2.3）'
        $NewVersion = if ($null -eq $NewVersionInput) { '' } else { $NewVersionInput.Trim() }
        if ($NewVersion -notmatch '^\d+\.\d+\.\d+$') {
            Write-Host '[提示] 版本号格式不正确，请按 x.x.x 输入。' -ForegroundColor Yellow
        }
    } while ($NewVersion -notmatch '^\d+\.\d+\.\d+$')

    $ConfirmationInput = Read-Host "确认构建版本 $NewVersion？(Y/N)"
    $Confirmation = if ($null -eq $ConfirmationInput) { '' } else { $ConfirmationInput.Trim() }
    if ($Confirmation -notmatch '^(?i:y|yes)$') {
        Write-Host '[取消] 未执行构建。'
        exit 0
    }

    $TotalTimer.Start()
    $VersionDirectory = Join-Path $PSScriptRoot $NewVersion
    $PublishDirectory = Join-Path $VersionDirectory 'publish'
    $InstallerBaseName = "$SoftwareName-安装包-$NewVersion"
    $InstallerOutput = Join-Path $VersionDirectory "$InstallerBaseName.exe"
    # 在线更新用的更新包：publish 目录原样打成一个 zip，客户端解压覆盖即可升级
    $UpdatePackageName = "$SoftwareName-更新包-$NewVersion.zip"
    $UpdatePackageOutput = Join-Path $VersionDirectory $UpdatePackageName
    New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null

    Write-Host '[1/4] 开始编译…'
    $PublishArguments = @(
        ('publish'), ('"' + $ProjectFile + '"'), '-c', 'Release', '-f', $TargetFramework,
        '-r', $RuntimeIdentifier, '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:PublishReadyToRun=true',
        # 在线更新按程序集版本判断新旧，这里必须把版本号写进 exe（否则永远是 csproj 里的那个）
        ('-p:Version=' + $NewVersion),
        '-o', ('"' + $PublishDirectory + '"')
    )
    $PublishResult = Invoke-ProcessWithProgress -FilePath 'dotnet.exe' -ArgumentList $PublishArguments -Activity '[1/4] 正在编译' -EstimateSeconds $PublishEstimateSeconds
    if ($PublishResult.ExitCode -ne 0) {
        $PublishDetails = ($PublishResult.Output | Select-Object -Last 8) -join [Environment]::NewLine
        throw "dotnet publish 失败，退出代码：$($PublishResult.ExitCode)`n$PublishDetails"
    }
    Write-Host "[1/4] 编译完成（用时 $(Format-Elapsed $PublishResult.Elapsed)）"

    Write-Host '[2/4] 开始打包…'
    $ExecutablePath = Join-Path $PublishDirectory "$SoftwareName.exe"
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "发布目录中未找到预期程序：$ExecutablePath"
    }
    $AppIconPath = Join-Path $ProjectRoot 'Assets\app.ico'
    $InnoArguments = @(
        ('"/DAppName=' + $SoftwareName + '"'),
        ('"/DAppVersion=' + $NewVersion + '"'),
        ('"/DPublishDir=' + $PublishDirectory + '"'),
        ('"/DOutputDir=' + $VersionDirectory + '"'),
        ('"/DInstallerBaseName=' + $InstallerBaseName + '"'),
        ('"/DAppIconPath=' + $AppIconPath + '"'),
        ('"' + $InstallerScript + '"')
    )
    $InstallerResult = Invoke-ProcessWithProgress -FilePath $InnoCompiler -ArgumentList $InnoArguments -Activity '[2/4] 正在打包' -EstimateSeconds $PackageEstimateSeconds
    if ($InstallerResult.ExitCode -ne 0) {
        $InstallerDetails = ($InstallerResult.Output | Select-Object -Last 8) -join [Environment]::NewLine
        throw "Inno Setup 打包失败，退出代码：$($InstallerResult.ExitCode)`n$InstallerDetails"
    }
    if (-not (Test-Path -LiteralPath $InstallerOutput -PathType Leaf)) {
        throw "打包命令已结束，但未生成安装包：$InstallerOutput"
    }
    Write-Host "[2/4] 打包完成（用时 $(Format-Elapsed $InstallerResult.Elapsed)）"

    Write-Host '[3/4] 正在生成更新包…'
    try {
        if ([IO.File]::Exists($UpdatePackageOutput)) { [IO.File]::Delete($UpdatePackageOutput) }

        # 用 ZipFile 而不是 Compress-Archive：快得多，也不会被非 ASCII 路径绕晕
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $PublishDirectory, $UpdatePackageOutput,
            [IO.Compression.CompressionLevel]::Optimal, $false)
    }
    catch {
        throw "生成更新包失败：$($_.Exception.Message)"
    }
    if (-not (Test-Path -LiteralPath $UpdatePackageOutput -PathType Leaf)) {
        throw "更新包未生成：$UpdatePackageOutput"
    }
    $UpdatePackageSize = [math]::Round((Get-Item -LiteralPath $UpdatePackageOutput).Length / 1MB, 1)
    Write-Host "[3/4] 更新包已生成（$UpdatePackageSize MB）"

    Set-Content -LiteralPath $VersionRecord -Value $NewVersion -Encoding UTF8
    Write-Host '[4/4] 版本记录已更新'
    Write-Host "安装包：$InstallerOutput"
    Write-Host "更新包：$UpdatePackageOutput"
    Write-Host "[提示] 把更新包上传到 Gitee 发行版附件，客户端才能在线更新"
    $TotalTimer.Stop()
    Write-Host "[完成] 全部流程完成，总耗时 $(Format-Elapsed $TotalTimer.Elapsed)"
    exit 0
}
catch {
    $TotalTimer.Stop()
    Stop-WithError -Message $_.Exception.Message
}

