param(
    [Parameter(Mandatory = $true)]
    [string]$RDR2GameDir = "D:\Red Dead Redemption 2",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "EnRAGECompanionsExtension.csproj"
if (-not (Test-Path (Join-Path $RDR2GameDir "ScriptHookRDRNetAPI.dll"))) {
    throw "ScriptHookRDRNetAPI.dll was not found in: $RDR2GameDir"
}
$msbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) { $msbuild = Get-Command msbuild -ErrorAction SilentlyContinue }
if (-not $msbuild) { throw "MSBuild was not found. Use a Visual Studio Developer PowerShell with the .NET Framework 4.8 targeting pack." }
& $msbuild.Source $project "/t:Restore,Build" "/p:Configuration=$Configuration" "/p:RDR2GameDir=$RDR2GameDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Host "Build output: $(Join-Path $PSScriptRoot "bin\$Configuration\EnRAGECompanionsExtension.dll")"
