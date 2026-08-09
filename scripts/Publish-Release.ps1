[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [string]$ReleaseNotesPath,
    [string]$TagMessage
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'LazyForza.RaceServer.sln'
$notesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
$projectPath = Join-Path $repoRoot 'src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj'
$cloudflarePackagePath = Join-Path $repoRoot 'cloudflare/package.json'
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/release'))
$tag = "v$Version"
$repository = 'Laz22y/LazyForza.RaceServer'
$title = "LazyForza RaceServer $Version"

if (!(Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Release notes do not exist: $notesPath"
}
if ((git status --porcelain).Count -ne 0) {
    throw 'The RaceServer worktree must be clean before publication.'
}
if (git tag --list $tag) {
    throw "Local tag already exists: $tag"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
if ($projectVersion -ne $Version) {
    throw "Project version is $projectVersion, expected $Version."
}
$cloudflareVersion = (Get-Content -LiteralPath $cloudflarePackagePath -Raw | ConvertFrom-Json).version
if ($cloudflareVersion -ne $Version) {
    throw "Cloudflare package version is $cloudflareVersion, expected $Version."
}

& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
& dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
& dotnet test $solution -c Release --no-build --no-restore --logger 'console;verbosity=minimal'
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }

Push-Location (Join-Path $repoRoot 'cloudflare')
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    & npm run check
    if ($LASTEXITCODE -ne 0) { throw 'Cloudflare type check failed.' }
    & npm test
    if ($LASTEXITCODE -ne 0) { throw 'Cloudflare tests failed.' }
}
finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'Publish-Development.ps1') `
    -Runtime all `
    -PackageVersion $Version `
    -ArtifactChannel release
if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed.' }

$assets = @(
    'win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64' |
        ForEach-Object { Join-Path $releaseRoot "LazyForza.RaceServer-$Version-$_.zip" }
)
$assets += Join-Path $releaseRoot "LazyForza.RaceServer-$Version-Cloudflare.zip"
$assetsWithChecksums = @()
foreach ($asset in $assets) {
    foreach ($path in @($asset, "$asset.sha256")) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release asset is missing: $path"
        }
        $assetsWithChecksums += $path
    }
    $expected = ((Get-Content -LiteralPath "$asset.sha256" -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "Release checksum mismatch: $asset"
    }
}

$message = if ([string]::IsNullOrWhiteSpace($TagMessage)) { $title } else { $TagMessage.Trim() }
& git tag -a $tag -m $message
if ($LASTEXITCODE -ne 0) { throw 'Annotated tag creation failed.' }
& git push origin main
if ($LASTEXITCODE -ne 0) { throw 'RaceServer main push failed.' }
& git push origin $tag
if ($LASTEXITCODE -ne 0) { throw 'RaceServer tag push failed.' }
& gh release create $tag @assetsWithChecksums `
    --repo $repository `
    --title $title `
    --notes-file $notesPath `
    --verify-tag
if ($LASTEXITCODE -ne 0) { throw 'GitHub Release creation failed.' }

$remoteTag = git ls-remote origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0 -or !$remoteTag) { throw 'Remote tag verification failed.' }
$release = gh release view $tag --repo $repository --json url,tagName,assets | ConvertFrom-Json
if ($release.tagName -ne $tag -or $release.assets.Count -ne $assetsWithChecksums.Count) {
    throw 'GitHub Release asset verification failed.'
}

Write-Output "RELEASE=$($release.url)"
Write-Output "TAG=$tag"
foreach ($asset in $assets) {
    Write-Output "ASSET=$(Split-Path -Leaf $asset) SHA256=$((Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash)"
}
