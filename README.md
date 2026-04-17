# ForgeFlow Lite

ForgeFlow Lite is a lightweight Windows video enhancement and interpolation tool.

## Release Build

Use:

```powershell
powershell -ExecutionPolicy Bypass -File .\build_release.ps1
```

Then build the installer with Inno Setup:

```powershell
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' .\setup.iss
```

## Runtime Tools

The source repository intentionally does not include large runtime binaries such as:

- `ffmpeg.exe`
- `ffprobe.exe`
- `rife-ncnn-vulkan.exe`
- RIFE model `.bin` files

Keep these files locally under `ForgeFlow\Tools` for building release packages, or publish them through GitHub Releases instead of Git source.

## Security

Do not commit private signing keys or secret folders. ForgeFlow Lite uses a separate license signing key from 4TikoTool Pro.
