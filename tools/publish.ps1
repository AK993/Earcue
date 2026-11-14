<#
.SYNOPSIS
构建包含 .NET 运行时、本地 ASR 模型与空白用户配置的 Windows x64 免安装包。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.8',
    [string]$ModelSource = ''
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号格式应为 x.y.z' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'publish'
if ([string]::IsNullOrWhiteSpace($ModelSource)) {
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
$name = "Earcue-v$Version-win-x64"
$destination = Join-Path $publishRoot $name
$zipPath = "$destination.zip"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zipPath)) {
    throw "目标已存在，请先保留已有包或使用新的版本号：$destination"
}

$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Earcue.Speech\Asr\asr-models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($model in $manifest) {
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
}

& dotnet publish (Join-Path $repoRoot 'src\Earcue.App\Earcue.App.csproj') `
    -c Release -r win-x64 --self-contained true -o $destination `
    -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false "-p:Version=$Version" --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

New-Item -ItemType Directory -Path (Join-Path $destination 'models') -Force | Out-Null
foreach ($model in $manifest) {
    Copy-Item -LiteralPath (Join-Path $ModelSource $model.id) -Destination (Join-Path $destination 'models') -Recurse
}
# 发布包不读取已有用户的 Key、简历或日志；升级时可由用户复制原 config.json。
$config = @{
    asr = @{ modelRoot = 'models'; loopbackModel = 'sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23' }
    llm = @{ apiKeyProtected = '' }
    profile = @{ resume = '' }
    privacy = @{ saveTranscript = $false; diagnosticMode = $false }
}
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'config.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.txt') -Destination $destination

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$hash  $name.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
Write-Host "已生成：$destination"
Write-Host "已生成：$zipPath"
