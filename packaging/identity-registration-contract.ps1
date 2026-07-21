Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:EzyIdentityExitCodes = [ordered]@{
    Success = 0
    InvalidInput = 2
    PrerequisiteFailure = 10
    CodecHostFailure = 20
    MainIdentityFailure = 21
    RollbackFailure = 30
    RemovalFailure = 40
}

function Get-EzyIdentityExitCodes {
    return [PSCustomObject]$script:EzyIdentityExitCodes
}

function Get-EzyRegistrationPhysicalPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet('Container', 'Leaf')]
        [string]$PathType,

        [Parameter(Mandatory)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains([char]0)) {
        throw "$Label path is empty or invalid."
    }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "$Label path is invalid: '$Path'."
    }

    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (($PathType -eq 'Container' -and -not $item.PSIsContainer) -or
        ($PathType -eq 'Leaf' -and $item.PSIsContainer)) {
        throw "$Label must be a physical $PathType path: '$fullPath'."
    }

    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label path must not be a reparse point: '$($item.FullName)'."
    }

    $current = if ($item.PSIsContainer) { $item.Parent } else { $item.Directory }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label path must not traverse a reparse point: '$($current.FullName)'."
        }
        $current = $current.Parent
    }

    return $item.FullName
}

function New-EzyIdentityRegistrationPlan {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Register', 'Unregister')]
        [string]$Action,

        [Parameter(Mandatory)]
        [ValidateSet('CurrentUser', 'AllUsers')]
        [string]$Scope,

        [Parameter(Mandatory)]
        [string]$InstallDirectory,

        [string]$CodecHostPackagePath,

        [string]$ExternalPackagePath
    )

    $installRoot = Get-EzyRegistrationPhysicalPath $InstallDirectory 'Container' `
        'InstallDirectory'
    $applicationPath = Join-Path $installRoot 'ezyImageViewer.exe'
    if ($Action -ceq 'Register') {
        if (-not [IO.File]::Exists($applicationPath)) {
            throw "InstallDirectory does not contain ezyImageViewer.exe: '$installRoot'."
        }
        [void](Get-EzyRegistrationPhysicalPath $applicationPath 'Leaf' 'Application')
    }

    $codecHostPath = $null
    $externalPath = $null
    if ($Action -ceq 'Register') {
        $codecHostPath = Get-EzyRegistrationPhysicalPath $CodecHostPackagePath 'Leaf' `
            'CodecHostPackage'
        $externalPath = Get-EzyRegistrationPhysicalPath $ExternalPackagePath 'Leaf' `
            'ExternalPackage'
        if ([IO.Path]::GetExtension($codecHostPath) -cne '.msix' -or
            [IO.Path]::GetExtension($externalPath) -cne '.msix') {
            throw 'Registration package inputs must use the exact .msix extension.'
        }
        if ([string]::Equals($codecHostPath, $externalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'CodecHost and external identity package paths must be distinct.'
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CodecHostPackagePath) -or
        -not [string]::IsNullOrWhiteSpace($ExternalPackagePath)) {
        throw 'Unregister plans identify installed packages by identity, not input package paths.'
    }

    $steps = if ($Action -ceq 'Register') {
        @(
            [PSCustomObject][ordered]@{
                StepId = 'codec-host'
                Operation = if ($Scope -ceq 'CurrentUser') {
                    'AddCurrentUserPackage'
                }
                else {
                    'StageAndProvisionPackage'
                }
                Arguments = [PSCustomObject][ordered]@{
                    PackagePath = $codecHostPath
                }
                Rollback = 'RemoveOnlyIfIntroducedByTransaction'
                FailureExitCode = $script:EzyIdentityExitCodes.CodecHostFailure
            },
            [PSCustomObject][ordered]@{
                StepId = 'main-identity'
                Operation = if ($Scope -ceq 'CurrentUser') {
                    'AddCurrentUserExternalLocationPackage'
                }
                else {
                    'StageAndProvisionExternalLocationPackage'
                }
                Arguments = [PSCustomObject][ordered]@{
                    PackagePath = $externalPath
                    ExternalLocation = $installRoot
                }
                Rollback = 'RemoveOnlyIfIntroducedByTransaction'
                FailureExitCode = $script:EzyIdentityExitCodes.MainIdentityFailure
            }
        )
    }
    else {
        @(
            [PSCustomObject][ordered]@{
                StepId = 'main-identity'
                Operation = if ($Scope -ceq 'CurrentUser') {
                    'RemoveCurrentUserPackageByIdentity'
                }
                else {
                    'DeprovisionAndRemovePackageByIdentity'
                }
                Arguments = [PSCustomObject][ordered]@{
                    PackageName = 'GRTech.ezyImageViewer'
                }
                Rollback = 'None'
                FailureExitCode = $script:EzyIdentityExitCodes.RemovalFailure
            },
            [PSCustomObject][ordered]@{
                StepId = 'codec-host'
                Operation = if ($Scope -ceq 'CurrentUser') {
                    'RemoveCurrentUserPackageIfUnreferenced'
                }
                else {
                    'DeprovisionAndRemovePackageIfUnreferenced'
                }
                Arguments = [PSCustomObject][ordered]@{
                    PackageName = 'GRTech.ezyImageViewer.CodecHost'
                }
                Rollback = 'None'
                FailureExitCode = $script:EzyIdentityExitCodes.RemovalFailure
            }
        )
    }

    $plan = [PSCustomObject][ordered]@{
        SchemaVersion = 1
        Action = $Action
        Scope = $Scope
        Identity = [PSCustomObject][ordered]@{
            MainPackageName = 'GRTech.ezyImageViewer'
            ApplicationId = 'App'
            CodecHostPackageName = 'GRTech.ezyImageViewer.CodecHost'
        }
        InstallDirectory = $installRoot
        Steps = $steps
        Transaction = [PSCustomObject][ordered]@{
            StopOnFirstFailure = $true
            RollbackCompletedRegisterStepsInReverseOrder = $Action -ceq 'Register'
            PreservePreExistingPackages = $true
            RollbackFailureExitCode = $script:EzyIdentityExitCodes.RollbackFailure
        }
    }

    Assert-EzyIdentityRegistrationPlan $plan
    return $plan
}

function Assert-EzyIdentityRegistrationPlan {
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Plan
    )

    if ($Plan.SchemaVersion -ne 1 -or
        $Plan.Action -notin @('Register', 'Unregister') -or
        $Plan.Scope -notin @('CurrentUser', 'AllUsers')) {
        throw 'Identity registration plan header is invalid.'
    }
    if ($Plan.Identity.MainPackageName -cne 'GRTech.ezyImageViewer' -or
        $Plan.Identity.ApplicationId -cne 'App' -or
        $Plan.Identity.CodecHostPackageName -cne 'GRTech.ezyImageViewer.CodecHost') {
        throw 'Identity registration plan contains an unexpected package identity.'
    }
    if (@($Plan.Steps).Count -ne 2) {
        throw 'Identity registration plan must contain exactly two ordered steps.'
    }

    $expectedOrder = if ($Plan.Action -ceq 'Register') {
        @('codec-host', 'main-identity')
    }
    else {
        @('main-identity', 'codec-host')
    }
    for ($index = 0; $index -lt $expectedOrder.Count; $index++) {
        if ($Plan.Steps[$index].StepId -cne $expectedOrder[$index]) {
            throw "Identity registration step order mismatch at index $index."
        }
    }

    if (-not $Plan.Transaction.StopOnFirstFailure -or
        -not $Plan.Transaction.PreservePreExistingPackages -or
        $Plan.Transaction.RollbackFailureExitCode -ne
            $script:EzyIdentityExitCodes.RollbackFailure) {
        throw 'Identity registration transaction policy is invalid.'
    }
    if ($Plan.Action -ceq 'Register') {
        if (-not $Plan.Transaction.RollbackCompletedRegisterStepsInReverseOrder -or
            @($Plan.Steps | Where-Object {
                    $_.Rollback -cne 'RemoveOnlyIfIntroducedByTransaction'
                }).Count -ne 0) {
            throw 'Register plan rollback policy is invalid.'
        }
        if ($Plan.Steps[1].Arguments.ExternalLocation -cne $Plan.InstallDirectory) {
            throw 'External location must exactly match the normalized install directory.'
        }
    }
    elseif ($Plan.Transaction.RollbackCompletedRegisterStepsInReverseOrder -or
        @($Plan.Steps | Where-Object { $_.Rollback -cne 'None' }).Count -ne 0) {
        throw 'Unregister plan must not claim reversible package removal.'
    }

    $exitCodes = @($script:EzyIdentityExitCodes.Values)
    if (@($exitCodes | Sort-Object -Unique).Count -ne $exitCodes.Count -or
        $script:EzyIdentityExitCodes.Success -ne 0) {
        throw 'Identity registration exit codes must be unique and reserve zero for success.'
    }
}

function ConvertTo-EzyIdentityRegistrationPlanJson {
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Plan
    )

    Assert-EzyIdentityRegistrationPlan $Plan
    return ($Plan | ConvertTo-Json -Depth 8 -Compress)
}
