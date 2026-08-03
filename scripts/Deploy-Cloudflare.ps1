[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,62}$')]
    [string]$WorkerName = 'lazyforza-race-server',
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$env:WRANGLER_SEND_METRICS = 'false'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw '请使用 PowerShell 7 或更高版本运行此脚本。'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$cloudflareRoot = Join-Path $repoRoot 'cloudflare'
$npm = Get-Command npm -ErrorAction Stop
$npx = Get-Command npx -ErrorAction Stop
$node = Get-Command node -ErrorAction Stop

$nodeVersionText = (& $node.Source --version).Trim().TrimStart('v')
$nodeMajor = [int]($nodeVersionText.Split('.')[0])
if ($nodeMajor -lt 20) {
    throw "当前 Node.js 为 $nodeVersionText；Wrangler 需要 Node.js 20 或更高版本。"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable 执行失败，退出码 $LASTEXITCODE。"
    }
}

function Read-RequiredSecret {
    param(
        [Parameter(Mandatory)] [string]$Prompt,
        [int]$MinimumLength = 8
    )
    while ($true) {
        $secret = Read-Host $Prompt -AsSecureString
        if ($secret.Length -ge $MinimumLength -and $secret.Length -le 128) { return $secret }
        Write-Warning "密码需要 $MinimumLength–128 个字符，请重新输入。"
    }
}

function Set-WranglerSecret {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [Security.SecureString]$Secret
    )
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try {
        $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $plainText | & $npx.Source wrangler secret put $Name --name $WorkerName --config wrangler.jsonc
        if ($LASTEXITCODE -ne 0) {
            throw "写入 Cloudflare Secret $Name 失败。"
        }
    }
    finally {
        if ($null -ne $plainText) { $plainText = $null }
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

Push-Location $cloudflareRoot
try {
    if (!$SkipInstall) {
        Invoke-Checked $npm.Source @('install', '--no-audit', '--no-fund')
    }
    Invoke-Checked $npm.Source @('run', 'check')
    Invoke-Checked $npm.Source @('test')

    & $npx.Source wrangler whoami --config wrangler.jsonc
    if ($LASTEXITCODE -ne 0) {
        Invoke-Checked $npx.Source @('wrangler', 'login')
    }

    $playerPassword = Read-RequiredSecret '设置车手登录密码（可留空）' -MinimumLength 0
    if ($playerPassword.Length -gt 0) {
        $adminPassword = Read-RequiredSecret '设置赛事总控密码（不要与车手密码相同）'
        Set-WranglerSecret 'PLAYER_PASSWORD' $playerPassword
        Set-WranglerSecret 'ADMIN_PASSWORD' $adminPassword
    }
    Invoke-Checked $npx.Source @('wrangler', 'deploy', '--name', $WorkerName, '--config', 'wrangler.jsonc')
    Write-Host "Cloudflare 地产赛事服务已部署：$WorkerName"
    if ($playerPassword.Length -eq 0) {
        Write-Host '未预设车手密码。请立即打开部署后的网页完成首次设置。'
    }
}
finally {
    Pop-Location
}
