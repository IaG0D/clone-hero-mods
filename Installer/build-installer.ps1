[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoDirectory = Split-Path -Parent $PSScriptRoot
$desktopProject = Join-Path $repoDirectory 'BackstageDesktop\BackstageDesktop.csproj'
$modProject = Join-Path $repoDirectory 'BackstageMod\BackstageMod.csproj'
[xml]$projectXml = Get-Content -LiteralPath $desktopProject
$version = [string]$projectXml.Project.PropertyGroup.Version

$bepInExName = 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip'
$bepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785%2B6abdba4.zip'
$bepInExHash = '2A7CBF74D26ABE4765C3E662DB1721B923BAC39849EBFEF2CA5DC7DE7E2D9B7F'
$bepInExCommit = '6abdba47eeebe08552282e7a58ef0f4a9ab60b62'

$desktopPublish = Join-Path $repoDirectory 'publish\BackstageDesktop'
$cacheDirectory = Join-Path $repoDirectory 'publish\InstallerCache'
$outputDirectory = Join-Path $repoDirectory 'publish\Installer'
$payloadDirectory = Join-Path $outputDirectory 'payload'
$setupPath = Join-Path $outputDirectory "Backstage-Setup-$version.exe"
$sedPath = Join-Path $outputDirectory 'Backstage.sed'
$bepInExArchive = Join-Path $cacheDirectory $bepInExName

Push-Location $repoDirectory
try {
    & dotnet publish $desktopProject -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' -o $desktopPublish
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o Backstage Desktop.' }

    & dotnet build $modProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o plugin Backstage.' }

    New-Item -ItemType Directory -Force -Path $cacheDirectory, $outputDirectory | Out-Null
    if (Test-Path -LiteralPath $payloadDirectory) {
        Remove-Item -LiteralPath $payloadDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payloadDirectory | Out-Null

    if (-not (Test-Path -LiteralPath $bepInExArchive)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $bepInExUrl -OutFile $bepInExArchive
    }
    if ((Get-FileHash -LiteralPath $bepInExArchive -Algorithm SHA256).Hash -ne $bepInExHash) {
        throw 'O pacote do BepInEx nao passou na verificacao SHA-256.'
    }

    Copy-Item -LiteralPath (Join-Path $desktopPublish 'Backstage.exe') -Destination $payloadDirectory
    Copy-Item -LiteralPath (Join-Path $repoDirectory 'BackstageMod\bin\Release\net6.0\Backstage.dll') -Destination $payloadDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $payloadDirectory
    Copy-Item -LiteralPath $bepInExArchive -Destination (Join-Path $payloadDirectory 'BepInEx.zip')

    $licensePath = Join-Path $payloadDirectory 'BepInEx-LICENSE.txt'
    Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/BepInEx/BepInEx/$bepInExCommit/LICENSE" -OutFile $licensePath
    @"
BepInEx 6.0.0-be.785
Copyright (C) 2020 BepInEx Team
License: GNU LGPL 2.1 or later; see BepInEx-LICENSE.txt.
Unmodified official binary: $bepInExUrl
Corresponding source: https://github.com/BepInEx/BepInEx/tree/$bepInExCommit
"@ | Set-Content -LiteralPath (Join-Path $payloadDirectory 'BepInEx-NOTICE.txt') -Encoding ASCII

    $testDirectory = Join-Path $outputDirectory '.selftest'
    if (Test-Path -LiteralPath $testDirectory) {
        Remove-Item -LiteralPath $testDirectory -Recurse -Force
    }
    $testGame = Join-Path $testDirectory 'Clone Hero'
    $testApp = Join-Path $testDirectory 'Backstage'
    New-Item -ItemType Directory -Force -Path (Join-Path $testGame 'Clone Hero_Data\il2cpp_data') | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $testGame 'Clone Hero.exe') | Out-Null
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $payloadDirectory 'install.ps1') -GameDir $testGame -AppDir $testApp -NoShortcuts -NoLaunch -NonInteractive
    if ($LASTEXITCODE -ne 0) { throw 'A instalacao simulada falhou.' }
    foreach ($installedFile in @(
        (Join-Path $testGame 'winhttp.dll'),
        (Join-Path $testGame 'BepInEx\plugins\Backstage.dll'),
        (Join-Path $testApp 'Backstage.exe'),
        (Join-Path $testApp 'BepInEx-LICENSE.txt')
    )) {
        if (-not (Test-Path -LiteralPath $installedFile)) {
            throw "A instalacao simulada nao criou $installedFile"
        }
    }
    Remove-Item -LiteralPath $testDirectory -Recurse -Force

    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setupPath
FriendlyName=Backstage $version
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="install.ps1"
FILE1="Backstage.exe"
FILE2="Backstage.dll"
FILE3="BepInEx.zip"
FILE4="BepInEx-LICENSE.txt"
FILE5="BepInEx-NOTICE.txt"
[SourceFiles]
SourceFiles0=$payloadDirectory\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
%FILE4%=
%FILE5%=
"@
    $sed | Set-Content -LiteralPath $sedPath -Encoding ASCII

    $iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
    $process = Start-Process -FilePath $iexpress -ArgumentList '/N', $sedPath -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupPath)) {
        throw 'O IExpress nao conseguiu gerar o Setup.exe.'
    }

    $setup = Get-Item -LiteralPath $setupPath
    $hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
    Write-Output "Criado: $($setup.FullName)"
    Write-Output "Tamanho: $($setup.Length) bytes"
    Write-Output "SHA256: $($hash.Hash)"
}
finally {
    Pop-Location
}
