<#
.SYNOPSIS
  下载并校验 Earcue 的本地流式 ASR 模型。

.DESCRIPTION
  模型清单与 SHA256 来自 src/Earcue.Speech/Asr/asr-models.json（与程序共用同一份清单）。
  默认安装到 %LOCALAPPDATA%\Earcue\models，程序启动时从该目录加载。
  下载使用 GitHub release 的官方地址，解压用 Windows 自带的 tar.exe（Win10 1803+）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\download-asr-models.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\download-asr-models.ps1 -Model all
#>
[CmdletBinding()]
param(
    [ValidateSet('zh', 'bilingual', 'all')]
    [string]$Model = 'zh',

    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Earcue\models'),

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\Earcue.Speech\Asr\asr-models.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "找不到模型清单: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$selected = switch ($Model) {
    'zh'        { @($manifest | Where-Object { $_.id -like '*zh-14M*' }) }
    'bilingual' { @($manifest | Where-Object { $_.id -like '*bilingual*' }) }
    'all'       { @($manifest) }
}

if ($selected.Count -eq 0) {
    throw "模型清单中没有找到 $Model 对应的条目"
}

$tempDir = Join-Path $env:TEMP 'EarcueModels'
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

$curl = Get-Command curl.exe -ErrorAction SilentlyContinue
$tar = Get-Command tar.exe -ErrorAction SilentlyContinue

foreach ($entry in $selected) {
    $id = [string]$entry.id
    $dest = Join-Path $InstallRoot $id
    $tokens = Join-Path $dest 'tokens.txt'
    $existingEncoders = @(Get-ChildItem -LiteralPath $dest -Filter 'encoder*.onnx' -File -ErrorAction SilentlyContinue)

    if (-not $Force -and (Test-Path -LiteralPath $tokens) -and $existingEncoders.Count -gt 0) {
        Write-Host "[skip] 已安装: $id" -ForegroundColor DarkGray
        continue
    }

    $sizeMb = if ($entry.sizeBytes -gt 0) { [math]::Round($entry.sizeBytes / 1MB, 1) } else { '?' }
    Write-Host ""
    Write-Host "[model] $($entry.displayName) ($id)" -ForegroundColor Cyan
    Write-Host "        下载约 $sizeMb MB，来源: $($entry.url)"
    if ([string]::IsNullOrWhiteSpace($entry.sha256) -or $entry.sha256 -eq 'PENDING_BILINGUAL_SHA256') {
        throw "模型清单缺少 SHA256，拒绝下载未校验的文件: $id"
    }

    $archive = Join-Path $tempDir ($id + '.tar.bz2')
    $needDownload = $true

    if (Test-Path -LiteralPath $archive) {
        $existingHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($existingHash -ieq $entry.sha256) {
            Write-Host "[reuse] 已有完整压缩包，跳过下载" -ForegroundColor DarkGray
            $needDownload = $false
        }
    }

    if ($needDownload) {
        if ($curl) {
            & $curl.Source -L -C - --fail --retry 10 --retry-delay 3 --connect-timeout 20 -o $archive $entry.url
            if ($LASTEXITCODE -ne 0) {
                throw "下载失败（curl exit $LASTEXITCODE）: $($entry.url)"
            }
        } else {
            Invoke-WebRequest -Uri $entry.url -OutFile $archive -UseBasicParsing
        }
    }

    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -ine $entry.sha256) {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        throw "SHA256 校验失败: $id`n  期望: $($entry.sha256)`n  实际: $actualHash"
    }

    Write-Host "[verify] SHA256 校验通过" -ForegroundColor Green

    if (-not $tar) {
        throw "找不到 tar.exe，无法解压 .tar.bz2。请安装 Windows 10 1803+，或先用 7-Zip 手动解压到 $dest"
    }

    if (Test-Path -LiteralPath $dest) {
        Remove-Item -LiteralPath $dest -Recurse -Force
    }

    Write-Host "[extract] 解压到 $InstallRoot"
    & $tar.Source -xjf $archive -C $InstallRoot
    if ($LASTEXITCODE -ne 0) {
        throw "解压失败（tar exit $LASTEXITCODE）: $archive"
    }

    if (-not (Test-Path -LiteralPath $tokens)) {
        throw "解压后没有找到 tokens.txt，请检查压缩包结构: $dest"
    }

    Remove-Item -LiteralPath $archive -Force
    Write-Host "[ok] $dest" -ForegroundColor Green
}

Write-Host ""
Write-Host "模型安装完成。程序默认从以下目录加载：" -ForegroundColor Cyan
Write-Host "  $InstallRoot"
Write-Host "如需换目录，修改 config.json 的 asr.modelRoot，或设置环境变量后重启程序。"
