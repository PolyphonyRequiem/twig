param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'A full lowercase source commit is required.' }
$ExpectedPublisher = 'CN=Daniel Green, O=Daniel Green, L=Kirkland, S=wa, C=US'
$ExpectedProfileEku = '1.3.6.1.4.1.311.97.526284689.305431252.158505691.772257111'
$path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Executable)
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The candidate executable is missing.' }
$signature = Get-AuthenticodeSignature -LiteralPath $path
if ($signature.Status -ne 'Valid') { throw "Candidate Authenticode verification failed: $($signature.Status)" }
if ($signature.SignerCertificate.Subject -ne $ExpectedPublisher) { throw 'The candidate has an unexpected publisher.' }
$ekuValues = @($signature.SignerCertificate.Extensions |
    Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
    ForEach-Object { $_.EnhancedKeyUsages } |
    ForEach-Object { $_.Value })
if ($ekuValues -notcontains '1.3.6.1.4.1.311.97.1.0' -or $ekuValues -notcontains $ExpectedProfileEku) {
    throw 'The candidate is not signed by the approved public-trust profile.'
}
if ($null -eq $signature.TimeStamperCertificate) { throw 'The candidate has no trusted timestamp.' }
$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
if ($productVersion -notmatch ([regex]::Escape('+' + $SourceCommit) + '$')) {
    throw 'The signed executable does not carry the expected complete source commit.'
}
[pscustomobject]@{
    Status = $signature.Status.ToString()
    Publisher = $signature.SignerCertificate.Subject
    Issuer = $signature.SignerCertificate.Issuer
    TimestampIssuer = $signature.TimeStamperCertificate.Issuer
    ProfileEku = $ExpectedProfileEku
    ProductVersion = $productVersion
    Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 3
