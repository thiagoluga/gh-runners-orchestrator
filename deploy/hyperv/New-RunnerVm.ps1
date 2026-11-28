#Requires -Version 5.1
#Requires -RunAsAdministrator
#Requires -Modules Hyper-V
<#
.SYNOPSIS
Creates the Hyper-V VM for a new runner host, booting the Ubuntu 24.04 Server installer
(docs/new-runner-host.md). Run it in an elevated PowerShell on the Hyper-V machine.

.DESCRIPTION
Generation 2, Secure Boot with the UEFI CA template (needed by Ubuntu), fixed memory
(the slots' memory limits assume it), no automatic checkpoints, starts with Windows.
The VM boots the ISO; finish the installation as the runbook says.

.EXAMPLE
.\New-RunnerVm.ps1 -Name runner-3 -Cpus 12 -MemoryGB 32 -IsoPath D:\iso\ubuntu-24.04.3-live-server-amd64.iso
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]{1,16}$')] [string] $Name,
    [Parameter(Mandatory)] [ValidateRange(4, 64)] [int] $Cpus,
    [Parameter(Mandatory)] [ValidateRange(8, 256)] [int] $MemoryGB,
    [Parameter(Mandatory)] [ValidateScript({ Test-Path $_ -PathType Leaf })] [string] $IsoPath,
    [ValidateRange(64, 2048)] [int] $DiskGB = 200,
    # An External switch bound to the network the pool's hosts are on.
    [string] $SwitchName,
    [string] $Path = (Get-VMHost).VirtualMachinePath
)

$ErrorActionPreference = 'Stop'

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "A VM named $Name already exists." }
if (-not $SwitchName) {
    $external = @(Get-VMSwitch -SwitchType External)
    if ($external.Count -ne 1) { throw "Pass -SwitchName; external switches: $(($external.Name) -join ', ')" }
    $SwitchName = $external[0].Name
}
$host_ = Get-VMHost
if ($Cpus -gt $host_.LogicalProcessorCount) { throw "This machine has $($host_.LogicalProcessorCount) logical processors." }

$vhd = Join-Path $Path "$Name\Virtual Hard Disks\$Name.vhdx"
if ($PSCmdlet.ShouldProcess($Name, "create a VM with $Cpus vCPUs, $MemoryGB GiB, a $DiskGB GB disk on $SwitchName")) {
    New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath $vhd `
        -NewVHDSizeBytes ($DiskGB * 1GB) -SwitchName $SwitchName -Path $Path | Out-Null
    Set-VM -Name $Name -ProcessorCount $Cpus -StaticMemory -AutomaticCheckpointsEnabled $false `
        -CheckpointType Production -AutomaticStartAction Start -AutomaticStopAction ShutDown
    Set-VMFirmware -VMName $Name -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority
    $dvd = Add-VMDvdDrive -VMName $Name -Path $IsoPath -Passthru
    Set-VMFirmware -VMName $Name -FirstBootDevice $dvd
    Enable-VMIntegrationService -VMName $Name -Name 'Guest Service Interface'
    Start-VM -Name $Name
    $mac = (Get-VMNetworkAdapter -VMName $Name).MacAddress
    Write-Host "VM $Name started on the installer. MAC $mac (reserve an address for it, or set one in the installer)."
    Write-Host "Open the console with: vmconnect.exe localhost $Name"
}
