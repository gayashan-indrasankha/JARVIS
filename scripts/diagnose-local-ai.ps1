[CmdletBinding()]
param(
    [string]$JarvisHome,
    [int]$Port = 18080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'config\local-model-manifest.json') -Raw |
    ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($JarvisHome)) { $JarvisHome = $env:JARVIS_HOME }
if ([string]::IsNullOrWhiteSpace($JarvisHome)) {
    $JarvisHome = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JARVIS'
}
if (-not [IO.Path]::IsPathFullyQualified($JarvisHome)) { throw 'JARVIS_HOME must be fully qualified.' }
$dataRoot = [IO.Path]::GetFullPath($JarvisHome).TrimEnd('\', '/')
if ($dataRoot -eq [IO.Path]::GetPathRoot($dataRoot).TrimEnd('\', '/')) {
    throw 'JARVIS_HOME must not be a filesystem root.'
}
if ($dataRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'JARVIS_HOME must be on a local filesystem volume.'
}
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be between 1 and 65535.' }
$ancestor = [IO.DirectoryInfo]::new($dataRoot)
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and
        ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'JARVIS_HOME must not traverse a reparse point.'
    }
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) {
        throw 'JARVIS_HOME must be outside every Git working tree.'
    }
    $ancestor = $ancestor.Parent
}

Write-Host "JARVIS data root: $dataRoot"
Write-Host "Architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
$computer = Get-CimInstance Win32_ComputerSystem
Write-Host ('System RAM: {0:N1} GiB' -f ($computer.TotalPhysicalMemory / 1GB))
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($null -ne $nvidiaSmi) {
    Write-Host "GPU: $(& $nvidiaSmi.Source --query-gpu=name,memory.total --format=csv,noheader 2>$null)"
} else {
    Write-Host 'GPU: NVIDIA tooling not detected.'
}

$checks = @(
    @{ Name = 'llama-server'; Path = Join-Path $dataRoot 'Runtime\LlamaCpp\llama-server.exe' },
    @{ Name = 'Qwen3 GGUF'; Path = Join-Path $dataRoot 'Models\Llm\Qwen3-4B-Q4_K_M.gguf' },
    @{ Name = 'Silero VAD'; Path = Join-Path $dataRoot 'Models\Vad\silero_vad.onnx' },
    @{ Name = 'Zipformer encoder'; Path = Join-Path $dataRoot 'Models\Speech\sherpa-onnx-streaming-zipformer-en-20M-2023-02-17\encoder-epoch-99-avg-1.int8.onnx' },
    @{ Name = 'Zipformer decoder'; Path = Join-Path $dataRoot 'Models\Speech\sherpa-onnx-streaming-zipformer-en-20M-2023-02-17\decoder-epoch-99-avg-1.onnx' },
    @{ Name = 'Zipformer joiner'; Path = Join-Path $dataRoot 'Models\Speech\sherpa-onnx-streaming-zipformer-en-20M-2023-02-17\joiner-epoch-99-avg-1.int8.onnx' },
    @{ Name = 'Zipformer tokens'; Path = Join-Path $dataRoot 'Models\Speech\sherpa-onnx-streaming-zipformer-en-20M-2023-02-17\tokens.txt' },
    @{ Name = 'Wake-word encoder'; Path = Join-Path $dataRoot 'Models\WakeWord\sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01\encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx' },
    @{ Name = 'Wake-word decoder'; Path = Join-Path $dataRoot 'Models\WakeWord\sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01\decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx' },
    @{ Name = 'Wake-word joiner'; Path = Join-Path $dataRoot 'Models\WakeWord\sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01\joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx' },
    @{ Name = 'Wake-word tokens'; Path = Join-Path $dataRoot 'Models\WakeWord\sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01\tokens.txt' },
    @{ Name = 'Kokoro model'; Path = Join-Path $dataRoot 'Models\Tts\kokoro-en-v0_19\model.onnx' },
    @{ Name = 'Kokoro voices'; Path = Join-Path $dataRoot 'Models\Tts\kokoro-en-v0_19\voices.bin' },
    @{ Name = 'Kokoro tokens'; Path = Join-Path $dataRoot 'Models\Tts\kokoro-en-v0_19\tokens.txt' },
    @{ Name = 'Kokoro phonemizer data'; Path = Join-Path $dataRoot 'Models\Tts\kokoro-en-v0_19\espeak-ng-data'; Type = 'Container' }
)
$missingAssets = 0
foreach ($check in $checks) {
    $pathType = if ($check.ContainsKey('Type')) { $check.Type } else { 'Leaf' }
    $present = Test-Path -LiteralPath $check.Path -PathType $pathType
    if (-not $present) { $missingAssets++ }
    Write-Host ("{0}: {1}" -f $check.Name, $(if ($present) { 'present' } else { 'MISSING' }))
}

$qwen = $manifest.models | Where-Object logicalId -eq 'qwen3-4b-q4-k-m'
$qwenPath = Join-Path $dataRoot "Models\Llm\$($qwen.expectedFilename)"
if (Test-Path -LiteralPath $qwenPath -PathType Leaf) {
    $valid = (Get-FileHash -LiteralPath $qwenPath -Algorithm SHA256).Hash -eq $qwen.sha256
    Write-Host "Qwen3 checksum: $(if ($valid) { 'valid' } else { 'FAILED' })"
    if (-not $valid) { throw 'Qwen3 checksum verification failed.' }
}

$deepQwen = $manifest.models | Where-Object logicalId -eq 'qwen3-8b-q4-k-m'
$deepQwenPath = Join-Path $dataRoot "Models\Llm\$($deepQwen.expectedFilename)"
if (Test-Path -LiteralPath $deepQwenPath -PathType Leaf) {
    $deepSizeValid = (Get-Item -LiteralPath $deepQwenPath).Length -eq [long]$deepQwen.expectedBytes
    $deepHashValid = (Get-FileHash -LiteralPath $deepQwenPath -Algorithm SHA256).Hash -eq
        $deepQwen.sha256
    Write-Host "Optional Qwen3 8B DEEP profile: $(if ($deepSizeValid -and $deepHashValid) { 'valid' } else { 'FAILED' })"
    if (-not $deepSizeValid -or -not $deepHashValid) {
        throw 'Optional Qwen3 8B checksum or size verification failed.'
    }
} else {
    Write-Host 'Optional Qwen3 8B DEEP profile: not installed (FAST remains fully functional)'
}

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:${Port}/health" -TimeoutSec 2 -NoProxy
    Write-Host "llama-server health on 127.0.0.1:${Port}: ready"
    $null = $health
} catch {
    Write-Host "llama-server health on 127.0.0.1:${Port}: not running or not ready"
}

Write-Host 'Diagnostics are local-only and do not persist hardware or identity data.'
if ($missingAssets -gt 0) {
    throw "$missingAssets required local AI asset(s) are missing. Run scripts\setup-local-ai.ps1."
}
