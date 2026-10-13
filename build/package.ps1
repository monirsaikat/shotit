<#
.SYNOPSIS
    Builds every ShotIt release artifact: Setup.exe, portable zip, MSI, update feed, hashes, winget manifest.

.DESCRIPTION
    Run from anywhere:   pwsh build/package.ps1 -Version 1.0.0
    Output lands in artifacts/release. Nothing is uploaded or published by this script.

.PARAMETER Version
    Three-part version number of the release, for example 1.2.0.

.PARAMETER Runtimes
    Which CPU architectures to build. Default: x64 and ARM64.

.PARAMETER UpdateFeedUrl
    https address the installed app checks for updates (a GitHub repository URL or a folder that
    serves the Velopack feed). Empty means the build never updates itself.

.PARAMETER SignParams
    Arguments for signtool.exe, for example:
      '/fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Your Company"'
    When empty the artifacts are NOT signed and Windows SmartScreen will warn users.

.PARAMETER SkipTests
    Skip the unit tests and self-tests (not recommended for a real release).

.PARAMETER SkipMsi
    Skip the MSI (it needs the WiX toolset, which is restored from NuGet on first use).

.PARAMETER PublisherName / PackageUrl
    Used only for the winget manifest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('win-x64', 'win-arm64')][string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$UpdateFeedUrl = '',
    [string]$SignParams = '',
    [switch]$SkipTests,
    [switch]$SkipMsi,
    [string]$PublisherName = 'ShotIt',
    [string]$PackageUrl = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $artifacts 'release'
$app = Join-Path $root 'src/ShotIt.App/ShotIt.App.csproj'
$icon = Join-Path $root 'src/ShotIt.App/Assets/shotit.ico'

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

if ($UpdateFeedUrl -and -not $UpdateFeedUrl.StartsWith('https://')) { throw 'UpdateFeedUrl must start with https://.' }

Push-Location $root
try {
    if (Test-Path $release) { Remove-Item $release -Recurse -Force }
    New-Item $release -ItemType Directory -Force | Out-Null

    Invoke-Checked 'Restore tools' { dotnet tool restore }

    if (-not $SkipTests) {
        Invoke-Checked 'Unit and integration tests' { dotnet test ShotIt.slnx -c Release --nologo -v q }
    }

    foreach ($rid in $Runtimes) {
        $arch = $rid.Substring(4)
        $publish = Join-Path $artifacts "publish/$rid"
        if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

        Invoke-Checked "Publish $rid" {
            dotnet publish $app -c Release -r $rid --self-contained `
                -p:PublishReadyToRun=true -p:Version=$Version -p:UpdateFeedUrl=$UpdateFeedUrl `
                -o $publish --nologo -v q
        }

        # The published program must pass its own end-to-end checks on the build machine's architecture.
        $native = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
        if (-not $SkipTests -and $rid -eq $native) {
            foreach ($scenario in 'overlay', 'editor', 'windows', 'policy', 'ocr') {
                $out = Join-Path $artifacts "selftest/$rid/$scenario"
                for ($attempt = 1; $attempt -le 2; $attempt++) {
                    Write-Host "==> Self-test $scenario ($rid), attempt $attempt" -ForegroundColor Cyan
                    $process = Start-Process (Join-Path $publish 'ShotIt.exe') -ArgumentList '--selftest', $scenario, "`"$out`"" -Wait -PassThru
                    if ($process.ExitCode -eq 0) { break }
                    if ($attempt -eq 2) { throw "Self-test '$scenario' failed twice. See $out\result.json." }
                    Write-Warning "Self-test '$scenario' failed on the first attempt; retrying after the desktop settles."
                }
            }
        }

        if ($SignParams) {
            # Sign our own binaries before they are packed; vpk signs the installer and the update package itself.
            $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
            $own = Get-ChildItem $publish -Filter 'ShotIt*' | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object FullName
            Invoke-Checked "Sign binaries ($rid)" { & $signtool sign @($SignParams -split ' (?=(?:[^"]*"[^"]*")*[^"]*$)' | ForEach-Object { $_.Trim('"') }) $own }
        }

        # --- portable zip: the published folder plus an empty "data" folder, which switches the app to portable mode ---
        $portable = Join-Path $artifacts "portable/$rid/ShotIt"
        if (Test-Path (Split-Path $portable)) { Remove-Item (Split-Path $portable) -Recurse -Force }
        New-Item $portable -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $publish '*') $portable -Recurse
        New-Item (Join-Path $portable 'data') -ItemType Directory | Out-Null
        Set-Content (Join-Path $portable 'data/README.txt') -Encoding utf8 -Value @(
            'ShotIt keeps its settings, history and logs in this folder because it exists.'
            'Delete the folder (and restart ShotIt) to use your Windows profile instead.'
        )
        $portableZip = Join-Path $release "ShotIt-Portable-$arch.zip"
        Compress-Archive -Path $portable -DestinationPath $portableZip -CompressionLevel Optimal
        Write-Host "    $portableZip"

        # --- installer and update feed ---
        $packOut = Join-Path $artifacts "velopack/$rid"
        if (Test-Path $packOut) { Remove-Item $packOut -Recurse -Force }
        $packArgs = @(
            'pack', '--packId', 'ShotIt.App', '--packTitle', 'ShotIt', '--packAuthors', $PublisherName,
            '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'ShotIt.exe', '--icon', $icon,
            '--runtime', $rid, '--channel', "$arch-stable", '--outputDir', $packOut
        )
        if ($SignParams) { $packArgs += @('--signParams', $SignParams) }
        Invoke-Checked "Pack installer ($rid)" { dotnet vpk @packArgs }

        Copy-Item (Join-Path $packOut "ShotIt.App-$arch-stable-Setup.exe") (Join-Path $release "ShotIt-Setup-$arch.exe")
        # Everything an update server has to host for this architecture.
        $feed = Join-Path $release "feed"
        New-Item $feed -ItemType Directory -Force | Out-Null
        Get-ChildItem $packOut | Where-Object { $_.Name -like '*.nupkg' -or $_.Name -like 'releases.*' -or $_.Name -like 'RELEASES*' -or $_.Name -like 'assets.*' } |
            Copy-Item -Destination $feed

        if (-not $SkipMsi) {
            $msiProject = Join-Path $root 'build/msi/ShotIt.Msi.wixproj'
            $platform = if ($arch -eq 'arm64') { 'arm64' } else { 'x64' }
            Invoke-Checked "Build MSI ($rid)" {
                dotnet build $msiProject -c Release -p:Platform=$platform -p:ProductVersion=$Version -p:PublishDir=$publish --nologo -v q
            }
            $msi = Join-Path $root "build/msi/bin/$platform/Release/ShotIt.msi"
            if ($SignParams) {
                $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
                Invoke-Checked "Sign MSI ($rid)" { & $signtool sign @($SignParams -split ' (?=(?:[^"]*"[^"]*")*[^"]*$)' | ForEach-Object { $_.Trim('"') }) $msi }
            }
            Copy-Item $msi (Join-Path $release "ShotIt-$arch.msi")
        }
    }

    if (-not $SkipMsi) {
        # Group Policy templates that go with the MSI.
        Compress-Archive -Path (Join-Path $root 'build/msi/policy/*') -DestinationPath (Join-Path $release 'ShotIt-Policy-Templates.zip') -CompressionLevel Optimal
    }

    # --- hashes ---
    $hashes = Get-ChildItem $release -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content (Join-Path $release 'SHA256SUMS.txt') -Value $hashes -Encoding ascii

    # --- winget manifest (to be submitted to microsoft/winget-pkgs by the publisher) ---
    if ($PackageUrl) {
        $winget = Join-Path $release "winget/$Version"
        New-Item $winget -ItemType Directory -Force | Out-Null
        $installers = foreach ($rid in $Runtimes) {
            $arch = $rid.Substring(4)
            $file = "ShotIt-Setup-$arch.exe"
            $hash = (Get-FileHash (Join-Path $release $file) -Algorithm SHA256).Hash
            "- Architecture: $arch`n  InstallerUrl: $($PackageUrl.TrimEnd('/'))/$file`n  InstallerSha256: $hash"
        }
        $noBom = New-Object System.Text.UTF8Encoding($false)
        foreach ($template in Get-ChildItem (Join-Path $root 'build/winget') -Filter '*.yaml') {
            $text = (Get-Content $template.FullName -Raw).Replace('{{VERSION}}', $Version).Replace('{{PUBLISHER}}', $PublisherName).Replace('{{INSTALLERS}}', ($installers -join "`n"))
            # winget validation rejects a byte-order mark, which Windows PowerShell would add.
            [System.IO.File]::WriteAllText((Join-Path $winget $template.Name), $text, $noBom)
        }
    }

    Write-Host ''
    Write-Host "Release $Version is in $release" -ForegroundColor Green
    Get-ChildItem $release -File | ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
    if (-not $SignParams) { Write-Warning 'The artifacts are NOT code-signed. Windows SmartScreen will warn users until they are signed.' }
}
finally {
    Pop-Location
}
