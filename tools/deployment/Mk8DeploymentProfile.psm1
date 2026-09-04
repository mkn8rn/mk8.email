Set-StrictMode -Version Latest

$script:Mk8ProfilePropertyNames = @(
    'Version'
    'ServerIPv4'
    'LanCidr'
    'TrustedAdminIPv4'
    'PublicIPv4'
    'SshKeyPath'
    'KnownHostsPath'
    'BackupDestination'
)

$script:Mk8TokenByProperty = [ordered]@{
    '@@MK8_SERVER_IPV4@@' = 'ServerIPv4'
    '@@MK8_LAN_CIDR@@' = 'LanCidr'
    '@@MK8_TRUSTED_ADMIN_IPV4@@' = 'TrustedAdminIPv4'
    '@@MK8_PUBLIC_IPV4@@' = 'PublicIPv4'
}

function Test-Mk8PathAtOrWithin {
    param(
        [Parameter(Mandatory)] [string]$Candidate,
        [Parameter(Mandatory)] [string]$Root
    )

    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $relativePath = [IO.Path]::GetRelativePath($rootPath, $candidatePath)
    if ($relativePath -eq '.') {
        return $true
    }

    $parentPrefix = "..$([IO.Path]::DirectorySeparatorChar)"
    $alternateParentPrefix = "..$([IO.Path]::AltDirectorySeparatorChar)"
    return $relativePath -ne '..' `
        -and -not $relativePath.StartsWith($parentPrefix, [StringComparison]::Ordinal) `
        -and -not $relativePath.StartsWith($alternateParentPrefix, [StringComparison]::Ordinal) `
        -and -not [IO.Path]::IsPathRooted($relativePath)
}

function Get-Mk8IPv4Details {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [Parameter(Mandatory)] [string]$Name
    )

    $address = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref]$address) `
        -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork `
        -or $address.ToString() -cne $Value) {
        throw "$Name must be a canonical IPv4 address."
    }

    $bytes = $address.GetAddressBytes()
    $number = ([uint64]$bytes[0] * 16777216) `
        + ([uint64]$bytes[1] * 65536) `
        + ([uint64]$bytes[2] * 256) `
        + [uint64]$bytes[3]
    return [pscustomobject]@{
        Address = $address
        Bytes = $bytes
        Number = $number
    }
}

function Test-Mk8PrivateIPv4 {
    param([Parameter(Mandatory)] [byte[]]$Bytes)

    return $Bytes[0] -eq 10 `
        -or ($Bytes[0] -eq 172 -and $Bytes[1] -ge 16 -and $Bytes[1] -le 31) `
        -or ($Bytes[0] -eq 192 -and $Bytes[1] -eq 168)
}

function Test-Mk8NumberInCidr {
    param(
        [Parameter(Mandatory)] [uint64]$Number,
        [Parameter(Mandatory)] [uint64]$Network,
        [Parameter(Mandatory)] [int]$PrefixLength
    )

    $blockSize = [uint64][Math]::Pow(2, 32 - $PrefixLength)
    return ($Number - ($Number % $blockSize)) -eq $Network
}

function Test-Mk8PublicIPv4 {
    param([Parameter(Mandatory)] [pscustomobject]$Details)

    if (Test-Mk8PrivateIPv4 -Bytes $Details.Bytes) {
        return $false
    }

    $blockedRanges = @(
        @('0.0.0.0', 8),
        @('100.64.0.0', 10),
        @('127.0.0.0', 8),
        @('169.254.0.0', 16),
        @('192.0.0.0', 24),
        @('192.0.2.0', 24),
        @('192.88.99.0', 24),
        @('198.18.0.0', 15),
        @('198.51.100.0', 24),
        @('203.0.113.0', 24),
        @('224.0.0.0', 4),
        @('240.0.0.0', 4)
    )
    foreach ($range in $blockedRanges) {
        $network = (Get-Mk8IPv4Details -Value $range[0] -Name 'Blocked range').Number
        if (Test-Mk8NumberInCidr -Number $Details.Number -Network $network -PrefixLength $range[1]) {
            return $false
        }
    }

    return $true
}

function Resolve-Mk8DeploymentProfilePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$RepositoryRoot
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    if (-not (Test-Path -LiteralPath $repositoryPath -PathType Container)) {
        throw 'The repository root does not exist.'
    }

    $profilePath = [IO.Path]::GetFullPath($Path)
    $secretsPath = [IO.Path]::GetFullPath((Join-Path $repositoryPath 'deploy/secrets'))
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    if (-not [IO.Path]::GetDirectoryName($profilePath).Equals($secretsPath, $comparison) `
        -or [IO.Path]::GetExtension($profilePath) -cne '.json') {
        throw 'The deployment profile must be a JSON file directly under deploy/secrets.'
    }
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        throw 'The deployment profile does not exist.'
    }

    foreach ($safePath in @($secretsPath, $profilePath)) {
        $item = Get-Item -LiteralPath $safePath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The deployment profile path cannot contain a reparse point.'
        }
    }

    return $profilePath
}

function Import-Mk8DeploymentProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [switch]$RequireConnectionFiles
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    $profilePath = Resolve-Mk8DeploymentProfilePath -Path $Path -RepositoryRoot $repositoryPath
    $rawProfile = [IO.File]::ReadAllText($profilePath, [Text.Encoding]::UTF8)
    try {
        $document = [Text.Json.JsonDocument]::Parse($rawProfile)
    }
    catch {
        throw 'The deployment profile is not valid JSON.'
    }

    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'The deployment profile must contain one JSON object.'
        }

        $values = [ordered]@{}
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $seen.Add($property.Name)) {
                throw "The deployment profile contains a duplicate property: $($property.Name)."
            }
            if (-not ($script:Mk8ProfilePropertyNames -ccontains $property.Name)) {
                throw "The deployment profile contains an unknown property: $($property.Name)."
            }

            if ($property.Name -ceq 'Version') {
                $version = 0
                if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::Number `
                    -or -not $property.Value.TryGetInt32([ref]$version)) {
                    throw 'Version must be an integer.'
                }
                $values[$property.Name] = $version
            }
            else {
                if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw "$($property.Name) must be a string."
                }
                $values[$property.Name] = $property.Value.GetString()
            }
        }

        foreach ($name in $script:Mk8ProfilePropertyNames) {
            if (-not $seen.Contains($name)) {
                throw "The deployment profile is missing a property: $name."
            }
        }
    }
    finally {
        $document.Dispose()
    }

    if ($values.Version -ne 1) {
        throw 'The deployment profile version is not supported.'
    }
    foreach ($name in $script:Mk8ProfilePropertyNames | Where-Object { $_ -cne 'Version' }) {
        if ([string]::IsNullOrWhiteSpace($values[$name])) {
            throw "$name cannot be empty."
        }
    }

    $server = Get-Mk8IPv4Details -Value $values.ServerIPv4 -Name 'ServerIPv4'
    $administrator = Get-Mk8IPv4Details -Value $values.TrustedAdminIPv4 -Name 'TrustedAdminIPv4'
    $public = Get-Mk8IPv4Details -Value $values.PublicIPv4 -Name 'PublicIPv4'

    $cidrMatch = [regex]::Match($values.LanCidr, '^(?<address>[^/]+)/(?<prefix>[0-9]{1,2})$')
    if (-not $cidrMatch.Success) {
        throw 'LanCidr must be a canonical IPv4 CIDR.'
    }
    $lanAddress = Get-Mk8IPv4Details -Value $cidrMatch.Groups['address'].Value -Name 'LanCidr'
    $prefixLength = [int]$cidrMatch.Groups['prefix'].Value
    if ($prefixLength -lt 8 -or $prefixLength -gt 30) {
        throw 'LanCidr must have a prefix length from 8 through 30.'
    }
    if (-not (Test-Mk8PrivateIPv4 -Bytes $lanAddress.Bytes)) {
        throw 'LanCidr must use a private IPv4 range.'
    }
    $blockSize = [uint64][Math]::Pow(2, 32 - $prefixLength)
    if (($lanAddress.Number % $blockSize) -ne 0) {
        throw 'LanCidr must start at its network address.'
    }
    foreach ($entry in @(
        @{ Name = 'ServerIPv4'; Details = $server },
        @{ Name = 'TrustedAdminIPv4'; Details = $administrator }
    )) {
        if (-not (Test-Mk8PrivateIPv4 -Bytes $entry.Details.Bytes) `
            -or -not (Test-Mk8NumberInCidr -Number $entry.Details.Number -Network $lanAddress.Number -PrefixLength $prefixLength)) {
            throw "$($entry.Name) must be inside LanCidr."
        }
        if ($entry.Details.Number -eq $lanAddress.Number `
            -or $entry.Details.Number -eq ($lanAddress.Number + $blockSize - 1)) {
            throw "$($entry.Name) cannot be a network or broadcast address."
        }
    }
    if ($server.Number -eq $administrator.Number) {
        throw 'ServerIPv4 and TrustedAdminIPv4 must be different.'
    }
    if (-not (Test-Mk8PublicIPv4 -Details $public)) {
        throw 'PublicIPv4 must be a globally routable IPv4 address.'
    }

    foreach ($name in @('SshKeyPath', 'KnownHostsPath', 'BackupDestination')) {
        if (-not [IO.Path]::IsPathFullyQualified($values[$name])) {
            throw "$name must be a fully qualified path."
        }
        $values[$name] = [IO.Path]::GetFullPath($values[$name])
        if (Test-Mk8PathAtOrWithin -Candidate $values[$name] -Root $repositoryPath) {
            throw "$name must be outside the repository."
        }
    }
    if ($values.SshKeyPath -ceq $values.KnownHostsPath) {
        throw 'SshKeyPath and KnownHostsPath must be different.'
    }
    if ((Test-Path -LiteralPath $values.BackupDestination) `
        -and -not (Test-Path -LiteralPath $values.BackupDestination -PathType Container)) {
        throw 'BackupDestination must be a directory.'
    }
    if ($RequireConnectionFiles) {
        foreach ($name in @('SshKeyPath', 'KnownHostsPath')) {
            if (-not (Test-Path -LiteralPath $values[$name] -PathType Leaf)) {
                throw "$name does not exist."
            }
            $item = Get-Item -LiteralPath $values[$name] -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$name cannot be a reparse point."
            }
        }
    }

    return [pscustomobject]$values
}

function New-Mk8RenderedDeployAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ProfilePath,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [Parameter(Mandatory)] [string]$Destination
    )

    $repositoryPath = [IO.Path]::GetFullPath($RepositoryRoot)
    $sourcePath = Join-Path $repositoryPath 'deploy'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        throw 'The deployment source directory does not exist.'
    }

    $destinationPath = [IO.Path]::GetFullPath($Destination)
    if (Test-Mk8PathAtOrWithin -Candidate $destinationPath -Root $repositoryPath) {
        throw 'The rendered deployment destination must be outside the repository.'
    }
    if (Test-Path -LiteralPath $destinationPath) {
        throw 'The rendered deployment destination already exists.'
    }

    $profile = Import-Mk8DeploymentProfile -Path $ProfilePath -RepositoryRoot $repositoryPath
    $created = $false
    try {
        [IO.Directory]::CreateDirectory($destinationPath) | Out-Null
        $created = $true
        $renderedDeployPath = Join-Path $destinationPath 'deploy'
        [IO.Directory]::CreateDirectory($renderedDeployPath) | Out-Null

        foreach ($sourceItem in Get-ChildItem -LiteralPath $sourcePath -Force) {
            if ($sourceItem.Name -ceq 'secrets') {
                continue
            }
            if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Deployment assets cannot contain a reparse point.'
            }
            if ($sourceItem.PSIsContainer) {
                $reparsePoint = Get-ChildItem -LiteralPath $sourceItem.FullName -Recurse -Force `
                    | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } `
                    | Select-Object -First 1
                if ($null -ne $reparsePoint) {
                    throw 'Deployment assets cannot contain a reparse point.'
                }
            }
            Copy-Item -LiteralPath $sourceItem.FullName -Destination $renderedDeployPath -Recurse -Force
        }

        if (Test-Path -LiteralPath (Join-Path $renderedDeployPath 'secrets')) {
            throw 'The rendered assets contain the private profile directory.'
        }

        $tokenCounts = @{}
        foreach ($token in $script:Mk8TokenByProperty.Keys) {
            $tokenCounts[$token] = 0
        }
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        foreach ($file in Get-ChildItem -LiteralPath $renderedDeployPath -File -Recurse -Force) {
            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
                continue
            }
            try {
                $content = $utf8.GetString($bytes)
            }
            catch {
                throw "A deployment asset is not valid UTF-8: $($file.Name)."
            }

            $rendered = $content
            foreach ($token in $script:Mk8TokenByProperty.Keys) {
                $count = [regex]::Matches($rendered, [regex]::Escape($token)).Count
                if ($count -gt 0) {
                    $tokenCounts[$token] += $count
                    $propertyName = $script:Mk8TokenByProperty[$token]
                    $rendered = $rendered.Replace($token, $profile.$propertyName, [StringComparison]::Ordinal)
                }
            }
            if ([regex]::IsMatch($rendered, '@@MK8_[A-Z0-9_]+@@')) {
                throw "A deployment asset contains an unknown token: $($file.Name)."
            }
            if ($rendered -cne $content) {
                [IO.File]::WriteAllText($file.FullName, $rendered, [Text.UTF8Encoding]::new($false))
            }
        }

        foreach ($token in $script:Mk8TokenByProperty.Keys) {
            if ($tokenCounts[$token] -eq 0) {
                throw "A required deployment token is missing: $token."
            }
        }

        return $destinationPath
    }
    catch {
        if ($created -and (Test-Path -LiteralPath $destinationPath)) {
            Remove-Item -LiteralPath $destinationPath -Recurse -Force
        }
        throw
    }
}

Export-ModuleMember -Function Import-Mk8DeploymentProfile, New-Mk8RenderedDeployAssets
