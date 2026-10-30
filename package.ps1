param(
    [string]$RestoreConfigFile,
    [string]$RestorePackagesPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Push-Location $PSScriptRoot
try {
    $restoreArguments = @()
    if ($RestoreConfigFile) { $restoreArguments += "-p:RestoreConfigFile=$RestoreConfigFile" }
    if ($RestorePackagesPath) { $restoreArguments += "-p:RestorePackagesPath=$RestorePackagesPath" }
    & dotnet build ./SkillTipsResponseAnalyzer.csproj -c Release -m:1 -nr:false -tl:off -v:minimal `
        -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU `
        -p:GenerateUraPluginManifestOnBuild=true -p:PackageUraPluginOnBuild=true `
        -p:DeployUraPluginToLocalAppDataOnBuild=false @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Managed plugin build failed ($LASTEXITCODE)." }

    & pwsh -NoProfile -File ./native/build.ps1
    if ($LASTEXITCODE -ne 0) { throw "Native plugin build failed ($LASTEXITCODE)." }

    $outputDirectory = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $nativeDestination = Join-Path $outputDirectory 'skill_tips_bridge.dll'
    if (Test-Path -LiteralPath $nativeDestination) {
        try { [IO.File]::Open($nativeDestination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read).Dispose() }
        catch { throw "无法覆盖 $nativeDestination；请退出加载此 DLL 的游戏后重试。$($_.Exception.Message)" }
    }
    $deliverables = @(
        (Join-Path $PSScriptRoot 'bin/Release/SkillTipsResponseAnalyzer.zip'),
        (Join-Path $PSScriptRoot 'native/target/x86_64-pc-windows-msvc/release/skill_tips_bridge.dll')
    )
    $checksums = foreach ($deliverable in $deliverables) {
        $name = Split-Path -Leaf $deliverable
        $destination = Join-Path $outputDirectory $name
        Copy-Item -LiteralPath $deliverable -Destination $destination
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        Write-Host "$destination ($((Get-Item -LiteralPath $destination).Length) bytes)"
        "$hash  $name"
    }
    $checksums | Set-Content -LiteralPath (Join-Path $outputDirectory 'SHA256SUMS.txt') -Encoding utf8
    $checksums
}
finally {
    Pop-Location
}
