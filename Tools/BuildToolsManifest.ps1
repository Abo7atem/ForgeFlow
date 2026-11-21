param(
    [string]$ToolsRoot = $PSScriptRoot,
    [string]$PrivateKeyPath = ""
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $ToolsRoot 'tools.sha256'
$signaturePath = Join-Path $ToolsRoot 'tools.sha256.sig'
$root = (Resolve-Path $ToolsRoot).Path

$lines = Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object {
        $_.Name -notin @('tools.sha256', 'tools.sha256.sig') -and
        $_.FullName -notlike (Join-Path $root 'tensorrt\*')
    } |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        "$hash  $relative"
    }

$content = ($lines -join "`n") + "`n"
[System.IO.File]::WriteAllText($manifestPath, $content, [System.Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
        throw "Signing key was not found: $PrivateKeyPath"
    }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem((Get-Content -LiteralPath $PrivateKeyPath -Raw))
        $signature = $rsa.SignData(
            [System.Text.Encoding]::UTF8.GetBytes($content),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $encoded = [Convert]::ToBase64String($signature).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        [System.IO.File]::WriteAllText($signaturePath, $encoded + "`n", [System.Text.UTF8Encoding]::new($false))
        Write-Host "Signed manifest: $signaturePath"
    }
    finally {
        $rsa.Dispose()
    }
}

Write-Host "Manifest entries: $($lines.Count)"
Write-Host "Manifest: $manifestPath"
