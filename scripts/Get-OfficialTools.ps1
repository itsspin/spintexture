[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$vendorRoot = Join-Path $projectRoot 'vendor'
$realRoot = Join-Path $vendorRoot 'realesrgan'
$licenseRoot = Join-Path $vendorRoot 'licenses'

New-Item -ItemType Directory -Force -Path $vendorRoot, $realRoot, $licenseRoot | Out-Null

$downloads = @(
    @{
        Uri = 'https://github.com/microsoft/DirectXTex/releases/download/may2026/texconv.exe'
        Output = Join-Path $vendorRoot 'texconv.exe'
    },
    @{
        Uri = 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip'
        Output = Join-Path $vendorRoot 'realesrgan-windows.zip'
    },
    @{
        Uri = 'https://raw.githubusercontent.com/microsoft/DirectXTex/main/LICENSE'
        Output = Join-Path $licenseRoot 'DirectXTex-LICENSE.txt'
    },
    @{
        Uri = 'https://raw.githubusercontent.com/xinntao/Real-ESRGAN-ncnn-vulkan/master/LICENSE'
        Output = Join-Path $licenseRoot 'Real-ESRGAN-ncnn-vulkan-LICENSE.txt'
    }
)

foreach ($download in $downloads) {
    Invoke-WebRequest -Uri $download.Uri -OutFile $download.Output
}

Expand-Archive -LiteralPath (Join-Path $vendorRoot 'realesrgan-windows.zip') -DestinationPath $realRoot -Force

Get-ChildItem -LiteralPath $vendorRoot -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($vendorRoot.Length + 1)
        Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
} | Format-Table -AutoSize
