[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'LocalMCPChatClient.sln'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK が見つかりません。Visual Studio 2026の「.NETデスクトップ開発」または https://dotnet.microsoft.com/download/dotnet/10.0 から導入してください。'
}

$sdkVersion = & dotnet --version
if ([version]($sdkVersion -replace '-.*$', '') -lt [version]'10.0.300') {
    throw ".NET 10 SDK 10.0.300 以降が必要です。検出されたバージョン: $sdkVersion"
}

Push-Location $repositoryRoot
try {
    & dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore に失敗しました。' }

    & dotnet build $solutionPath --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build に失敗しました。' }

    if (-not $SkipTests) {
        & dotnet test $solutionPath --configuration $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test に失敗しました。' }
    }
}
finally {
    Pop-Location
}

Write-Host '準備が完了しました。Visual Studio で LocalMCPChatClient.sln を開くか、次を実行してください:' -ForegroundColor Green
Write-Host 'dotnet run --project src/LocalMCPChatClient.App/LocalMCPChatClient.App.csproj'
