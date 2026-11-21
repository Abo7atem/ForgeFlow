# ForgeFlow

ForgeFlow is a free Windows video workflow and editing application. The latest
customer installer is published in the
[ForgeFlowAPP Releases](https://github.com/Abo7atem/ForgeFlowAPP/releases/latest)
page. It has no account, license key, activation, trial counter, or processing
limit.

## Features

- Video editing with a multi-track timeline, preview, trimming, and export.
- Frame interpolation with RIFE and video enhancement with supported models.
- CPU, NVIDIA, and AMD processing paths where the system supports them.
- Export profiles for common video and social media workflows.

## Build from source

Requirements: Windows 10 or later, .NET 8 SDK, and the Windows Desktop SDK.

```powershell
dotnet build ForgeFlow.csproj -c Release
```

To create a self-contained Windows x64 installer, run `build_installer.bat`.
Some optional vendor runtimes are not included; see
[`docs/third-party-notices.md`](docs/third-party-notices.md).

## License and attribution

Original ForgeFlow source and assets are licensed under the MIT License in
[`LICENSE`](LICENSE). Keep the copyright and license notice with copies or
substantial portions. The MIT License allows commercial use and redistribution;
it does not require a promotional mention. If you share or build on ForgeFlow,
credit is appreciated: **ForgeFlow by A.Hatem T-Zone SYR**.

Bundled tools, libraries, and model weights keep their own licenses. Read the
third-party notices before redistributing the installer or its contents.

Project: <https://github.com/Abo7atem/ForgeFlow>

Author: <https://x.com/abo_7atemm>
