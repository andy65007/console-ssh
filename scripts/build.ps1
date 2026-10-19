param(
    [string]$DotnetExecutable = "dotnet",
    [string]$Configuration = "Release",
    [string]$Version = "1.0.1"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\ConsoleSsh\ConsoleSsh.csproj"
$distRoot = Join-Path $repoRoot "dist"
$outputDirectory = Join-Path $distRoot "ConsoleSsh"
$launcherSource = Join-Path $repoRoot "src\ConsoleSsh\launcher\launcher.c"
$launcherBinary = Join-Path $repoRoot "src\ConsoleSsh\launcher\console-ssh-launcher.exe"

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version must not be empty."
}

$resolvedDistRoot = [System.IO.Path]::GetFullPath($distRoot)
if (-not $resolvedDistRoot.StartsWith([System.IO.Path]::GetFullPath($repoRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a distribution path outside the repository: $resolvedDistRoot"
}

if (Test-Path -LiteralPath $outputDirectory) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($outputDirectory)
    if (-not $resolvedOutput.StartsWith($resolvedDistRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected directory: $resolvedOutput"
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

$gcc = Get-Command gcc -ErrorAction SilentlyContinue
if ($gcc) {
    Write-Host "Building native launcher with $($gcc.Source)"
    & $gcc.Source -O2 -static -s -municode -Wall -Wextra -o $launcherBinary $launcherSource
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher compilation failed."
    }
}
elseif (-not (Test-Path -LiteralPath $launcherBinary)) {
    throw "gcc is unavailable and the prebuilt launcher is missing."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$nugetConfig = Join-Path $repoRoot "NuGet.Config"
& $DotnetExecutable restore $project --configfile $nugetConfig --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}
& $DotnetExecutable publish $project -c $Configuration -o $outputDirectory -p:Version=$Version --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$dotnetPath = (Get-Command $DotnetExecutable -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnetPath
$fxrRoot = Join-Path $dotnetRoot "host\fxr"
$runtimeRoot = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App"
if (-not (Test-Path -LiteralPath $fxrRoot) -or -not (Test-Path -LiteralPath $runtimeRoot)) {
    throw "Unable to locate host/fxr or shared runtime under $dotnetRoot."
}

$fxrVersion = Get-ChildItem -LiteralPath $fxrRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "hostfxr.dll") } |
    Sort-Object Name -Descending |
    Select-Object -First 1
$runtimeVersion = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
    Where-Object { $_.Name -like "10.*" } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $fxrVersion -or -not $runtimeVersion) {
    throw "Unable to locate a compatible .NET runtime."
}

$bundledRuntime = Join-Path $outputDirectory "runtime"
$bundledFXR = Join-Path $bundledRuntime "host\fxr\$($fxrVersion.Name)"
$bundledFramework = Join-Path $bundledRuntime "shared\Microsoft.NETCore.App\$($runtimeVersion.Name)"
New-Item -ItemType Directory -Path $bundledFXR -Force | Out-Null
New-Item -ItemType Directory -Path $bundledFramework -Force | Out-Null
Copy-Item -Path (Join-Path $fxrVersion.FullName "*") -Destination $bundledFXR -Force
Copy-Item -Path (Join-Path $runtimeVersion.FullName "*") -Destination $bundledFramework -Force

Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\install.ps1") -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\uninstall.ps1") -Destination $outputDirectory -Force

Get-ChildItem -LiteralPath $distRoot -Filter "console-ssh-*-win-x64.zip" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
$archive = Join-Path $distRoot "console-ssh-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $outputDirectory "*") -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Build complete: $outputDirectory (version $Version)"
Write-Host "Archive: $archive"
