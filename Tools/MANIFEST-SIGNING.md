# ForgeFlow tool manifest

`tools.sha256` is the base/customer manifest. It deliberately excludes
`Tools/tensorrt/`, which is an optional NVIDIA vendor runtime and is not part of
the default customer build. Regenerate it after changing bundled tools:

```powershell
pwsh -File Tools/BuildToolsManifest.ps1
```

Release signing is intentionally external to the repository. Provide a PEM RSA
private key only in the release environment:

```powershell
pwsh -File Tools/BuildToolsManifest.ps1 -PrivateKeyPath $env:FORGEFLOW_MANIFEST_PRIVATE_KEY
```

The script writes `tools.sha256.sig`, a base64url RSA-SHA256 signature over the
exact UTF-8 manifest bytes. Never commit the private key. The public key used by
the customer verifier must be shipped separately in the signed release metadata.
