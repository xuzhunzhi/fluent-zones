# 一次生成三样产物（都在 dist\ 下）：
#   1. fluent-zones-v<版本>-win-x64.zip        便携版（自包含，解压即用）
#   2. FluentZones-<版本>-x64.msix             MSIX（商店上架 / GitHub 侧载）
#   3. FluentZones-<版本>-Setup-x64.exe        EXE 安装包（含「管理员」快捷方式）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
#   可选参数：
#     -Version 0.1.1        覆盖版本号（默认读 csproj）
#     -SkipInstall          跳过 Inno Setup 安装包
#     -SkipMsix             跳过 MSIX
#     -CertPath .\my.pfx -CertPassword xxx   给 MSIX 签名（GitHub 侧载需要）
#
param(
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$SkipInstall,
    [switch]$SkipMsix,
    [string]$CertPath = "",
    [string]$CertPassword = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-Dotnet {
    $userSdk = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $userSdk) { return $userSdk }
    return "dotnet"
}

function Invoke-Step([string]$title) {
    Write-Host ""
    Write-Host "==> $title" -ForegroundColor Cyan
}

$dotnet = Get-Dotnet
$project = "src\FluentZones.UI\FluentZones.UI.csproj"
$binDir = "src\FluentZones.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
$payload = "dist\FluentZones"

if (-not $Version) {
    $csproj = Get-Content $project -Raw
    $match = [regex]::Match($csproj, "<Version>([^<]+)</Version>")
    $Version = if ($match.Success) { $match.Groups[1].Value } else { "0.1.0" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+') {
    throw "版本号格式不对：$Version（需要形如 0.1.0）"
}

New-Item -ItemType Directory -Force -Path "dist" | Out-Null

# 1. 先正常 build 一次，拿到 .xbf；publish 不会带编译后的 XAML
if (-not $SkipPublish) {
    Invoke-Step "dotnet build（拿 XAML 资源）"
    & $dotnet build $project -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "build 失败" }

    # 2. 自包含 publish（.NET 运行时 + Windows App SDK 都带上）
    Invoke-Step "dotnet publish（自包含 win-x64）"
    if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
    & $dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true `
        -p:DebugType=none -p:DebugSymbols=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

    Copy-Item "$binDir\*.xbf" "$payload\" -Force

    # publish 会把 bin 的 runtimeconfig 改写成自包含，重编一次恢复，run.cmd 才能继续用
    & $dotnet build $project -c Release -p:Platform=x64 | Out-Null
}
elseif (-not (Test-Path $payload)) {
    throw "没有现成的 dist\FluentZones，去掉 -SkipPublish 先 publish 一次"
}

# 3. 便携版 zip
Invoke-Step "便携版 zip"
$zip = "dist\fluent-zones-v$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$payload\*" -DestinationPath $zip -Force
Write-Host "  $zip"

# 4. MSIX
if (-not $SkipMsix) {
    Invoke-Step "MSIX 打包"

    $stage = "dist\msix-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "$payload\*" $stage -Recurse
    Copy-Item "packaging\msix\AppxManifest.xml" $stage
    New-Item -ItemType Directory -Force -Path "$stage\Assets" | Out-Null
    Copy-Item "packaging\msix\Assets\*" "$stage\Assets\" -Force

    # 版本号写进 manifest（四段式）
    $manifestPath = Join-Path $stage "AppxManifest.xml"
    $manifest = Get-Content $manifestPath -Raw
    $manifest = $manifest -replace 'Version="\d+\.\d+\.\d+\.0"', "Version=`"$Version.0`""
    [System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

    $makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
        Sort-Object { try { [version](Split-Path (Split-Path $_.FullName -Parent) -Leaf) } catch { [version]"0.0" } } |
        Select-Object -Last 1 -ExpandProperty FullName
    if (-not $makeappx) { throw "找不到 makeappx.exe（需要 Windows SDK）" }

    $msix = "dist\FluentZones-$Version-x64.msix"
    if (Test-Path $msix) { Remove-Item $msix -Force }
    & $makeappx pack /d $stage /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败" }
    Remove-Item $stage -Recurse -Force
    Write-Host "  $msix"

    if ($CertPath) {
        Invoke-Step "MSIX 签名"
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object { try { [version](Split-Path (Split-Path $_.FullName -Parent) -Leaf) } catch { [version]"0.0" } } |
            Select-Object -Last 1 -ExpandProperty FullName
        if (-not $signtool) { throw "找不到 signtool.exe（需要 Windows SDK）" }
        & $signtool sign /fd SHA256 /f $CertPath /p $CertPassword $msix
        if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败" }
        Write-Host "  已签名：$msix"
    }
    else {
        Write-Host "  提示：未签名。商店上架由微软重新签名；GitHub 侧载需要自己签，见 docs/RELEASING.md" -ForegroundColor Yellow
    }
}

# 5. EXE 安装包（Inno Setup）
if (-not $SkipInstall) {
    Invoke-Step "EXE 安装包（Inno Setup）"
    $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" }
    if (-not (Test-Path $iscc)) {
        Write-Warning "没找到 Inno Setup（ISCC.exe）。先运行：winget install JRSoftware.InnoSetup，再重试。"
    }
    else {
        & $iscc "/DAppVersion=$Version" "packaging\installer\FluentZones.iss"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }
        Write-Host "  dist\FluentZones-$Version-Setup-x64.exe"
    }
}

Invoke-Step "完成"
Get-ChildItem dist -File | Where-Object { $_.Name -match "fluent-zones-v|FluentZones-$Version" } |
    Select-Object Name, @{n = '大小(MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
