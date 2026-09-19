param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$VerificationSummary
)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$destination = [IO.Path]::GetFullPath('D:\Acose\CustomerManager')
$engine = Join-Path $destination 'Engine'
$baselineHash = '9CD865A3454BF63C85A512B4145A4683238B49E864820DD65E010A8A7AF912D6'
$manifestPath = Join-Path $destination 'engine-manifest.json'
$built = Join-Path $taskRoot 'Source\src\AutomationPlatform.Presentation\bin\Release\net10.0-windows\win-x64'
$files = @('AutomationPlatform.Presentation.dll', 'AutomationPlatform.Presentation.pdb')

if ($destination -ne 'D:\Acose\CustomerManager' -or
    !(Test-Path -LiteralPath (Join-Path $destination '.acose-customer-manager-install') -PathType Leaf)) {
    throw 'Unexpected CustomerManager installation target.'
}
foreach ($directory in @($destination, $engine)) {
    if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing redirected installation directory: $directory"
    }
}
$managerExecutable = Join-Path $destination 'Acose.CustomerManager.exe'
$workerExecutable = Join-Path $engine 'AutomationPlatform.Presentation.exe'
function Assert-CustomerManagerClosed {
    $active = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -eq $managerExecutable -or $_.ExecutablePath -eq $workerExecutable
    })
    if ($active.Count -gt 0) {
        throw 'Close CustomerManager and all of its course/profile windows before applying this update.'
    }
}
Assert-CustomerManagerClosed
if ((Get-FileHash -LiteralPath (Join-Path $engine $files[0])).Hash -ne $baselineHash) {
    throw 'Installed engine changed since baseline verification; inspect the current build before updating.'
}
foreach ($file in $files) {
    foreach ($directory in @($engine, $built)) {
        if (!(Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf)) {
            throw "Missing required build or backup file: $directory\$file"
        }
    }
}
foreach ($name in @('Source', 'Checks', 'ScannerChecks', 'EnrollmentChecks')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskRoot $name) -PathType Container)) {
        throw "Missing update archive source: $name"
    }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (@($manifest.Files | Where-Object { $_.File -eq $files[0] }).Count -ne 1) {
    throw 'Engine manifest must contain exactly one Presentation DLL entry.'
}
# Validate every recorded engine file before preserving the existing manifest entries.
foreach ($entry in $manifest.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $engine $entry.File))
    if (![IO.Path]::GetDirectoryName($target).Equals($engine, [StringComparison]::OrdinalIgnoreCase) -or
        !(Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Unexpected engine manifest file: $($entry.File)"
    }
    if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.SHA256) {
        throw "Engine manifest checksum does not match: $($entry.File)"
    }
}

$update = Join-Path $destination ('Updates\peer-pdf-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$backup = Join-Path $update 'Before'
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $engine $file) -Destination (Join-Path $backup $file)
}
Copy-Item -LiteralPath $manifestPath -Destination $backup
foreach ($name in @('Source', 'Checks', 'ScannerChecks', 'EnrollmentChecks')) {
    $source = Join-Path $taskRoot $name
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $target = Join-Path (Join-Path $update $name) ([IO.Path]::GetRelativePath($source, $file.FullName))
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
foreach ($name in @('README.md', 'deploy.ps1')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $update
}

# Recheck after archival so a newly opened manager cannot launch an old worker mid-update.
Assert-CustomerManagerClosed
if ((Get-FileHash -LiteralPath (Join-Path $engine $files[0])).Hash -ne $baselineHash) {
    throw 'Installed engine changed while preparing the update.'
}
try {
    foreach ($file in $files) {
        $source = Join-Path $built $file
        $target = Join-Path $engine $file
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw "Installed checksum mismatch: $file"
        }
    }
    foreach ($entry in $manifest.Files | Where-Object { $_.File -in $files }) {
        $entry.SHA256 = (Get-FileHash -LiteralPath (Join-Path $engine $entry.File)).Hash
    }
    $pdfRoot = Join-Path $destination 'PreparedPdfs'
    if (!(Test-Path -LiteralPath $pdfRoot)) {
        New-Item -ItemType Directory -Path $pdfRoot | Out-Null
    } elseif (!(Test-Path -LiteralPath $pdfRoot -PathType Container)) {
        throw 'PreparedPdfs exists but is not a directory.'
    }
    $manifest | Add-Member -NotePropertyName PeerPdfUpdate -NotePropertyValue ([pscustomobject]@{
        InstalledAt = [DateTimeOffset]::Now.ToString('o')
        Source = Join-Path $update 'Source'
        Backup = $backup
        Checks = $VerificationSummary
        PreparedPdfRoot = $pdfRoot
    }) -Force
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
} catch {
    $updateFailure = $_
    foreach ($file in $files) {
        Copy-Item -LiteralPath (Join-Path $backup $file) -Destination (Join-Path $engine $file) -Force
    }
    Copy-Item -LiteralPath (Join-Path $backup 'engine-manifest.json') -Destination $manifestPath -Force
    throw $updateFailure
}
Write-Output "Installed peer PDF update: $update"
Write-Output "Prepared PDF library: $pdfRoot"
Get-FileHash -LiteralPath (Join-Path $engine $files[0]) | Select-Object Hash, Path
