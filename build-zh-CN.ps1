param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish\WallpaperIconHider-1.3.0-zh-CN')
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$project = Join-Path $PSScriptRoot 'WallpaperIconHider\WallpaperIconHider.csproj'
Push-Location $PSScriptRoot
try {
& $Dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -p:Version=1.3.0 `
    -p:InformationalVersion=1.3.0-zh-CN.1 `
    -p:SourceRevisionId=b0f7227e7199e19a493f92d99c3f1e41c06bdab0 -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出代码：$LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $OutputDirectory
Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'WallpaperIconHider.exe') -Algorithm SHA256
}
finally {
    Pop-Location
}
