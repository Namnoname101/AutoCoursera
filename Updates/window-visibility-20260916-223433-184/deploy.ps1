$ErrorActionPreference = 'Stop'
$installRoot = 'D:\Acose\CustomerManager'
$stageRoot = $PSScriptRoot
$expectedBaseline = '45D4CEB45F00DC947495092EC26822D97292E19BE8A1FF3A40B43EB2F85F1E80'
$binaries = @('Acose.CustomerManager.exe', 'Acose.CustomerManager.dll', 'Acose.CustomerManager.pdb')
$sources = @('Models.cs', 'ManagerController.cs', 'UI\MainWindow.xaml', 'UI\MainWindow.xaml.cs', 'Runtime\WorkerWindow.cs')

if (-not (Test-Path -LiteralPath (Join-Path $installRoot '.acose-customer-manager-install'))) { throw 'Missing installation marker.' }
$running = Get-Process Acose.CustomerManager,AutomationPlatform.Presentation -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Close CustomerManager and its Workers before updating.' }
if ((Get-FileHash -LiteralPath (Join-Path $installRoot 'Acose.CustomerManager.dll')).Hash -ne $expectedBaseline) {
    throw 'Installed Manager changed since inspection; deployment stopped.'
}
foreach ($file in @('Acose.CustomerManager.deps.json', 'Acose.CustomerManager.runtimeconfig.json')) {
    if ((Get-FileHash -LiteralPath (Join-Path $installRoot $file)).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $stageRoot ('build\' + $file))).Hash) { throw "Runtime changed: $file" }
}

$protectedFiles = @(
    'Data\manager-data.json', 'Data\manager-data.json.bak', 'Data\engine-settings.protected',
    'engine-manifest.json', 'Engine\AutomationPlatform.Presentation.dll'
)
$protectedHashes = @{}
foreach ($file in $protectedFiles) {
    $filePath = Join-Path $installRoot $file
    if (Test-Path -LiteralPath $filePath) { $protectedHashes[$file] = (Get-FileHash -LiteralPath $filePath).Hash }
}

$updateRoot = Join-Path $installRoot ('Updates\window-visibility-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$backupRoot = Join-Path $updateRoot 'Backup'
New-Item -ItemType Directory -Path $backupRoot | Out-Null
foreach ($file in $binaries) { Copy-Item -LiteralPath (Join-Path $installRoot $file) -Destination $backupRoot }
Copy-Item -LiteralPath (Join-Path $installRoot 'Source') -Destination (Join-Path $backupRoot 'Source') -Recurse
New-Item -ItemType Directory -Path (Join-Path $updateRoot 'Source') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $stageRoot 'Source') | Where-Object Name -NotIn @('bin','obj') |
    Copy-Item -Destination (Join-Path $updateRoot 'Source') -Recurse
New-Item -ItemType Directory -Path (Join-Path $updateRoot 'Checks') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $stageRoot 'Checks') | Where-Object Name -NotIn @('bin','obj') |
    Copy-Item -Destination (Join-Path $updateRoot 'Checks') -Recurse
Copy-Item -LiteralPath $PSCommandPath -Destination $updateRoot

try {
    foreach ($file in $binaries) {
        Copy-Item -LiteralPath (Join-Path $stageRoot ('build\' + $file)) -Destination (Join-Path $installRoot $file) -Force
        if ((Get-FileHash -LiteralPath (Join-Path $stageRoot ('build\' + $file))).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $installRoot $file)).Hash) { throw "Binary verification failed: $file" }
    }
    foreach ($file in $sources) {
        Copy-Item -LiteralPath (Join-Path $stageRoot ('Source\' + $file)) -Destination (Join-Path $installRoot ('Source\' + $file)) -Force
        if ((Get-FileHash -LiteralPath (Join-Path $stageRoot ('Source\' + $file))).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $installRoot ('Source\' + $file))).Hash) { throw "Source verification failed: $file" }
    }
    foreach ($file in $protectedHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $installRoot $file)).Hash -ne $protectedHashes[$file]) {
            throw "Protected file changed unexpectedly: $file"
        }
    }
}
catch {
    foreach ($file in $binaries) { Copy-Item -LiteralPath (Join-Path $backupRoot $file) -Destination (Join-Path $installRoot $file) -Force }
    foreach ($file in $sources) {
        $original = Join-Path $backupRoot ('Source\' + $file)
        $destination = Join-Path $installRoot ('Source\' + $file)
        if (Test-Path -LiteralPath $original) { Copy-Item -LiteralPath $original -Destination $destination -Force }
        elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
    }
    throw
}
[PSCustomObject]@{
    Updated = $installRoot
    Backup = $backupRoot
    ManagerSha256 = (Get-FileHash -LiteralPath (Join-Path $installRoot 'Acose.CustomerManager.dll')).Hash
    ProtectedFilesUnchanged = $true
} | ConvertTo-Json
