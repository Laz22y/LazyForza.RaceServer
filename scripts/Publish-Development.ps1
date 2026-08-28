[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [ValidatePattern('^\d{8}-dev\.\d+$')]
    [string]$CloudflareLabel,
    [ValidatePattern('^\d{8}-dev\.\d+$')]
    [string]$DevelopmentLabel,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$PackageVersion,
    [ValidateSet('development', 'release')]
    [string]$ArtifactChannel = 'development'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj'
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts/$ArtifactChannel"))
$runtimes = if ($Runtime -eq 'all') {
    @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
} else {
    @($Runtime)
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

foreach ($targetRuntime in $runtimes) {
    $packageLabel = if ($PackageVersion) {
        $PackageVersion
    } elseif ($DevelopmentLabel) {
        $DevelopmentLabel
    } elseif ($CloudflareLabel) {
        $CloudflareLabel
    } else {
        $null
    }
    $packageBaseName = if ($packageLabel) {
        "LazyForza.RaceServer-$packageLabel-$targetRuntime"
    } else {
        "LazyForza.RaceServer-$targetRuntime"
    }
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot $packageBaseName))
    $archive = "$publishDirectory.zip"
    $artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (!$publishDirectory.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        !$archive.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside $artifactRoot."
    }
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    $publishArguments = @(
        'publish', $project,
        '--configuration', 'Release',
        '--runtime', $targetRuntime,
        '--self-contained', (!$FrameworkDependent),
        '--output', $publishDirectory,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true')
    if ($PackageVersion) {
        $publishArguments += "-p:Version=$PackageVersion"
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $targetRuntime."
    }

    foreach ($runtimeDirectory in @('data', 'Logs', 'Recordings')) {
        $runtimePath = [System.IO.Path]::GetFullPath((Join-Path $publishDirectory $runtimeDirectory))
        if (!$runtimePath.StartsWith($publishDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to modify a path outside $publishDirectory."
        }
        if (Test-Path -LiteralPath $runtimePath) {
            Remove-Item -LiteralPath $runtimePath -Recurse -Force
        }
    }
    Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $publishDirectory 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE.txt')

    $forbidden = Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force |
        Where-Object {
            $_.PSIsContainer -and $_.Name -in @('data', 'Logs', 'Recordings') -or
            -not $_.PSIsContainer -and (
            $_.Extension -in @('.db', '.db-wal', '.db-shm', '.log', '.user') -or
                $_.Name -notin @('appsettings.json', 'web.config') -and
                $_.Name -match '(^|[._-])(settings|config)([._-]|$)'
            )
        }
    if ($forbidden) {
        throw "Package contains runtime or user-data candidates: $($forbidden.FullName -join ', ')"
    }

    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path -Leaf $archive)" -Encoding ascii
    Write-Host "$targetRuntime  $hash  $archive"
}

if ($CloudflareLabel -or $PackageVersion) {
    $cloudflarePackageLabel = if ($PackageVersion) { $PackageVersion } else { $CloudflareLabel }
    $cloudflareRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'cloudflare'))
    $cloudflareStage = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "_cloudflare-$cloudflarePackageLabel"))
    $cloudflareArchive = [System.IO.Path]::GetFullPath(
        (Join-Path $artifactRoot $(if ($PackageVersion) {
                    "LazyForza.RaceServer-$PackageVersion-Cloudflare.zip"
                } else {
                    "LazyForza.RaceServer-Cloudflare-$CloudflareLabel.zip"
                })))
    $artifactPrefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    foreach ($target in @($cloudflareStage, $cloudflareArchive)) {
        if (!$target.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to modify a path outside $artifactRoot."
        }
    }
    foreach ($target in @($cloudflareStage, $cloudflareArchive, "$cloudflareArchive.sha256")) {
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
    foreach ($directory in @('public', 'src', 'tests')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $cloudflareStage $directory) | Out-Null
    }
    foreach ($relative in @(
            'public/app.js', 'public/events.css', 'public/i18n.js', 'public/index.html', 'public/lazyforza-logo.png',
            'public/results.css', 'public/styles.css', 'public/teams.css',
            'src/index.ts', 'src/passwords.ts', 'src/protocol.ts', 'src/race-core.ts', 'src/rule-templates.ts', 'src/track-package.ts',
            'tests/passwords.test.ts', 'tests/race-core.test.ts', 'tests/rule-templates.test.ts', 'tests/track-package.test.ts', 'tests/web-localization.test.ts',
            'package-lock.json', 'package.json', 'README.md', 'tsconfig.json', 'wrangler.jsonc')) {
        $source = [System.IO.Path]::GetFullPath((Join-Path $cloudflareRoot $relative))
        if (!$source.StartsWith(
                $cloudflareRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
                [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Cloudflare development input is missing or outside its source root: $source"
        }
        $destination = Join-Path $cloudflareStage $relative
        Copy-Item -LiteralPath $source -Destination $destination
    }
    Set-Content -LiteralPath (Join-Path $cloudflareStage 'BUILDINFO.txt') -Encoding UTF8 -Value @(
        "LazyForza RaceServer Cloudflare $cloudflarePackageLabel"
        $(if ($PackageVersion) { 'Formal release' } else { 'Development preview - not a formal release' })
        "BuiltUtc: $([DateTimeOffset]::UtcNow.ToString('O'))"
    )
    Compress-Archive -Path (Join-Path $cloudflareStage '*') -DestinationPath $cloudflareArchive -CompressionLevel Optimal
    $cloudflareHash = (Get-FileHash -LiteralPath $cloudflareArchive -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$cloudflareArchive.sha256" `
        -Value "$cloudflareHash  $(Split-Path -Leaf $cloudflareArchive)" -Encoding ascii
    Remove-Item -LiteralPath $cloudflareStage -Recurse -Force
    Write-Host "cloudflare  $cloudflareHash  $cloudflareArchive"
}
