param([Parameter(Mandatory = $true)][string]$Root)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $user = $identity.User
    if ($null -eq $user) { throw 'Cannot establish current-user candidate ownership.' }
    if (-not [System.IO.Directory]::Exists($path)) {
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path))
        $security = [System.Security.AccessControl.DirectorySecurity]::new()
        $security.SetOwner($user)
        $security.SetAccessRuleProtection($true, $false)
        $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $user, [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow))
        [System.IO.FileSystemAclExtensions]::Create([System.IO.DirectoryInfo]::new($path), $security)
    }
    $directory = [System.IO.DirectoryInfo]::new($path)
    if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Candidate root cannot be a reparse point.' }
    $current = [System.IO.FileSystemAclExtensions]::GetAccessControl($directory,
        [System.Security.AccessControl.AccessControlSections]'Owner,Access')
    if (-not $user.Equals($current.GetOwner([System.Security.Principal.SecurityIdentifier])) -or -not $current.AreAccessRulesProtected) {
        throw 'Candidate root is not privately owned by the current user.'
    }
    foreach ($rule in $current.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and -not $user.Equals($rule.IdentityReference)) {
            throw 'Candidate root grants another principal access; selection refused.'
        }
    }
    [pscustomobject]@{ Root = $path; CurrentUserOnly = $true } | ConvertTo-Json
} finally { $identity.Dispose() }
