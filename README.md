# ForgeFlow

ForgeFlow is a Windows desktop app for improving video with frame interpolation, AI upscaling, and subject-focus processing.

## Features

- Interpolate frames with RIFE v4.26.
- Upscale frames with Real-CUGAN or Real-ESRGAN models.
- Apply subject-focus processing with MODNet.
- Process and export video through FFmpeg.
- Review processing progress and select the order of interpolation and upscaling.

## Build

Requirements: Windows and the .NET 8 SDK.

```powershell
dotnet build ForgeFlow.csproj -c Release
```

The `Tools` directory contains the runtime tools and model files used by the app. Their licenses are separate from ForgeFlow's license; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistributing them.

## License

ForgeFlow's original source code is released under the MIT License. You may use, modify, distribute, and sell copies, provided you preserve the copyright and license notice. Third-party components remain under their own licenses.

## Contributions

Issues and pull requests are welcome. Please include a clear description of the change and any steps needed to reproduce a bug.

Project author: [A.Hatem T-Zone SYR (@abo_7atemm)](https://x.com/abo_7atemm).

---

### العربية

ForgeFlow تطبيق ويندوز لمعالجة الفيديو، يتضمن تحسين الحركة بإضافة إطارات، ورفع الدقة بالذكاء الاصطناعي، ومعالجة تركيز العنصر.

المصدر الأصلي للمشروع مرخّص برخصة MIT، ويسمح بالاستخدام والتعديل وإعادة التوزيع والبيع مع إبقاء إشعار حقوق النشر والترخيص. مكوّنات الطرف الثالث لها تراخيصها الخاصة؛ راجع [إشعارات تراخيص الطرف الثالث](THIRD-PARTY-NOTICES.md).
