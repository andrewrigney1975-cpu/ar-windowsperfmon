<#
.SYNOPSIS
    Builds the release version of Detective: Native AOT, self-contained, English only, unused runtime parts removed.
.DESCRIPTION
    Output: publish\Detective\ (run Detective.exe from there) and publish\Detective-win-x64.zip.
    Uses the Visual Studio 2026 MSBuild on F:. Native AOT links with the MSVC toolchain, which needs
    vswhere.exe on PATH; a VS developer prompt has it, a plain shell doesn't, so it's added here.
#>
$ErrorActionPreference = 'Stop'
$msbuild = 'F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

$root = $PSScriptRoot
$out = Join-Path $root 'publish\Detective'
$zip = Join-Path $root 'publish\Detective-win-x64.zip'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $msbuild (Join-Path $root 'src\Detective\Detective.csproj') -restore -t:Publish `
    -p:Configuration=Release -p:Platform=x64 "-p:PublishDir=$out\" -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal

$files = Get-ChildItem $out -Recurse -File
"{0} files, {1:N1} MB -> {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $out
"zip: {0:N1} MB -> {1}" -f ((Get-Item $zip).Length / 1MB), $zip
