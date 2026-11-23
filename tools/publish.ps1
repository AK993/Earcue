<#
.SYNOPSIS
构建包含 .NET 运行时、本地 ASR 模型与空白用户配置的 Windows x64 免安装包。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.11',
    [string]$ModelSource = '',
    [ValidateSet('Full', 'Lite', 'Both')]
    [string]$Edition = 'Both'
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号格式应为 x.y.z' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'publish'
if ($Edition -ne 'Lite' -and [string]::IsNullOrWhiteSpace($ModelSource)) {
    $localModels = Join-Path $repoRoot 'tools\models'
    if (Test-Path -LiteralPath $localModels -PathType Container) {
        $ModelSource = $localModels
    } else {
        $latest = Get-ChildItem -LiteralPath $publishRoot -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'models') -PathType Container } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { throw '未找到本地模型目录，请使用 -ModelSource 指定模型来源。' }
        $ModelSource = Join-Path $latest.FullName 'models'
    }
}
$editions = if ($Edition -eq 'Both') { @('Full', 'Lite') } else { @($Edition) }
foreach ($flavor in $editions) {
    $packageName = "Earcue-v$Version-win-x64" + $(if ($flavor -eq 'Lite') { '-Lite' } else { '' })
    $packageDirectory = Join-Path $publishRoot $packageName
    if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath "$packageDirectory.zip")) {
        throw "目标已存在，请使用新版本号或先整理已有包：$packageDirectory"
    }
}

$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Earcue.Speech\Asr\asr-models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($Edition -ne 'Lite') { foreach ($model in $manifest) {
    $modelDirectory = Join-Path $ModelSource $model.id
    if (-not (Test-Path -LiteralPath (Join-Path $modelDirectory 'tokens.txt'))) {
        throw "缺少模型 tokens.txt：$modelDirectory"
    }
    $prefixes = if ($model.modelType -eq 'Paraformer') { @('encoder', 'decoder') } else { @('encoder', 'decoder', 'joiner') }
    foreach ($prefix in $prefixes) {
        if (@(Get-ChildItem -LiteralPath $modelDirectory -Filter "$prefix*.onnx" -File -Recurse).Count -eq 0) {
            throw "模型不完整，缺少 $prefix*.onnx：$modelDirectory"
        }
    }
    foreach ($file in $model.files) {
        $filePath = Join-Path $modelDirectory $file.path
        if (-not (Test-Path -LiteralPath $filePath) -or (Get-FileHash -LiteralPath $filePath).Hash -ine $file.sha256) {
            throw "模型文件缺失或校验失败：$filePath"
        }
    }
} }

$baseName = "Earcue-v$Version-win-x64" + $(if ($Edition -eq 'Lite') { '-Lite' } else { '' })
$destination = Join-Path $publishRoot $baseName

& dotnet publish (Join-Path $repoRoot 'src\Earcue.App\Earcue.App.csproj') `
    -c Release -r win-x64 --self-contained true -o $destination `
    -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

if ($Edition -eq 'Both') {
    $liteDirectory = Join-Path $publishRoot "Earcue-v$Version-win-x64-Lite"
    New-Item -ItemType Directory -Path $liteDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $destination | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $liteDirectory -Recurse }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($flavor in $editions) {
    $name = "Earcue-v$Version-win-x64" + $(if ($flavor -eq 'Lite') { '-Lite' } else { '' })
    $target = Join-Path $publishRoot $name
    if ($flavor -eq 'Full') {
        foreach ($model in $manifest) {
            $sourceDirectory = [IO.Path]::GetFullPath((Join-Path $ModelSource $model.id))
            $targetDirectory = Join-Path $target "models\$($model.id)"
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            # Retain metadata, vocabulary and samples, but ship only one usable weight per component.
            Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse | Where-Object { $_.Extension -ne '.onnx' } | ForEach-Object {
                $relative = $_.FullName.Substring($sourceDirectory.Length).TrimStart('\', '/')
                $targetFile = Join-Path $targetDirectory $relative
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($targetFile)) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $targetFile
            }
            $prefixes = if ($model.modelType -eq 'Paraformer') { @('encoder','decoder') } else { @('encoder','decoder','joiner') }
            foreach ($prefix in $prefixes) {
                $weight = Get-ChildItem -LiteralPath $sourceDirectory -Filter "$prefix*.onnx" -File |
                    Sort-Object @{ Expression = { if ($_.Name -like '*.int8.onnx') { 0 } else { 1 } } }, Name | Select-Object -First 1
                if (-not $weight) { throw "缺少 $prefix 权重：$sourceDirectory" }
                Copy-Item -LiteralPath $weight.FullName -Destination $targetDirectory
            }
        }
    }
    # Create clean distributable configs before any local user settings are migrated.
    $config = @{
        asr = @{ modelRoot = $(if ($flavor -eq 'Full') { 'models' } else { '' }); loopbackModel = 'sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23'; hotwords = @(); hotwordsScore = 1.5 }
        llm = @{ apiKeyProtected = '' }
        profile = @{ resume = '' }
        mic = @{ captureForContext = $false }
        privacy = @{ saveTranscript = $false; diagnosticMode = $false }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'config.json') -Encoding UTF8
    @{ version=$Version; edition=$flavor; bundledModels=($flavor -eq 'Full') } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $target 'package-info.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.txt') -Destination $target
    $zipPath = "$target.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($target, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$hash  $name.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
    Write-Host "已生成 $flavor：$target"
    Write-Host "ZIP：$zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)) MiB)"
}
