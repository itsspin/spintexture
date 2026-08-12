[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipSelfTests,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:SPINTEXTURE_TEXCONV = Join-Path $projectRoot 'vendor\texconv.exe'
$env:SPINTEXTURE_REALESRGAN = Join-Path $projectRoot 'vendor\realesrgan\realesrgan-ncnn-vulkan.exe'
$env:SPINTEXTURE_REALESRGAN_MODELS = Join-Path $projectRoot 'vendor\realesrgan\models'
$env:SPINTEXTURE_UPSCAYL = Join-Path $projectRoot 'vendor\upscayl\upscayl-bin.exe'
$env:SPINTEXTURE_TEXTURE_MODELS = Join-Path $projectRoot 'vendor\upscayl\models'
$solution = Join-Path $projectRoot 'SpinTextureProject.sln'
$buildOutputRoot = Join-Path $projectRoot "artifacts\release-build\$Configuration"
$publishRoot = Join-Path $projectRoot 'publish\SpinTexture-win-x64'
$releaseArchiveName = if ([string]::IsNullOrWhiteSpace($Version)) {
    'SpinTexture-win-x64'
}
else {
    "SpinTexture-$Version-win-x64"
}
$zipPath = Join-Path $projectRoot "publish\$releaseArchiveName.zip"
[string[]]$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $versionArguments += "-p:Version=$Version"
}
$resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$resolvedPublishRoot = [IO.Path]::GetFullPath($publishRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ((-not $resolvedPublishRoot.StartsWith($resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) -or
    ((Split-Path -Leaf $resolvedPublishRoot) -ne 'SpinTexture-win-x64')) {
    throw "Refusing to use an unexpected publish target: $resolvedPublishRoot"
}
$requiredTools = @(
    (Join-Path $projectRoot 'vendor\texconv.exe'),
    (Join-Path $projectRoot 'vendor\realesrgan\realesrgan-ncnn-vulkan.exe'),
    (Join-Path $projectRoot 'vendor\realesrgan\vcomp140.dll'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrgan-x4plus.bin'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrgan-x4plus.param'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrnet-x4plus.bin'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrnet-x4plus.param'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrgan-x4plus-anime.bin'),
    (Join-Path $projectRoot 'vendor\realesrgan\models\realesrgan-x4plus-anime.param'),
    (Join-Path $projectRoot 'vendor\upscayl\upscayl-bin.exe'),
    (Join-Path $projectRoot 'vendor\upscayl\models\4x-PBRify_UpscalerSPANV4.bin'),
    (Join-Path $projectRoot 'vendor\upscayl\models\4x-PBRify_UpscalerSPANV4.param')
)

foreach ($toolPath in $requiredTools) {
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Required bundled component is missing: $toolPath"
    }
}

$vendorRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'vendor')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$vendorManifestPath = Join-Path $vendorRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $vendorManifestPath -PathType Leaf)) {
    throw "Required vendor manifest is missing: $vendorManifestPath"
}

$vendorManifest = Get-Content -LiteralPath $vendorManifestPath -Raw | ConvertFrom-Json
if ($vendorManifest.schemaVersion -ne 1 -or $null -eq $vendorManifest.components) {
    throw "The vendor manifest schema is unsupported or incomplete."
}

$manifestedComponentPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($component in $vendorManifest.components) {
    if ([string]::IsNullOrWhiteSpace($component.path) -or
        [string]::IsNullOrWhiteSpace($component.sha256) -or
        $component.bytes -lt 1) {
        throw "The vendor manifest contains an incomplete component entry."
    }

    $componentPath = [IO.Path]::GetFullPath((Join-Path $vendorRoot ([string]$component.path)))
    if (-not $componentPath.StartsWith(
            $vendorRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The vendor manifest contains an unsafe path: $($component.path)"
    }
    if (-not $manifestedComponentPaths.Add($componentPath)) {
        throw "The vendor manifest contains a duplicate component path: $($component.path)"
    }
    if (-not (Test-Path -LiteralPath $componentPath -PathType Leaf)) {
        throw "A pinned vendor component is missing: $($component.path)"
    }

    $componentFile = Get-Item -LiteralPath $componentPath
    if ($componentFile.Length -ne [long]$component.bytes) {
        throw "Vendor component length verification failed: $($component.path)"
    }
    $componentHash = (Get-FileHash -LiteralPath $componentPath -Algorithm SHA256).Hash
    if (-not $componentHash.Equals(
            [string]$component.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Vendor component SHA-256 verification failed: $($component.path)"
    }
}

foreach ($toolPath in $requiredTools) {
    $resolvedToolPath = [IO.Path]::GetFullPath($toolPath)
    if (-not $manifestedComponentPaths.Contains($resolvedToolPath)) {
        throw "A required bundled component is not pinned by the vendor manifest: $toolPath"
    }
}

dotnet restore $solution --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

dotnet build $solution -c $Configuration --no-restore -p:NuGetAudit=false -p:OutputPath=$buildOutputRoot @versionArguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

if (-not $SkipSelfTests) {
    $selfTestAssembly = Join-Path $buildOutputRoot 'SpinTexture.SelfTest.dll'
    if (-not (Test-Path -LiteralPath $selfTestAssembly -PathType Leaf)) {
        throw "SpinTexture self-test assembly was not built: $selfTestAssembly"
    }

    dotnet $selfTestAssembly
    if ($LASTEXITCODE -ne 0) { throw 'SpinTexture self-tests failed.' }
}

if (Test-Path -LiteralPath $resolvedPublishRoot) {
    Remove-Item -LiteralPath $resolvedPublishRoot -Recurse -Force
}

dotnet publish (Join-Path $projectRoot 'src\SpinTexture.App\SpinTexture.App.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:NuGetAudit=false -p:OutputPath=$buildOutputRoot -o $resolvedPublishRoot @versionArguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $resolvedPublishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $resolvedPublishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination (Join-Path $resolvedPublishRoot 'docs') -Recurse

$releaseVersionPath = Join-Path $resolvedPublishRoot 'release-version.json'
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishedProductVersion = (Get-Item -LiteralPath (Join-Path $resolvedPublishRoot 'SpinTexture.exe')).VersionInfo.ProductVersion
    $normalizedPublishedVersion = ($publishedProductVersion -split '\+', 2)[0]
    if (-not $normalizedPublishedVersion.Equals($Version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published SpinTexture.exe reports version '$publishedProductVersion', expected '$Version'."
    }

    [pscustomobject]@{
        SchemaVersion = 1
        Version = $Version
    } | ConvertTo-Json | Set-Content -LiteralPath $releaseVersionPath -Encoding UTF8
}

$requiredPublishedFiles = @(
    (Join-Path $resolvedPublishRoot 'SpinTexture.exe'),
    (Join-Path $resolvedPublishRoot 'docs\SAFETY_AND_RESTORE.md'),
    (Join-Path $resolvedPublishRoot 'Tools\texconv.exe'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\realesrgan-ncnn-vulkan.exe'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\vcomp140.dll'),
    (Join-Path $resolvedPublishRoot 'Tools\manifest.json'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrgan-x4plus.bin'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrgan-x4plus.param'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrnet-x4plus.bin'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrnet-x4plus.param'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrgan-x4plus-anime.bin'),
    (Join-Path $resolvedPublishRoot 'Tools\realesrgan\models\realesrgan-x4plus-anime.param'),
    (Join-Path $resolvedPublishRoot 'Tools\upscayl\upscayl-bin.exe'),
    (Join-Path $resolvedPublishRoot 'Tools\upscayl\models\4x-PBRify_UpscalerSPANV4.bin'),
    (Join-Path $resolvedPublishRoot 'Tools\upscayl\models\4x-PBRify_UpscalerSPANV4.param')
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $requiredPublishedFiles += $releaseVersionPath
}
foreach ($publishedFile in $requiredPublishedFiles) {
    if (-not (Test-Path -LiteralPath $publishedFile -PathType Leaf)) {
        throw "Published release is incomplete: $publishedFile"
    }
}

$startupSmokeArguments = @('--startup-smoke')
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $startupSmokeArguments += '--expected-version'
    $startupSmokeArguments += $Version
}
$startupSmoke = Start-Process `
    -FilePath (Join-Path $resolvedPublishRoot 'SpinTexture.exe') `
    -ArgumentList $startupSmokeArguments `
    -WorkingDirectory $resolvedPublishRoot `
    -PassThru
if (-not $startupSmoke.WaitForExit(60000)) {
    Stop-Process -Id $startupSmoke.Id -Force -ErrorAction SilentlyContinue
    throw 'Published SpinTexture startup smoke test timed out.'
}
if ($startupSmoke.ExitCode -ne 0) {
    throw "Published SpinTexture startup smoke test failed with exit code $($startupSmoke.ExitCode)."
}

$manifest = Get-ChildItem -LiteralPath $resolvedPublishRoot -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($resolvedPublishRoot.Length + 1)
        Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $resolvedPublishRoot 'release-manifest.json') -Encoding UTF8

$resolvedZipPath = [IO.Path]::GetFullPath($zipPath)
$expectedZipParent = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish')).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (((Split-Path -Parent $resolvedZipPath).TrimEnd([IO.Path]::DirectorySeparatorChar) -ne $expectedZipParent) -or
    ((Split-Path -Leaf $resolvedZipPath) -ne "$releaseArchiveName.zip")) {
    throw "Refusing to use an unexpected release archive target: $resolvedZipPath"
}

Compress-Archive -Path (Join-Path $resolvedPublishRoot '*') -DestinationPath $resolvedZipPath -CompressionLevel Optimal -Force
$zipHash = (Get-FileHash -LiteralPath $resolvedZipPath -Algorithm SHA256).Hash
"$zipHash  $releaseArchiveName.zip" | Set-Content -LiteralPath ($resolvedZipPath + '.sha256.txt') -Encoding ASCII

Write-Host "SpinTexture published to $resolvedPublishRoot" -ForegroundColor Green
Write-Host "Portable release: $resolvedZipPath" -ForegroundColor Green
