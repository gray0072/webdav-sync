<#
.SYNOPSIS
    Smoke test of the CLI prototype against a real WebDAV server on a disposable test pair (spec 16, rollout step 2).

.DESCRIPTION
    Runs `wds probe`, then scenarios C (+ E), A and B (spec 7) in subfolders C, A, B of the test folders:
        <LocalRoot>\C  <->  <RemoteRoot>/C   local data to a missing cloud folder, then changes on both sides
        <LocalRoot>\A  <->  <RemoteRoot>/A   cloud data to an empty local folder
        <LocalRoot>\B  <->  <RemoteRoot>/B   union merge of two non-empty folders (conflict copy expected)
    Both test folders must exist and be empty. Pairs live in a separate data folder, not in the app's data\.
    Plans are approved automatically (--yes --accept-guards): use disposable data only.
    Local deletes go to the Recycle Bin (that path is tested too).

    The password is read from the WDS_PASSWORD environment variable only.

.EXAMPLE
    $env:WDS_PASSWORD = '<app password>'   # or set it as a user environment variable
    .\scripts\smoke-real.ps1 -Url https://webdav.cloud.mail.ru/ -User me@mail.ru
#>
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$User,
    [string]$LocalRoot = 'D:\CloudTest',
    [string]$RemoteRoot = '/webdavsync-test',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA ('wds-smoke\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [switch]$SkipProbe,
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent $PSScriptRoot
$RemoteRoot = '/' + $RemoteRoot.Trim('/')
$results = New-Object System.Collections.Generic.List[string]

function Fail([string]$message) { throw "FAIL: $message" }

function Step([string]$name) { Write-Host ''; Write-Host "=== $name" -ForegroundColor Cyan }

function Pass([string]$name) { $results.Add("PASS  $name"); Write-Host "PASS  $name" -ForegroundColor Green }

# ------------------------------------------------------------------ preflight

if ([string]::IsNullOrEmpty($env:WDS_PASSWORD)) { Fail 'Set the WebDAV app password in WDS_PASSWORD first.' }
$LocalRoot = [IO.Path]::GetFullPath($LocalRoot)
if ($LocalRoot.TrimEnd('\') -ieq 'D:\Cloud' -or (Test-Path (Join-Path $LocalRoot '.cloud.db'))) { Fail "$LocalRoot looks like the real sync folder; use a disposable test folder." }
if (-not (Test-Path $LocalRoot -PathType Container)) { Fail "Local test folder $LocalRoot does not exist." }
if (@(Get-ChildItem -LiteralPath $LocalRoot -Force).Count -gt 0) { Fail "Local test folder $LocalRoot is not empty." }

Add-Type -AssemblyName System.Net.Http
$baseUri = New-Object Uri ($Url.TrimEnd('/') + '/')
$http = New-Object System.Net.Http.HttpClient
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Basic',
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($User + ':' + $env:WDS_PASSWORD)))

function Get-DavUri([string]$path) {
    $segments = @($path.Split('/') | Where-Object { $_ } | ForEach-Object { [Uri]::EscapeDataString($_) })
    New-Object Uri ($baseUri, ($segments -join '/'))
}

function Invoke-Dav([string]$method, [string]$path, $body = $null, [hashtable]$headers = @{}) {
    $request = New-Object System.Net.Http.HttpRequestMessage ((New-Object System.Net.Http.HttpMethod $method), (Get-DavUri $path))
    if ($null -ne $body) { $request.Content = New-Object System.Net.Http.StringContent ($body, [Text.Encoding]::UTF8) }
    foreach ($key in $headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, $headers[$key]) }
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
}

# Names of the direct children of a remote folder, or $null if it does not exist.
function Get-DavChildren([string]$path) {
    $r = Invoke-Dav 'PROPFIND' $path '<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>' @{ Depth = '1' }
    if ($r.Status -eq 404) { return $null }
    if ($r.Status -ne 207) { Fail "PROPFIND $path returned $($r.Status)" }
    $xml = [xml]$r.Body
    $ns = New-Object Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace('d', 'DAV:')
    $self = $path.Trim('/')
    $names = foreach ($href in $xml.SelectNodes('//d:response/d:href', $ns)) {
        $decoded = [Uri]::UnescapeDataString(([Uri]::new($baseUri, $href.InnerText)).AbsolutePath).Trim('/')
        $basePath = [Uri]::UnescapeDataString($baseUri.AbsolutePath).Trim('/')
        if ($basePath -and $decoded.StartsWith($basePath)) { $decoded = $decoded.Substring($basePath.Length).Trim('/') }
        if ($decoded -ine $self) { $decoded.Split('/')[-1] }
    }
    , @($names)
}

function Get-DavText([string]$path) {
    $r = Invoke-Dav 'GET' $path
    if ($r.Status -ne 200) { Fail "GET $path returned $($r.Status)" }
    $r.Body
}

function Put-DavText([string]$path, [string]$text) {
    $r = Invoke-Dav 'PUT' $path $text
    if ($r.Status -notin 200, 201, 204) { Fail "PUT $path returned $($r.Status)" }
}

function New-DavFolder([string]$path) {
    $r = Invoke-Dav 'MKCOL' ($path.TrimEnd('/') + '/')
    if ($r.Status -notin 201, 405) { Fail "MKCOL $path returned $($r.Status)" }
}

function Remove-DavItem([string]$path) {
    $r = Invoke-Dav 'DELETE' $path
    if ($r.Status -notin 200, 204, 404) { Fail "DELETE $path returned $($r.Status)" }
}

$rootChildren = Get-DavChildren $RemoteRoot
if ($null -eq $rootChildren) { Fail "Remote test folder $RemoteRoot does not exist." }
if ($rootChildren.Count -gt 0) { Fail "Remote test folder $RemoteRoot is not empty: $($rootChildren -join ', ')" }

Step 'Build the CLI'
& dotnet build (Join-Path $repo 'src\WebDavSync.Cli') -c Release -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'build' }
$wdsDll = Join-Path $repo 'src\WebDavSync.Cli\bin\Release\net10.0\wds.dll'
$env:WDS_DATA_DIR = $DataDir
New-Item -ItemType Directory -Force $DataDir | Out-Null
Write-Host "Data folder (settings, logs, probe report): $DataDir"

function Invoke-Wds {
    $output = & dotnet $wdsDll @args
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host "    $_" }
    [pscustomobject]@{ Code = $code; Text = ($output -join "`n") }
}

function Sync-Pair([string]$pair) {
    $r = Invoke-Wds sync $pair --once --yes --accept-guards
    if ($r.Code -ne 0) { Fail "sync $pair exited with $($r.Code)" }
}

function Assert-UpToDate([string]$pair) {
    $r = Invoke-Wds plan $pair
    if ($r.Code -ne 0 -or $r.Text -notmatch 'Plan: nothing to do\.') { Fail "$pair is not up to date after sync" }
}

function Add-Pair([string]$name, [string]$local, [string]$remote) {
    New-Item -ItemType Directory -Force $local | Out-Null
    $r = Invoke-Wds pair add --name $name --local $local --url $Url --remote-root $remote --user $User
    if ($r.Code -ne 0) { Fail "pair add $name" }
}

function Write-LocalText([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}

function Assert-Equal($actual, $expected, [string]$what) {
    if ($actual -cne $expected) { Fail "$what`: expected '$expected', got '$actual'" }
}

# Cyrillic "Otchet" built from code points: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.
$special = (-join [char[]](0x041E, 0x0442, 0x0447, 0x0451, 0x0442)) + ' #1 + 50% & co.txt'

try {
    # -------------------------------------------------------------- probe
    if (-not $SkipProbe) {
        Step 'Probe (inside the remote test folder)'
        $r = Invoke-Wds probe --url $Url --user $User --root $RemoteRoot
        if ($r.Code -ne 0) { Fail 'probe' }
        Pass 'probe'
    }

    # -------------------------------------------------------------- C: local -> missing cloud folder
    Step 'Scenario C: local data to a missing cloud folder'
    $c = Join-Path $LocalRoot 'C'
    Write-LocalText "$c\a.txt" 'alpha'
    Write-LocalText "$c\Docs\$special" 'report'
    Write-LocalText "$c\remote-edit.txt" 'v1'
    Write-LocalText "$c\remote-delete.txt" 'to be deleted remotely'
    New-Item -ItemType Directory -Force "$c\Empty" | Out-Null
    $bytes = New-Object byte[] (5MB); (New-Object Random 42).NextBytes($bytes); [IO.File]::WriteAllBytes("$c\big.bin", $bytes)
    Add-Pair 'smoke-c' $c "$RemoteRoot/C"
    Sync-Pair 'smoke-c'
    $names = Get-DavChildren "$RemoteRoot/C"
    foreach ($n in 'a.txt', 'Docs', 'Empty', 'big.bin', '.webdavsync') { if ($names -notcontains $n) { Fail "remote C misses $n" } }
    Assert-Equal (Get-DavText "$RemoteRoot/C/Docs/$special") 'report' 'remote special-name file'
    Assert-UpToDate 'smoke-c'
    Pass 'scenario C'

    # -------------------------------------------------------------- E: changes on both sides
    Step 'Scenario E: changes on both sides'
    Write-LocalText "$c\a.txt" 'alpha edited locally'
    Rename-Item "$c\Docs" 'Docs2'
    Remove-Item "$c\big.bin"
    Write-LocalText "$c\new-local.txt" 'new local'
    Put-DavText "$RemoteRoot/C/remote-edit.txt" 'v2 from the cloud'
    Remove-DavItem "$RemoteRoot/C/remote-delete.txt"
    Put-DavText "$RemoteRoot/C/from-cloud.txt" 'from the cloud'
    Sync-Pair 'smoke-c'
    $names = Get-DavChildren "$RemoteRoot/C"
    foreach ($n in 'Docs2', 'new-local.txt') { if ($names -notcontains $n) { Fail "remote C misses $n" } }
    foreach ($n in 'Docs', 'big.bin') { if ($names -contains $n) { Fail "remote C still has $n" } }
    Assert-Equal (Get-DavText "$RemoteRoot/C/a.txt") 'alpha edited locally' 'remote a.txt'
    Assert-Equal ([IO.File]::ReadAllText("$c\remote-edit.txt")) 'v2 from the cloud' 'local remote-edit.txt'
    Assert-Equal ([IO.File]::ReadAllText("$c\from-cloud.txt")) 'from the cloud' 'local from-cloud.txt'
    if (Test-Path "$c\remote-delete.txt") { Fail 'local remote-delete.txt was not deleted' }
    Assert-UpToDate 'smoke-c'
    Pass 'scenario E (local move/edit/delete, remote edit/delete/create)'

    # -------------------------------------------------------------- A: cloud -> empty local folder
    Step 'Scenario A: cloud data to an empty local folder'
    New-DavFolder "$RemoteRoot/A"
    New-DavFolder "$RemoteRoot/A/Sub Folder"
    Put-DavText "$RemoteRoot/A/top.txt" 'top'
    Put-DavText "$RemoteRoot/A/Sub Folder/$special" 'nested'
    $a = Join-Path $LocalRoot 'A'
    Add-Pair 'smoke-a' $a "$RemoteRoot/A"
    Sync-Pair 'smoke-a'
    Assert-Equal ([IO.File]::ReadAllText("$a\top.txt")) 'top' 'local top.txt'
    Assert-Equal ([IO.File]::ReadAllText("$a\Sub Folder\$special")) 'nested' 'local nested file'
    Assert-UpToDate 'smoke-a'
    Pass 'scenario A'

    # -------------------------------------------------------------- B: union merge
    Step 'Scenario B: union merge of two non-empty folders'
    New-DavFolder "$RemoteRoot/B"
    Put-DavText "$RemoteRoot/B/same.txt" 'identical'
    Put-DavText "$RemoteRoot/B/diff.txt" 'remote version'
    Put-DavText "$RemoteRoot/B/remote-only.txt" 'r'
    $b = Join-Path $LocalRoot 'B'
    Write-LocalText "$b\same.txt" 'identical'
    Write-LocalText "$b\diff.txt" 'local version, longer'
    Write-LocalText "$b\local-only.txt" 'l'
    Add-Pair 'smoke-b' $b "$RemoteRoot/B"
    Sync-Pair 'smoke-b'
    $remoteNames = Get-DavChildren "$RemoteRoot/B"
    $conflict = @($remoteNames | Where-Object { $_ -like 'diff (conflict * cloud).txt' })
    if ($conflict.Count -ne 1) { Fail 'expected one remote conflict copy' }
    Assert-Equal (Get-DavText "$RemoteRoot/B/$($conflict[0])") 'remote version' 'remote conflict copy'
    Assert-Equal (Get-DavText "$RemoteRoot/B/diff.txt") 'local version, longer' 'remote diff.txt'
    Assert-Equal ([IO.File]::ReadAllText((Join-Path $b $conflict[0]))) 'remote version' 'local conflict copy'
    foreach ($n in 'local-only.txt', 'remote-only.txt', 'same.txt') { if ($remoteNames -notcontains $n -or -not (Test-Path "$b\$n")) { Fail "B: $n missing on a side" } }
    Assert-UpToDate 'smoke-b'
    Pass 'scenario B'
}
finally {
    Write-Host ''
    Write-Host '=== Summary' -ForegroundColor Cyan
    $results | ForEach-Object { Write-Host $_ }
    Write-Host "Logs and probe report: $DataDir"
    if ($Cleanup) {
        foreach ($sub in 'A', 'B', 'C') {
            Remove-DavItem "$RemoteRoot/$sub"
            $path = Join-Path $LocalRoot $sub
            if (Test-Path $path) { Remove-Item -Recurse -Force $path }
        }
        Write-Host 'Cleaned up the test folders (A, B, C on both sides).'
    }
}
