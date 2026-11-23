# KomaSnap

KomaSnap 是一款轻量级漫画与电子书阅读器。支持导入 PDF、无 DRM 的 EPUB、CBZ 和图片 ZIP。

## 下载

- [Windows 1.8（EXE）](https://github.com/krs097577-png/KomaSnap/releases/download/windows-v1.8/KomaSnap-Windows.exe)。下载后直接运行。阅读时按 Esc 返回书架，点窗口关闭按钮收进系统托盘。
- [Android 1.8.2（APK）](https://github.com/krs097577-png/KomaSnap/releases/download/v1.8.2/KomaSnap-Android-1.8.2.apk)。

## 源码与构建

| 平台 | 技术栈 | 运行或构建 |
| --- | --- | --- |
| [Windows](windows/) | WPF / .NET Framework | 在 Windows 下运行 PowerShell 脚本 `windows/Build.ps1`，需要 Windows 8.1 SDK 的 WinRT 元数据。生成文件位于 `outputs/`。 |
| [Android](android/) | Android Framework / Java | 最低 Android 7.0。使用 JDK 17 和 Android SDK 构建，见 [Android 构建说明](android/README.md)。 |
| [网页](web/) | HTML / CSS / JavaScript | 在 `web/` 运行 `node serve.mjs`，或将 `web/dist/` 放到静态站点。第三方依赖的许可证随 `web/dist/vendor/` 提供。 |

Android 1.8.2 支持白色、护眼和夜间主题。在阅读器顶部选择「翻页方式 → 仿真卷页」可拖动卷页。覆盖安装已有版本时，需要沿用原签名密钥。
