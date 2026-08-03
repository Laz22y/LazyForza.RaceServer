[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'all',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj'
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/development'))
$runtimes = if ($Runtime -eq 'all') {
    @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
} else {
    @($Runtime)
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

foreach ($targetRuntime in $runtimes) {
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "LazyForza.RaceServer-$targetRuntime"))
    $archive = "$publishDirectory.zip"
    $artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (!$publishDirectory.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        !$archive.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside $artifactRoot."
    }
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    dotnet publish $project `
        --configuration Release `
        --runtime $targetRuntime `
        --self-contained (!$FrameworkDependent) `
        --output $publishDirectory `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $targetRuntime."
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $publishDirectory 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE.txt')

    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path -Leaf $archive)" -Encoding ascii
    Write-Host "$targetRuntime  $hash  $archive"
}
