<#
    run.ps1 - compile l'Editeur PDF puis lance l'application.

    Utilisation :
        powershell -ExecutionPolicy Bypass -File .\run.ps1
        .\run.ps1 -Configuration Debug
        .\run.ps1 -NoLaunch                 (compilation seule)
        .\run.ps1 -File C:\chemin\doc.pdf   (ouvre directement un document)
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoLaunch,

    [string] $File
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$project = Join-Path $root 'PDFEditor.csproj'

if (-not (Test-Path $project)) {
    Write-Error "Projet introuvable : $project"
    exit 1
}

Write-Host "Compilation de l'Editeur PDF ($Configuration)..." -ForegroundColor Cyan

dotnet build $project -c $Configuration --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "La compilation a echoue (code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$exe = Join-Path $root ('bin\{0}\net10.0-windows10.0.19041.0\win-x64\PDFEditor.exe' -f $Configuration)

if (-not (Test-Path $exe)) {
    Write-Error "Executable introuvable : $exe"
    exit 1
}

if ($NoLaunch) {
    Write-Host "Compilation terminee : $exe" -ForegroundColor Green
    exit 0
}

Write-Host "Lancement de $exe" -ForegroundColor Green
if ($File) {
    Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f (Resolve-Path $File)) -WorkingDirectory (Split-Path -Parent $exe)
}
else {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
}
