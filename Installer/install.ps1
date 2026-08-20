[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$AppDir = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Backstage'),
    [switch]$NoShortcuts,
    [switch]$NoLaunch,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

function Select-CloneHeroDirectory {
    $defaultDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Clone Hero'
    if (Test-Path -LiteralPath (Join-Path $defaultDirectory 'Clone Hero.exe')) {
        return $defaultDirectory
    }

    if ($NonInteractive) {
        throw 'Clone Hero nao foi encontrado.'
    }

    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    try {
        $dialog.Description = 'Selecione a pasta que contem Clone Hero.exe'
        $dialog.SelectedPath = [Environment]::GetFolderPath('MyDocuments')
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            throw 'Instalacao cancelada.'
        }
        return $dialog.SelectedPath
    }
    finally {
        $dialog.Dispose()
    }
}

function New-Shortcut([string]$Path, [string]$Target) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path -Parent $Target
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

try {
    if (-not $GameDir) {
        $GameDir = Select-CloneHeroDirectory
    }

    $GameDir = [IO.Path]::GetFullPath($GameDir)
    $gameExe = Join-Path $GameDir 'Clone Hero.exe'
    if (-not (Test-Path -LiteralPath $gameExe)) {
        throw 'A pasta selecionada nao contem Clone Hero.exe.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Clone Hero_Data\il2cpp_data'))) {
        throw 'Esta instalacao nao parece ser o Clone Hero 1.1 IL2CPP compativel.'
    }
    if (Get-Process -Name 'Clone Hero', 'Backstage' -ErrorAction SilentlyContinue) {
        throw 'Feche o Clone Hero e o Backstage antes de instalar.'
    }

    $requiredFiles = @(
        'Backstage.exe',
        'Backstage.dll',
        'BepInEx.zip',
        'BepInEx-LICENSE.txt',
        'BepInEx-NOTICE.txt'
    )
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file))) {
            throw "Arquivo ausente no instalador: $file"
        }
    }

    Expand-Archive -LiteralPath (Join-Path $PSScriptRoot 'BepInEx.zip') -DestinationPath $GameDir -Force

    $pluginDirectory = Join-Path $GameDir 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $pluginDirectory | Out-Null
    Get-ChildItem -LiteralPath $pluginDirectory -Filter 'Backstage*.dll' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination ($_.FullName + '.bak') -Force }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Backstage.dll') -Destination (Join-Path $pluginDirectory 'Backstage.dll') -Force

    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    $appExe = Join-Path $AppDir 'Backstage.exe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Backstage.exe') -Destination $appExe -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BepInEx-LICENSE.txt') -Destination $AppDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BepInEx-NOTICE.txt') -Destination $AppDir -Force

    if (-not $NoShortcuts) {
        $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Backstage.lnk'
        $desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Backstage.lnk'
        New-Shortcut $startMenu $appExe
        New-Shortcut $desktop $appExe
    }

    if ($NonInteractive) {
        Write-Output "Backstage instalado em $AppDir"
    }
    elseif ($NoLaunch) {
        [System.Windows.Forms.MessageBox]::Show(
            'Backstage instalado com sucesso.',
            'Backstage',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    }
    else {
        [System.Windows.Forms.MessageBox]::Show(
            'Backstage instalado. O Clone Hero sera aberto agora. A primeira abertura pode levar alguns minutos enquanto o BepInEx prepara os arquivos.',
            'Backstage',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
        Start-Process -FilePath $gameExe -WorkingDirectory $GameDir
    }
}
catch {
    $message = $_.Exception.Message
    if (-not $NonInteractive) {
        [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Backstage - erro na instalacao',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    Write-Error $message
    exit 1
}
