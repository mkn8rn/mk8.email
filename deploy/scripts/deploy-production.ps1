#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$ProfilePath,
    [switch]$Activate
)

$ErrorActionPreference = 'Stop'

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "$FilePath exceeded its $TimeoutSeconds second limit."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($stdout) {
        Write-Host $stdout.TrimEnd()
    }
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode).`n$stderr"
    }
    if ($stderr) {
        Write-Host $stderr.TrimEnd()
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$profileModulePath = Join-Path $repositoryRoot 'tools/deployment/Mk8DeploymentProfile.psm1'
Import-Module $profileModulePath -Force
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $repositoryRoot 'deploy/secrets/production-profile.json'
}
$profile = Import-Mk8DeploymentProfile `
    -Path $ProfilePath `
    -RepositoryRoot $repositoryRoot `
    -RequireConnectionFiles
$Target = "root@$($profile.ServerIPv4)"
$KeyPath = $profile.SshKeyPath
$KnownHostsPath = $profile.KnownHostsPath

$taskRoot = 'D:\temp\mk8.email\deploy-production'
$runRoot = Join-Path $taskRoot ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

try {
    $env:DOTNET_CLI_HOME = Join-Path $runRoot 'home'
    $env:NUGET_PACKAGES = Join-Path $runRoot 'packages'
    $env:TEMP = Join-Path $runRoot 'tmp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $artifactsRoot = Join-Path $runRoot 'artifacts'
    [IO.Directory]::CreateDirectory($env:DOTNET_CLI_HOME) | Out-Null
    [IO.Directory]::CreateDirectory($env:NUGET_PACKAGES) | Out-Null
    [IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
    [IO.Directory]::CreateDirectory($artifactsRoot) | Out-Null

    Invoke-BoundedProcess dotnet @('restore', 'mk8.email.slnx', '--locked-mode', '--artifacts-path', $artifactsRoot) 600 $repositoryRoot
    Invoke-BoundedProcess dotnet @('build', 'mk8.email.slnx', '--configuration', 'Release', '--no-restore', '--artifacts-path', $artifactsRoot, '--property:TreatWarningsAsErrors=true', '--property:ContinuousIntegrationBuild=true', '--property:UseSharedCompilation=false') 600 $repositoryRoot
    Invoke-BoundedProcess dotnet @('test', 'mk8.email.Application.Tests\mk8.email.Application.Tests.csproj', '--configuration', 'Release', '--no-build', '--artifacts-path', $artifactsRoot, '--logger', 'trx;LogFileName=application.trx', '--results-directory', (Join-Path $runRoot 'test-results')) 600 $repositoryRoot
    Invoke-BoundedProcess dotnet @('test', 'mk8.email.Infrastructure.Tests\mk8.email.Infrastructure.Tests.csproj', '--configuration', 'Release', '--no-build', '--artifacts-path', $artifactsRoot, '--logger', 'trx;LogFileName=infrastructure.trx', '--results-directory', (Join-Path $runRoot 'test-results')) 600 $repositoryRoot

    $cliOutput = Join-Path $runRoot 'release\cli'
    $adminOutput = Join-Path $runRoot 'release\admin'
    Invoke-BoundedProcess dotnet @('publish', 'mk8.email.CLI\mk8.email.Application.CLI.csproj', '--configuration', 'Release', '--no-build', '--artifacts-path', $artifactsRoot, '--output', $cliOutput, '--property:UseAppHost=false') 600 $repositoryRoot
    Invoke-BoundedProcess dotnet @('publish', 'mk8.email.PublicAPI\mk8.email.PublicAPI.csproj', '--configuration', 'Release', '--no-build', '--artifacts-path', $artifactsRoot, '--output', $adminOutput, '--property:UseAppHost=false') 600 $repositoryRoot

    $releaseArchive = Join-Path $runRoot 'mk8email-release.tar.gz'
    Invoke-BoundedProcess tar.exe @('--create', '--gzip', "--file=$releaseArchive", "--directory=$(Join-Path $runRoot 'release')", 'cli', 'admin') 120 $repositoryRoot

    $assetArchive = Join-Path $runRoot 'mk8email-assets.tar.gz'
    $renderedAssetsRoot = Join-Path $runRoot 'rendered-assets'
    New-Mk8RenderedDeployAssets `
        -ProfilePath $ProfilePath `
        -RepositoryRoot $repositoryRoot `
        -Destination $renderedAssetsRoot | Out-Null
    Invoke-BoundedProcess tar.exe @('--create', '--gzip', "--file=$assetArchive", '--directory', $renderedAssetsRoot, 'deploy') 120 $repositoryRoot
    $releaseDigest = (Get-FileHash -LiteralPath $releaseArchive -Algorithm SHA256).Hash
    $assetDigest = (Get-FileHash -LiteralPath $assetArchive -Algorithm SHA256).Hash
    $releaseMaterial = [Text.Encoding]::ASCII.GetBytes("$releaseDigest`n$assetDigest`n")
    $releaseId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($releaseMaterial)).Substring(0, 16).ToLowerInvariant()

    $remoteRoot = "/run/mk8email-deploy-$releaseId"
    $remoteScript = Join-Path $runRoot 'remote-install.sh'
    $activationRequested = if ($Activate) { 'true' } else { 'false' }
    $remoteScriptText = @"
#!/bin/sh
set -eu
remote_root='$remoteRoot'
release_id='$releaseId'
activate_requested='$activationRequested'
was_active=false
changes_started=false
apt_sources_changing=false
backup_path=
previous_release=
apt_source_backup=

restore_apt_sources() {
    restore_status=0
    rm -f -- /etc/apt/sources.list \
        /etc/apt/sources.list.d/debian.sources || restore_status=1
    if [ -f "`$apt_source_backup/sources.list" ]; then
        cp --archive "`$apt_source_backup/sources.list" /etc/apt/sources.list \
            || restore_status=1
    fi
    if [ -f "`$apt_source_backup/debian.sources" ]; then
        cp --archive "`$apt_source_backup/debian.sources" \
            /etc/apt/sources.list.d/debian.sources || restore_status=1
    fi
    return "`$restore_status"
}

finish() {
    deployment_status=`$?
    trap - EXIT HUP INT TERM
    if [ "`$deployment_status" -ne 0 ] && [ "`$apt_sources_changing" = true ]; then
        printf '%s\n' "The deployment failed. The prior APT sources will be restored." >&2
        set +e
        restore_apt_sources
        apt_rollback_status=`$?
        if [ "`$apt_rollback_status" -ne 0 ]; then
            printf '%s\n' "APT source rollback failed. Immediate operator action is required." >&2
        fi
        set -e
    fi
    if [ "`$deployment_status" -ne 0 ] && [ "`$changes_started" = true ] && [ "`$was_active" = true ]; then
        printf '%s\n' "The deployment failed. The prior release will be restored." >&2
        set +e
        /usr/local/sbin/deactivate-mail-stack
        tar --extract --gzip --file="`$backup_path/configuration.tar.gz" --directory=/
        ln -sfnT "`$previous_release" /opt/mk8email/current
        systemctl daemon-reload
        nft -f /etc/nftables.conf
        systemctl reload nginx.service
        systemctl restart fail2ban.service
        /usr/local/sbin/activate-mail-stack
        rollback_status=`$?
        if [ "`$rollback_status" -ne 0 ]; then
            printf '%s\n' "Automatic rollback failed. Immediate operator action is required." >&2
        fi
        case "`$release_id" in
            *[!0-9a-f]*|'') ;;
            *) rm -rf -- "/opt/mk8email/releases/`$release_id" ;;
        esac
        set -e
    fi
    case "`$remote_root" in
        /run/mk8email-deploy-*) rm -rf -- "`$remote_root" ;;
    esac
    exit "`$deployment_status"
}
trap finish EXIT
trap 'exit 130' HUP INT TERM

install -d -o root -g root -m 0700 "`$remote_root/assets"
tar --extract --gzip --file="`$remote_root/mk8email-assets.tar.gz" --directory="`$remote_root/assets"
chown root:root \
    "`$remote_root/assets/deploy/prerequisites/debian-13-runtime.txt" \
    "`$remote_root/assets/deploy/prerequisites/debian-13-sources.list" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"
chmod 0644 \
    "`$remote_root/assets/deploy/prerequisites/debian-13-runtime.txt" \
    "`$remote_root/assets/deploy/prerequisites/debian-13-sources.list" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"

if [ -f /etc/mk8email/mail-stack-ready ]; then
    was_active=true
    previous_release=`$(readlink -f /opt/mk8email/current)
    case "`$previous_release" in
        /opt/mk8email/releases/*) ;;
        *) printf '%s\n' "The current release path is not valid." >&2; exit 1 ;;
    esac
    timeout 180s apt-get update
    /usr/local/sbin/verify-host-prerequisites
    backup_path=`$(/usr/local/sbin/mk8-backup)
    case "`$backup_path" in
        /var/backups/mk8/20??????T??????Z) ;;
        *) printf '%s\n' "The deployment backup path is not valid." >&2; exit 1 ;;
    esac
elif [ -x /usr/local/sbin/verify-host-prerequisites ] \
    && [ -d /usr/local/share/mk8email/prerequisites ]; then
    timeout 180s apt-get update
    /usr/local/sbin/verify-host-prerequisites
fi

apt_source_backup="`$remote_root/apt-source-backup"
install -d -o root -g root -m 0700 "`$apt_source_backup"
for managed_source in \
    /etc/apt/sources.list \
    /etc/apt/sources.list.d/debian.sources; do
    if [ -L "`$managed_source" ] \
        || { [ -e "`$managed_source" ] && [ ! -f "`$managed_source" ]; }; then
        printf '%s\n' "A managed APT source path is unsafe." >&2
        exit 1
    fi
done
if [ -f /etc/apt/sources.list ]; then
    cp --archive /etc/apt/sources.list "`$apt_source_backup/sources.list"
fi
if [ -f /etc/apt/sources.list.d/debian.sources ]; then
    cp --archive /etc/apt/sources.list.d/debian.sources \
        "`$apt_source_backup/debian.sources"
fi
apt_sources_changing=true
sh "`$remote_root/assets/deploy/scripts/configure-apt-sources" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"
timeout 180s apt-get update
sh "`$remote_root/assets/deploy/scripts/verify-host-prerequisites" \
    "`$remote_root/assets/deploy/prerequisites"
sh "`$remote_root/assets/deploy/tests/apt_sources_smoke" \
    "`$remote_root/assets/deploy/scripts/configure-apt-sources" \
    "`$remote_root/assets/deploy/prerequisites/debian.sources"

if [ -n "`$backup_path" ] && [ -s /etc/mk8email/secrets/backup-age-recipient ]; then
    backup_name=`${backup_path##*/}
    encrypted_name="`$backup_name.tar.gz.age"
    if [ ! -s "/var/backups/mk8-export/`$encrypted_name" ] \
        || [ ! -s "/var/backups/mk8-export/`$encrypted_name.sha256" ]; then
        bash "`$remote_root/assets/deploy/scripts/mk8-export-backup" "`$backup_name" >/dev/null
    fi
    (cd /var/backups/mk8-export && sha256sum --check --status "`$encrypted_name.sha256")
fi

changes_started=true
sh "`$remote_root/assets/deploy/scripts/install-native-release" "`$remote_root/mk8email-release.tar.gz" '$releaseId'
sh "`$remote_root/assets/deploy/scripts/install-mail-stack" "`$remote_root/assets"
if [ -n "`$previous_release" ]; then
    /usr/local/sbin/prune-native-releases "`$previous_release"
else
    /usr/local/sbin/prune-native-releases
fi
/usr/local/lib/mk8email/tests/release_retention_smoke
if [ "`$activate_requested" = true ] || [ "`$was_active" = true ]; then
    /usr/local/sbin/activate-mail-stack
    /usr/local/lib/mk8email/tests/management_cli_smoke
    /usr/local/lib/mk8email/tests/certificate_deploy_smoke
    /usr/local/lib/mk8email/tests/multi_domain_smoke
fi
apt_sources_changing=false
"@
    [IO.File]::WriteAllText($remoteScript, $remoteScriptText, [Text.UTF8Encoding]::new($false))

    $sshOptions = @(
        '-i', $KeyPath,
        '-o', 'BatchMode=yes',
        '-o', 'ConnectTimeout=10',
        '-o', 'ServerAliveInterval=10',
        '-o', 'ServerAliveCountMax=2',
        '-o', 'StrictHostKeyChecking=yes',
        '-o', "UserKnownHostsFile=$KnownHostsPath",
        '-o', 'LogLevel=ERROR'
    )
    Invoke-BoundedProcess ssh.exe ($sshOptions + @($Target, "install -d -o root -g root -m 0700 '$remoteRoot'")) 60 $repositoryRoot
    Invoke-BoundedProcess scp.exe ($sshOptions + @($releaseArchive, "$Target`:$remoteRoot/mk8email-release.tar.gz")) 180 $repositoryRoot
    Invoke-BoundedProcess scp.exe ($sshOptions + @($assetArchive, "$Target`:$remoteRoot/mk8email-assets.tar.gz")) 180 $repositoryRoot
    Invoke-BoundedProcess scp.exe ($sshOptions + @($remoteScript, "$Target`:$remoteRoot/remote-install.sh")) 60 $repositoryRoot
    Invoke-BoundedProcess ssh.exe ($sshOptions + @($Target, "timeout 900s sh '$remoteRoot/remote-install.sh'")) 930 $repositoryRoot

    Write-Host "Deployed release $releaseId."
}
finally {
    $resolvedTaskRoot = [IO.Path]::GetFullPath($taskRoot).TrimEnd('\') + '\'
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if ($resolvedRunRoot.StartsWith($resolvedTaskRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRunRoot)) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
