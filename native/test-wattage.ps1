# Manual verification harness for the AC-adapter-wattage RPC (TODO #41).
#
#   Run from an ELEVATED PowerShell (the RPC needs admin), in native\:
#       .\test-wattage.ps1
#
# Companion to test-read.ps1. Loads LenPower.dll and reads the connected charger's rated wattage
# via LenGetAcAdapterWattage (proc #45). rc=0 means the RPC call succeeded.
#
# Expected on a ThinkPad X1 Yoga Gen 7 with a 60 W USB-C adapter attached:
#   rc=0  capable=1  wattage=60      (whole watts, not milliwatts)
# On battery / no adapter the firmware reports capable=0.

$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'LenPower.dll'
if (-not (Test-Path $dll)) {
    Write-Host "LenPower.dll not found - run build.cmd first." -ForegroundColor Red
    return
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LenWattTest {
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("LenPower.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int LenGetAcAdapterWattage(int battery, out int capable, out int wattage);
}
'@

if ([LenWattTest]::LoadLibrary($dll) -eq [IntPtr]::Zero) {
    Write-Host "Failed to load LenPower.dll (LastError=$([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message))" -ForegroundColor Red
    return
}

$cap = 0; $w = 0
$rc = [LenWattTest]::LenGetAcAdapterWattage(1, [ref]$cap, [ref]$w)

Write-Host ""
Write-Host "LenGetAcAdapterWattage(battery=1) ->" -ForegroundColor Cyan
Write-Host ("  rc       = {0}  {1}" -f $rc, $(if ($rc -eq 0) { '(success)' } else { '(RPC error / driver missing / not elevated)' }))
Write-Host "  capable  = $cap"
Write-Host "  wattage  = $w"
Write-Host ""
if ($rc -eq 0 -and $cap -ne 0 -and $w -gt 0) { Write-Host "Charger rated $w W." -ForegroundColor Green }
elseif ($rc -eq 0)                           { Write-Host "No usable value (capable=$cap, wattage=$w) - on battery or unsupported." -ForegroundColor Yellow }
