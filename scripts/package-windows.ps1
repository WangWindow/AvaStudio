param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$InstallerPlatform = "",
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ProjectPath = Join-Path $RepoRoot "AvaStudio.Desktop\AvaStudio.Desktop.csproj"
$BuildPropsPath = Join-Path $RepoRoot "Directory.Build.props"
$WixProjPath = Join-Path $RepoRoot "scripts\windows\AvaStudio.wixproj"

# 版本号默认取自根目录 Directory.Build.props（唯一来源）
if (-not $Version) {
    [xml]$BuildPropsXml = Get-Content -LiteralPath $BuildPropsPath
    $versionNode = $BuildPropsXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ($null -ne $versionNode) {
        $Version = $versionNode.InnerText.Trim()
    }
}

if (-not $Version) {
    throw "Version is not defined. Pass -Version or define it in Directory.Build.props."
}

# RID -> MSI 安装平台映射
if (-not $InstallerPlatform) {
    $InstallerPlatform = switch ($Rid) {
        "win-x64" { "x64" }
        "win-arm64" { "arm64" }
        default { throw "Unsupported Windows RID for MSI packaging: $Rid" }
    }
}

$ArtifactRoot = Join-Path $RepoRoot "artifacts\package\$Rid\$Version"
$PublishDir = Join-Path $ArtifactRoot "publish"
$ZipPath = Join-Path $ArtifactRoot "AvaStudio-$Version-$Rid.zip"
$MsiPath = Join-Path $ArtifactRoot "AvaStudio-$Version-$Rid.msi"

# 发布输出必须包含的文件（缺失即失败，避免产出不完整的安装包）
$RequiredPublishFiles = @(
    "AvaStudio.exe",
    "libmpv\$Rid\libmpv-2.dll"
)

# 发布产物中的可执行文件名：Desktop 项目的程序集标识必须保持 AvaStudio.Desktop，
# 否则会与核心库 AvaStudio.dll 撞名；但安装/命令行使用 AvaStudio.exe。
# 原生 AOT 镜像本身自包含（无托管依赖清单），发布后直接重命名即可，因此不做程序集改名。
$PublishedExeName = "AvaStudio.Desktop.exe"
$InstalledExeName = "AvaStudio.exe"

$LibMpvSource = Join-Path $RepoRoot "runtimes\libmpv\$Rid\libmpv-2.dll"

function Invoke-Step {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $FilePath $($Arguments -join ' ')"
    }
}

# 把发布目录里的 AvaStudio.Desktop.exe 规范化为 AvaStudio.exe（安装包与 ZIP 中只保留这一个 exe）
function Rename-PublishedExecutable {
    param([string]$Directory)

    $legacyExe = Join-Path $Directory $PublishedExeName
    $installedExe = Join-Path $Directory $InstalledExeName

    if (Test-Path -LiteralPath $legacyExe) {
        if (Test-Path -LiteralPath $installedExe) {
            Remove-Item -LiteralPath $installedExe -Force
        }

        Move-Item -LiteralPath $legacyExe -Destination $installedExe
    }
}

if (-not $SkipPublish) {
    # 应用运行时会锁定 exe，导致发布失败，先做明确提示
    $running = Get-Process -Name "AvaStudio.Desktop" -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        throw "AvaStudio.Desktop is running (PID: $($running.Id -join ', ')). Close it before packaging."
    }

    if (-not (Test-Path -LiteralPath $LibMpvSource)) {
        throw "libmpv native library not found: $LibMpvSource. Place the runtime files under runtimes\libmpv\$Rid\."
    }

    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

    Invoke-Step dotnet @(
        "restore",
        $ProjectPath,
        "-r", $Rid,
        "-p:EnableWindowsTargeting=true"
    )

    # Release + 自包含 + PublishAot（由 csproj 的 Release 配置打开）
    Invoke-Step dotnet @(
        "publish",
        $ProjectPath,
        "-c", $Configuration,
        "-r", $Rid,
        "--self-contained", "true",
        "-p:EnableWindowsTargeting=true",
        "-p:Version=$Version",
        "-o", $PublishDir
    )
}

# 无论是否 -SkipPublish，都统一把发布目录规范化为 AvaStudio.exe（只保留一个 exe）
if (-not (Test-Path -LiteralPath $PublishDir)) {
    throw "Publish directory not found: $PublishDir. Run without -SkipPublish first."
}

Rename-PublishedExecutable -Directory $PublishDir

foreach ($required in $RequiredPublishFiles) {
    $requiredPath = Join-Path $PublishDir $required
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Publish output is incomplete: missing '$required' in $PublishDir."
    }
}

# 打包前移除调试符号
if (Test-Path -LiteralPath $PublishDir) {
    Get-ChildItem -Path $PublishDir -Recurse -Filter '*.pdb' -File | Remove-Item -Force
}

New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force
    Write-Host "ZIP built: $ZipPath"
}

if (-not $SkipMsi) {
    if (-not (Test-Path -LiteralPath $WixProjPath)) {
        Write-Warning "WiX project not found at $WixProjPath. Skipping MSI build."
    }
    else {
        Write-Host "Building MSI with WiX SDK (wixproj)..."
        $wixBuildDir = Join-Path $ArtifactRoot "wix-build"

        Invoke-Step dotnet @(
            "build",
            $WixProjPath,
            "-c", "Release",
            "-p:AppPublishDir=$PublishDir",
            "-p:AppVersion=$Version",
            "-p:InstallerPlatform=$InstallerPlatform",
            "-p:OutputPath=$wixBuildDir"
        )

        $builtMsi = Get-ChildItem -Path $wixBuildDir -Filter "*.msi" -Recurse | Select-Object -First 1
        if ($null -eq $builtMsi) {
            throw "MSI was not found in WiX build output: $wixBuildDir"
        }

        Copy-Item -LiteralPath $builtMsi.FullName -Destination $MsiPath -Force
        Remove-Item -LiteralPath $wixBuildDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "MSI built: $MsiPath"
    }
}

Write-Host ""
Write-Host "Artifacts written to: $ArtifactRoot"
if (Test-Path -LiteralPath $ZipPath) {
    Write-Host "  ZIP : $ZipPath"
}
if (Test-Path -LiteralPath $MsiPath) {
    Write-Host "  MSI : $MsiPath"
}
