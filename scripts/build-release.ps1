<#
.SYNOPSIS
  Builds a release: self-contained win-x64 publish, portable zip, and MSI installer (WiX v5).
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = $props.Project.PropertyGroup.Version
$publish = Join-Path $root 'artifacts\publish'
$dist = Join-Path $root 'dist'
$work = Join-Path $root 'artifacts\wix'
Remove-Item $publish, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $dist, $work | Out-Null

Write-Host "== Tests =="
dotnet test src/DNSManagerPro.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; release aborted.' }

Write-Host "== Publish (self-contained, ReadyToRun) =="
dotnet publish src/DNSManagerPro.App -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host "== Portable zip =="
$portableDir = Join-Path $work 'portable\DNSManagerPro'
New-Item -ItemType Directory -Force $portableDir | Out-Null
Copy-Item "$publish\*" $portableDir -Recurse
Copy-Item (Join-Path $root 'LICENSE') $portableDir
Copy-Item (Join-Path $root 'README.md') $portableDir
$zip = Join-Path $dist "DNSManagerPro-$version-win-x64-portable.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $portableDir -DestinationPath $zip -CompressionLevel Optimal
Write-Host "  $zip"

if ($SkipInstaller) { return }

Write-Host "== Installer (MSI) =="
# Harvest the publish folder into a WiX fragment: one component per file, folders mirrored.
$ids = 0
function New-DirXml([string]$path, [int]$indent) {
    $pad = ' ' * $indent
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in Get-ChildItem $path -File | Sort-Object Name) {
        $script:ids++
        $n = $script:ids
        $src = $f.FullName -replace '&', '&amp;'
        $name = $f.Name -replace '&', '&amp;'
        if ($f.Name -eq 'DNSManagerPro.exe') {
            [void]$sb.AppendLine("$pad<Component Id=`"c$n`" Guid=`"*`"><File Id=`"MainExe`" Source=`"$src`" Name=`"$name`" KeyPath=`"yes`" /></Component>")
        } else {
            [void]$sb.AppendLine("$pad<Component Id=`"c$n`" Guid=`"*`"><File Id=`"f$n`" Source=`"$src`" Name=`"$name`" KeyPath=`"yes`" /></Component>")
        }
    }
    foreach ($d in Get-ChildItem $path -Directory | Sort-Object Name) {
        $script:ids++
        $dn = $script:ids
        [void]$sb.AppendLine("$pad<Directory Id=`"d$dn`" Name=`"$($d.Name)`">")
        [void]$sb.Append((New-DirXml $d.FullName ($indent + 2)))
        [void]$sb.AppendLine("$pad</Directory>")
    }
    return $sb.ToString()
}
# Components in subfolders must sit inside Directory elements; the top level goes straight into INSTALLFOLDER.
$body = New-DirXml $publish 6
$frag = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">
$body    </ComponentGroup>
  </Fragment>
</Wix>
"@
$files = Join-Path $work 'Files.wxs'
Set-Content -Path $files -Value $frag -Encoding UTF8

# MIT license as RTF for the installer's license page.
$licText = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}'
$rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ' + (($licText -split "`r?`n") -join '\par ') + '}'
$licenseRtf = Join-Path $work 'license.rtf'
Set-Content -Path $licenseRtf -Value $rtf -Encoding ASCII

$msi = Join-Path $dist "DNSManagerPro-$version-win-x64.msi"
$iconFile = Join-Path $root 'assets\app.ico'
$productWxs = Join-Path $root 'installer\Product.wxs'
wix build $productWxs $files -ext WixToolset.UI.wixext -arch x64 `
    -d "ProductVersion=$version" -d "IconFile=$iconFile" -d "LicenseRtf=$licenseRtf" -pdb (Join-Path $work "installer.wixpdb") -o $msi
if ($LASTEXITCODE -ne 0) { throw 'WiX build failed.' }
Write-Host "  $msi"

Write-Host "== SHA-256 =="
$sums = @(Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Extension -in '.zip', '.msi' } | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$h  $($_.Name)"
})
Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
$sums
