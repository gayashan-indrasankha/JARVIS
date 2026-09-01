[CmdletBinding()]
param(
    [switch]$DownloadRuntime,
    [switch]$DownloadModels,
    [ValidateSet('cuda-12.4', 'cpu')]
    [string]$RuntimeVariant = 'cuda-12.4',
    [string]$JarvisHome
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'config\local-model-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($JarvisHome)) {
    $JarvisHome = $env:JARVIS_HOME
}
if ([string]::IsNullOrWhiteSpace($JarvisHome)) {
    $JarvisHome = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JARVIS'
}
if (-not [IO.Path]::IsPathFullyQualified($JarvisHome)) {
    throw 'JARVIS_HOME must be a fully qualified path.'
}
$dataRoot = [IO.Path]::GetFullPath($JarvisHome).TrimEnd('\', '/')
if ($dataRoot -eq [IO.Path]::GetPathRoot($dataRoot).TrimEnd('\', '/')) {
    throw 'JARVIS_HOME must not be a filesystem root.'
}
if ($dataRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'JARVIS_HOME must be on a local filesystem volume.'
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
    [Runtime.InteropServices.Architecture]::X64) {
    throw 'JARVIS local AI setup currently supports only Windows x64.'
}
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

$directories = @(
    'Models\Llm', 'Models\Speech', 'Models\Tts', 'Models\Vad', 'Models\WakeWord',
    'Runtime\LlamaCpp', 'Data', 'Logs', 'Cache', 'Cache\Downloads'
)
foreach ($relativeDirectory in $directories) {
    $directory = Join-Path $dataRoot $relativeDirectory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    $directoryInfo = Get-Item -LiteralPath $directory -Force
    if (($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "JARVIS data directory must not be a reparse point: $relativeDirectory"
    }
}

Write-Host "JARVIS data root: $dataRoot"
Write-Host "Windows architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
$computer = Get-CimInstance Win32_ComputerSystem
Write-Host ('System RAM: {0:N1} GiB' -f ($computer.TotalPhysicalMemory / 1GB))
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($null -ne $nvidiaSmi) {
    $gpuSummary = & $nvidiaSmi.Source --query-gpu=name,memory.total --format=csv,noheader 2>$null
    Write-Host "NVIDIA GPU: $gpuSummary"
} else {
    Write-Host 'NVIDIA GPU: not detected; use -RuntimeVariant cpu if CUDA is unavailable.'
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory)] [string]$Url,
        [Parameter(Mandatory)] [string]$Destination,
        [AllowNull()] [string]$Sha256,
        [long]$ExpectedBytes = 0
    )

    $downloadUri = [Uri]$Url
    $allowedHosts = @('github.com', 'huggingface.co')
    if ($downloadUri.Scheme -ne 'https' -or
        -not [string]::IsNullOrEmpty($downloadUri.UserInfo) -or
        $downloadUri.Host -notin $allowedHosts) {
        throw "The manifest download URL is outside the approved HTTPS hosts: $Url"
    }

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $sizeValid = $ExpectedBytes -le 0 -or
            (Get-Item -LiteralPath $Destination).Length -eq $ExpectedBytes
        $hashValid = [string]::IsNullOrWhiteSpace($Sha256) -or
            (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256
        if ($sizeValid -and $hashValid) {
            Write-Host "Already downloaded: $(Split-Path -Leaf $Destination)"
            return
        }
        throw "Existing download failed checksum verification: $Destination"
    }

    $partial = "$Destination.partial"
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }
    Write-Host "Downloading $(Split-Path -Leaf $Destination)..."
    try {
        Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
        if ($ExpectedBytes -gt 0 -and
            (Get-Item -LiteralPath $partial).Length -ne $ExpectedBytes) {
            throw "Downloaded file has an unexpected size: $(Split-Path -Leaf $Destination)."
        }
        if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
            $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
            if ($actual -ne $Sha256) {
                throw "Checksum verification failed for $(Split-Path -Leaf $Destination)."
            }
        } else {
            Write-Warning "No authoritative checksum is published for $(Split-Path -Leaf $Destination); authenticity was not cryptographically verified."
        }
        Move-Item -LiteralPath $partial -Destination $Destination
    } catch {
        if (Test-Path -LiteralPath $partial -PathType Leaf) {
            Remove-Item -LiteralPath $partial -Force
        }
        throw
    }
}

function Expand-VerifiedTarModel {
    param(
        [Parameter(Mandatory)] [string]$Archive,
        [Parameter(Mandatory)] [string]$ParentDirectory,
        [Parameter(Mandatory)] [string]$ExpectedDirectory,
        [Parameter(Mandatory)] [string[]]$RequiredRelativePaths
    )

    $destination = Join-Path $ParentDirectory $ExpectedDirectory
    if (Test-Path -LiteralPath $destination -PathType Container) {
        $missing = @($RequiredRelativePaths | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $destination $_))
        })
        if ($missing.Count -eq 0) {
            Write-Host "Already extracted: $ExpectedDirectory"
            return
        }
        throw "Existing model directory is incomplete: $destination"
    }

    $entries = @(& tar.exe -tf $Archive)
    if ($LASTEXITCODE -ne 0 -or $entries.Count -eq 0 -or $entries.Count -gt 10000) {
        throw "Cannot inspect model archive: $Archive"
    }
    $expectedPrefix = "$ExpectedDirectory/"
    foreach ($entry in $entries) {
        $normalized = $entry.Replace('\', '/')
        $segments = @($normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
        if ($normalized.StartsWith('/') -or
            -not $normalized.StartsWith($expectedPrefix, [StringComparison]::Ordinal) -or
            $segments -contains '..') {
            throw "Model archive contains an unsafe path: $entry"
        }
    }

    $verboseEntries = @(& tar.exe -tvf $Archive)
    if ($LASTEXITCODE -ne 0 -or
        $verboseEntries.Count -eq 0 -or
        $verboseEntries.Count -gt 10000) {
        throw "Cannot inspect model archive entry types: $Archive"
    }
    foreach ($entry in $verboseEntries) {
        if ($entry.Length -eq 0 -or $entry[0] -notin @('-', 'd')) {
            throw 'Model archive contains a link or unsupported entry type.'
        }
    }

    $stage = Join-Path $dataRoot ("Cache\setup-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        & tar.exe -xf $Archive -C $stage
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract model archive: $Archive" }
        $stagedDestination = Join-Path $stage $ExpectedDirectory
        foreach ($required in $RequiredRelativePaths) {
            if (-not (Test-Path -LiteralPath (Join-Path $stagedDestination $required))) {
                throw "Extracted model is missing required file: $required"
            }
        }
        $reparsePoint = Get-ChildItem -LiteralPath $stagedDestination -Recurse -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($null -ne $reparsePoint) {
            throw 'Extracted model contains an unsafe reparse point.'
        }
        Move-Item -LiteralPath $stagedDestination -Destination $destination
    } finally {
        if (Test-Path -LiteralPath $stage -PathType Container) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}

function Expand-ZipContents {
    param(
        [Parameter(Mandatory)] [string]$Archive,
        [Parameter(Mandatory)] [string]$Destination,
        [switch]$RequireServer
    )

    $stage = Join-Path $dataRoot ("Cache\setup-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        Expand-Archive -LiteralPath $Archive -DestinationPath $stage
        $server = Get-ChildItem -LiteralPath $stage -Filter 'llama-server.exe' -Recurse | Select-Object -First 1
        if ($RequireServer -and $null -eq $server) {
            throw 'The llama.cpp runtime archive does not contain llama-server.exe.'
        }
        $source = if ($null -ne $server) { $server.Directory.FullName } else { $stage }
        Get-ChildItem -LiteralPath $source | Copy-Item -Destination $Destination -Recurse -Force
    } finally {
        if ($stage.StartsWith((Join-Path $dataRoot 'Cache'), [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}

$downloads = Join-Path $dataRoot 'Cache\Downloads'
if ($DownloadRuntime) {
    $variant = $manifest.llamaCpp.variants.$RuntimeVariant
    $archive = Join-Path $downloads $variant.archive
    Get-VerifiedDownload $variant.url $archive $variant.sha256
    Expand-ZipContents $archive (Join-Path $dataRoot 'Runtime\LlamaCpp') -RequireServer
    if ($RuntimeVariant -eq 'cuda-12.4') {
        $cudaArchive = Join-Path $downloads $variant.cudaRuntimeArchive
        Get-VerifiedDownload $variant.cudaRuntimeUrl $cudaArchive $variant.cudaRuntimeSha256
        Expand-ZipContents $cudaArchive (Join-Path $dataRoot 'Runtime\LlamaCpp')
    }
}

if ($DownloadModels) {
    foreach ($model in $manifest.models) {
        switch ($model.kind) {
            'language-model' {
                $destination = Join-Path $dataRoot "Models\Llm\$($model.expectedFilename)"
                Get-VerifiedDownload $model.url $destination $model.sha256
            }
            'voice-activity-detection' {
                $destination = Join-Path $dataRoot "Models\Vad\$($model.expectedFilename)"
                Get-VerifiedDownload $model.url $destination $model.sha256
            }
            'speech-recognition' {
                $archive = Join-Path $downloads $model.expectedArchive
                $expectedBytes = if ($null -ne $model.PSObject.Properties['expectedBytes']) {
                    [long]$model.expectedBytes
                } else { 0 }
                Get-VerifiedDownload $model.url $archive $model.sha256 $expectedBytes
                Expand-VerifiedTarModel `
                    $archive `
                    (Join-Path $dataRoot 'Models\Speech') `
                    $model.expectedDirectory `
                    @('encoder-epoch-99-avg-1.int8.onnx', 'decoder-epoch-99-avg-1.onnx', 'joiner-epoch-99-avg-1.int8.onnx', 'tokens.txt')
            }
            'keyword-spotting' {
                $archive = Join-Path $downloads $model.expectedArchive
                Get-VerifiedDownload $model.url $archive $model.sha256 ([long]$model.expectedBytes)
                Expand-VerifiedTarModel `
                    $archive `
                    (Join-Path $dataRoot 'Models\WakeWord') `
                    $model.expectedDirectory `
                    @('encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx', 'decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx', 'joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx', 'tokens.txt')
            }
            'speech-synthesis' {
                $archive = Join-Path $downloads $model.expectedArchive
                Get-VerifiedDownload $model.url $archive $model.sha256
                Expand-VerifiedTarModel `
                    $archive `
                    (Join-Path $dataRoot 'Models\Tts') `
                    $model.expectedDirectory `
                    @('model.onnx', 'voices.bin', 'tokens.txt', 'espeak-ng-data')
            }
        }
    }
}

$installationRecord = [ordered]@{
    manifestVersion = $manifest.manifestVersion
    llamaCppVersion = $manifest.llamaCpp.version
    runtimeVariant = $RuntimeVariant
    approvedModelIds = @($manifest.models.logicalId)
    runtimeDownloadRequested = [bool]$DownloadRuntime
    modelDownloadsRequested = [bool]$DownloadModels
}
$installationRecord | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $dataRoot 'installed-components.json') -Encoding utf8

Write-Host ''
Write-Host 'Licenses include MIT, Apache-2.0, CC-BY-4.0 provenance, GPL-3.0 eSpeak NG data, and separate NVIDIA CUDA terms.'
Write-Host 'Review docs\security\third-party-licenses.md before packaging or redistribution.'
if (-not $DownloadRuntime -and -not $DownloadModels) {
    Write-Host 'No downloads were requested. Re-run with -DownloadRuntime and/or -DownloadModels.'
} else {
    Write-Host 'Requested local components are installed. Run scripts\diagnose-local-ai.ps1 next.'
}
