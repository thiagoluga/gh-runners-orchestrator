#Requires -Version 7.2
<#
.SYNOPSIS
Adds a prepared Ubuntu VM to the runner pool as one more peer host (ADR 0013,
docs/new-runner-host.md). Run it from the admin PC.

.DESCRIPTION
Over SSH, with the admin key:
 1. reads the configuration of every existing host;
 2. runs setup-host.sh on the new VM (Docker, SSH by key, optional static address);
 3. copies the existing host's compose file (same pinned image), repository list and
    orchestrator settings to the new host, with its own name, slots and Docker group;
 4. copies the App key files host to host (never shown, never written on this PC);
 5. adds the new host to PoolHosts on the existing hosts and restarts them, before the new
    host starts, so no host in the old layout ever sweeps the new host's runners;
 6. starts the new host and checks /healthz on every host.
Running it again for the same host repeats only what is not already in place.

.EXAMPLE
./Add-RunnerHost.ps1 -Address 192.168.68.71 -Name runner-3 -Slots '0-5:16g' `
  -ExistingHosts 192.168.68.66,192.168.68.50 `
  -StaticIp 192.168.68.70/22 -Gateway 192.168.68.1 -Dns 192.168.68.1,192.168.15.1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Where the new VM answers SSH now (the installer's address).
    [Parameter(Mandatory)] [string] $Address,
    # The new host's pool name: 1-16 lower-case letters, digits or single hyphens.
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9](?:[a-z0-9]|-(?!-)){0,14}[a-z0-9]$|^[a-z0-9]$')] [string] $Name,
    # Slots as cpuset:memory separated by ';', for example '0-4:16g;5-9:16g'.
    [Parameter(Mandatory)] [ValidatePattern('^\d[\d,-]*:\d+[gm](;\d[\d,-]*:\d+[gm])*$')] [string] $Slots,
    # Addresses of the hosts already in the pool; the first one is the source of the settings.
    [Parameter(Mandatory)] [string[]] $ExistingHosts,
    [string] $StaticIp,
    [string] $Gateway,
    [string[]] $Dns,
    [string] $User = 'ci',
    [string] $Key = (Join-Path $HOME '.ssh/ci_vm_ed25519')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$here = $PSScriptRoot
$sshArgs = @('-i', $Key, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10')
$remoteDir = "/home/$User/gh-runners-orchestrator"

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Remote([string] $target, [string] $command) {
    $output = & ssh @sshArgs "$User@$target" $command
    if ($LASTEXITCODE -ne 0) { throw "ssh $target failed ($LASTEXITCODE): $command" }
    return $output
}

function Send-Text([string] $target, [string] $text, [string] $path) {
    $file = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($file, ($text -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        & scp @sshArgs -q $file "$($User)@$($target):$path"
        if ($LASTEXITCODE -ne 0) { throw "scp to $target failed" }
    }
    finally { Remove-Item $file -Force }
}

# Host-to-host copy through cmd's byte pipe: the data never lands on this PC or its console.
function Copy-Secret([string] $from, [string] $to, [string] $path) {
    $keyPath = (Resolve-Path $Key).Path
    $line = "ssh -i `"$keyPath`" -o BatchMode=yes $User@$from `"sudo cat $path`" | " +
        "ssh -i `"$keyPath`" -o BatchMode=yes $User@$to `"sudo install -m 0400 -o 1654 -g 1654 /dev/stdin $path`""
    & cmd /c $line 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Invoke-Remote $to "sudo rm -f $path" | Out-Null
        throw "copying $path from $from to $to failed"
    }
    $hashes = @($from, $to) | ForEach-Object { (Invoke-Remote $_ "sudo sha256sum $path | cut -c1-16") }
    if ($hashes[0] -ne $hashes[1]) {
        Invoke-Remote $to "sudo rm -f $path" | Out-Null
        throw "$path differs between $from and $to after the copy; removed it on $to"
    }
}

function ConvertTo-Slots([string] $spec) {
    $index = 0
    foreach ($part in $spec -split ';') {
        $cpuset, $memory = $part -split ':'
        $index++
        [ordered]@{ Name = "slot-$index"; Cpuset = $cpuset; Memory = $memory }
    }
}

Write-Step "Existing hosts"
$hosts = @(foreach ($existingAddress in $ExistingHosts) {
    $settings = (Invoke-Remote $existingAddress "cat $remoteDir/config/orchestrator.json") -join "`n" | ConvertFrom-Json -AsHashtable
    # PowerShell names are case-insensitive: never reuse $Name, $Key or $Address here.
    $hostName = $settings.Orchestrator['Host']
    if (-not $hostName) { $hostName = ((Invoke-Remote $existingAddress 'hostname') -join '').Trim().ToLowerInvariant() }
    [pscustomobject]@{ Address = $existingAddress; Name = $hostName; Settings = $settings }
    Write-Host "$existingAddress is $hostName"
})
if (($hosts | Select-Object -ExpandProperty Name) -contains $Name -and ($hosts | Select-Object -ExpandProperty Address) -notcontains $Address) {
    throw "$Name is already the name of $(($hosts | Where-Object Name -eq $Name).Address)"
}
$source = $hosts[0]
$pool = @($source.Settings.Orchestrator['PoolHosts'])
if ($pool.Count -eq 0) { $pool = @(($hosts | Select-Object -ExpandProperty Name)) }
foreach ($existing in $hosts) {
    if ($pool -notcontains $existing.Name) { throw "$($existing.Address) ($($existing.Name)) is not in PoolHosts $($pool -join ', ')" }
}
if ($pool -notcontains $Name) { $pool += $Name }
Write-Host "PoolHosts becomes: $($pool -join ', ')"

# The orchestrator refuses these names at start; catch them before any host restarts with them.
$accounts = @($source.Settings.Orchestrator['Accounts'] | ForEach-Object { $_['Owner'] }) + @($source.Settings.Orchestrator['Owner']) |
    Where-Object { $_ }
foreach ($poolName in $pool) {
    if ($poolName -cnotmatch '^[a-z0-9](?:[a-z0-9]|-(?!-)){0,14}[a-z0-9]$|^[a-z0-9]$') { throw "'$poolName' is not a valid pool host name" }
    if ($accounts | Where-Object { $_ -ieq $poolName }) { throw "'$poolName' is an account name; pick another host name" }
}
if ($StaticIp -and (-not $Gateway -or -not $Dns)) { throw '-StaticIp needs -Gateway and -Dns' }
if ($WhatIfPreference) {
    Write-Host "What if: prepare $Address as $Name with slots $Slots, copy settings and keys from $($source.Name),"
    Write-Host "restart $(($hosts | Where-Object { (@($_.Settings.Orchestrator['PoolHosts']) -join ',') -ne ($pool -join ',') } | Select-Object -ExpandProperty Name) -join ', ') with the new PoolHosts, then start $Name. Nothing was changed."
    return
}

Write-Step "Prepare the new VM ($Address)"
& scp @sshArgs -q (Join-Path $here 'setup-host.sh') "$($User)@$($Address):/tmp/setup-host.sh"
if ($LASTEXITCODE -ne 0) { throw "copying setup-host.sh to $Address failed" }
$setup = 'bash /tmp/setup-host.sh'
if ($StaticIp) { $setup += " --ip $StaticIp --gateway $Gateway --dns $($Dns -join ',')" }
& ssh @sshArgs "$User@$Address" "$setup < /dev/null"
$setupExit = $LASTEXITCODE
$target = if ($StaticIp) { ($StaticIp -split '/')[0] } else { $Address }
# 255 is ssh losing the session, expected only when netplan moves the VM to its new address.
if ($setupExit -ne 0 -and -not ($setupExit -eq 255 -and $target -ne $Address)) { throw "setup-host.sh failed on $Address ($setupExit)" }
if ($StaticIp -and $target -ne $Address) {
    Write-Host "Waiting for $target"
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-NetConnection $target -Port 22 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) { throw "$target does not answer SSH" }
        Start-Sleep -Seconds 5
    }
}
$gid = ((Invoke-Remote $target 'stat -c %g /var/run/docker.sock') -join '').Trim()

Write-Step "Settings for $Name"
# A copy: the source host's own settings are written back below and must keep its slots.
$settings = $source.Settings | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable
$settings.Orchestrator['Host'] = $Name
$settings.Orchestrator['PoolHosts'] = $pool
$settings.Orchestrator['Slots'] = @(ConvertTo-Slots $Slots)
Send-Text $target ($settings | ConvertTo-Json -Depth 10) "$remoteDir/config/orchestrator.json"
Send-Text $target ((Invoke-Remote $source.Address "cat $remoteDir/config/repositories.json") -join "`n") "$remoteDir/config/repositories.json"
$compose = (Invoke-Remote $source.Address "cat $remoteDir/compose.yaml") -join "`n"
Send-Text $target $compose "$remoteDir/compose.yaml"
$envFile = ((Invoke-Remote $source.Address "cat $remoteDir/.env") -join "`n") -replace '(?m)^DOCKER_GID=.*$', "DOCKER_GID=$gid"
Send-Text $target $envFile "$remoteDir/.env"

Write-Step "App keys"
$keyFiles = [regex]::Matches($compose + "`n" + $envFile, '/etc/gh-runners-orchestrator/[A-Za-z0-9._-]+\.pem') |
    Select-Object -ExpandProperty Value | Sort-Object -Unique
foreach ($path in $keyFiles) {
    $present = (Invoke-Remote $target "sudo test -s $path && echo yes || echo no") -join ''
    if ($present -eq 'yes') {
        $same = (Invoke-Remote $source.Address "sudo sha256sum $path | cut -c1-16") -eq (Invoke-Remote $target "sudo sha256sum $path | cut -c1-16")
        if ($same) { Write-Host "$path already there"; continue }
    }
    Copy-Secret $source.Address $target $path
    Write-Host "$path copied"
}

Write-Step "Add $Name to PoolHosts on the other hosts"
foreach ($existing in $hosts) {
    $current = @($existing.Settings.Orchestrator['PoolHosts'])
    if (($current -join ',') -eq ($pool -join ',') -and $existing.Settings.Orchestrator['Host'] -eq $existing.Name) {
        Write-Host "$($existing.Name) already lists the pool"
        continue
    }
    if (-not $PSCmdlet.ShouldProcess($existing.Name, 'update PoolHosts and restart its orchestrator')) { continue }
    $existing.Settings.Orchestrator['Host'] = $existing.Name
    $existing.Settings.Orchestrator['PoolHosts'] = $pool
    Invoke-Remote $existing.Address "cp -p $remoteDir/config/orchestrator.json $remoteDir/config/orchestrator.json.before-$Name 2>/dev/null || true" | Out-Null
    Send-Text $existing.Address ($existing.Settings | ConvertTo-Json -Depth 10) "$remoteDir/config/orchestrator.json"
    Invoke-Remote $existing.Address "cd $remoteDir && docker compose restart" | Out-Null
    Write-Host "$($existing.Name) restarted with the new pool"
}

Write-Step "Start $Name"
& ssh @sshArgs "$User@$target" "cd $remoteDir && docker compose config -q && docker compose up -d --force-recreate"
if ($LASTEXITCODE -ne 0) { throw "starting the orchestrator on $target failed" }

Write-Step "Health"
Start-Sleep -Seconds 45
$failed = 0
foreach ($healthTarget in @(($hosts | Select-Object -ExpandProperty Address)) + $target) {
    $health = (& ssh @sshArgs "$User@$healthTarget" 'curl -s -m 5 http://127.0.0.1:9464/healthz') -join ''
    Write-Host "$healthTarget /healthz: $health"
    if ($health -ne 'ok') { $failed++ }
}
if ($failed -gt 0) { throw "$failed host(s) not healthy; read 'docker compose logs' there" }
Write-Host "`n$Name is serving the pool." -ForegroundColor Green
