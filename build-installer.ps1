<#
    build-installer.ps1 - construit le programme d'installation de l'Editeur PDF.

    1. publie l'application en mode autonome (runtime .NET inclus : rien a installer en plus) ;
    2. compresse la publication ;
    3. compile Setup.exe (.NET Framework 4.7.2, integre a Windows) ;
    4. ajoute l'archive a la fin de Setup.exe.

    Resultat : dist\EditeurPDF-Setup-<version>.exe

    Utilisation :
        powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
        .\build-installer.ps1 -SkipPublish     (reutilise artifacts\app)
#>

[CmdletBinding()]
param(
    [switch] $SkipPublish,

    # Version a appliquer (par defaut : celle de PDFEditor.csproj).
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$project = Join-Path $root 'PDFEditor.csproj'
$setupProject = Join-Path $root 'Installer\Setup.csproj'
$artifacts = Join-Path $root 'artifacts'
$appDir = Join-Path $artifacts 'app'
$payload = Join-Path $artifacts 'app.zip'
$setupDir = Join-Path $artifacts 'setup'
$dist = Join-Path $root 'dist'

if (-not $Version) {
    [xml] $xml = Get-Content $project -Encoding UTF8
    $Version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { $Version = '1.0.0' }
}
$version = $Version

Write-Host "Editeur PDF $version" -ForegroundColor Cyan

if (-not $SkipPublish) {
    Write-Host '1/4 Publication autonome de l''application...' -ForegroundColor Cyan
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        "-p:Version=$version" -p:DebugType=none -p:DebugSymbols=false -o $appDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "La publication a echoue (code $LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $appDir 'PDFEditor.exe'))) {
    throw "Publication introuvable : $appDir"
}

Write-Host '2/4 Compression...' -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appDir, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host '3/4 Compilation du programme d''installation...' -ForegroundColor Cyan
if (Test-Path $setupDir) { Remove-Item $setupDir -Recurse -Force }
dotnet build $setupProject -c Release "-p:Version=$version" -o $setupDir --nologo
if ($LASTEXITCODE -ne 0) { throw "La compilation de Setup.exe a echoue (code $LASTEXITCODE)." }

$extra = Get-ChildItem $setupDir -File | Where-Object { $_.Extension -notin '.exe', '.config', '.pdb' }
if ($extra) {
    throw "Setup.exe depend de fichiers supplementaires : $($extra.Name -join ', ')"
}

Write-Host '4/4 Assemblage...' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dist | Out-Null
$output = Join-Path $dist "EditeurPDF-Setup-$version.exe"
$zipLength = (Get-Item $payload).Length

$stream = [IO.File]::Create($output)
try {
    $setupBytes = [IO.File]::ReadAllBytes((Join-Path $setupDir 'Setup.exe'))
    $stream.Write($setupBytes, 0, $setupBytes.Length)

    $zip = [IO.File]::OpenRead($payload)
    try { $zip.CopyTo($stream) } finally { $zip.Dispose() }

    $lengthBytes = [BitConverter]::GetBytes([int64] $zipLength)
    $stream.Write($lengthBytes, 0, $lengthBytes.Length)
    $magic = [Text.Encoding]::ASCII.GetBytes('PDFEDITOR-SETUP1')
    $stream.Write($magic, 0, $magic.Length)
}
finally {
    $stream.Dispose()
}

$sizeMb = [math]::Round((Get-Item $output).Length / 1MB, 1)
Write-Host "Programme d'installation : $output ($sizeMb Mo)" -ForegroundColor Green
