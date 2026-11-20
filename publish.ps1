param(
    [switch]$Standalone,
    [switch]$All
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Running unit tests..." -ForegroundColor Cyan
dotnet test MouseDeck.sln -c Release -nologo

if (Test-Path "dist") { Remove-Item "dist" -Recurse -Force }
New-Item -ItemType Directory -Force -Path "dist" | Out-Null

$proj = "src\MouseDeck.App\MouseDeck.App.csproj"

if (-not $Standalone -or $All) {
    Write-Host "Publishing portable single-file MouseDeck.exe -> dist\MouseDeck.exe..." -ForegroundColor Cyan
    $portableDir = "dist\_portable"
    dotnet publish $proj `
        -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
        -o $portableDir -nologo
    Move-Item "$portableDir\MouseDeck.exe" "dist\MouseDeck.exe" -Force
    Remove-Item $portableDir -Recurse -Force
}

if ($Standalone -or $All) {
    Write-Host "Publishing standalone (self-contained) MouseDeck -> dist\MouseDeck-standalone.exe..." -ForegroundColor Cyan
    $standaloneDir = "dist\_standalone"
    dotnet publish $proj `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $standaloneDir -nologo
    $targetName = if ($All) { "MouseDeck-standalone.exe" } else { "MouseDeck.exe" }
    Move-Item "$standaloneDir\MouseDeck.exe" "dist\$targetName" -Force
    Remove-Item $standaloneDir -Recurse -Force
}

if ($All) {
    $hashes = Get-ChildItem "dist\*.exe" | ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$h  $($_.Name)"
    }
    [System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot "dist\SHA256SUMS.txt"), $hashes)
}

Get-ChildItem "dist\*.exe" | Select-Object FullName, @{N='SizeMB';E={[math]::Round($_.Length/1MB,2)}}
