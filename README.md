# KomaSnap

KomaSnap 是一款轻量级漫画与电子书阅读器。支持导入 PDF、无 DRM 的 EPUB、CBZ 和图片 ZIP。Windows 版还支持通过本机 Suwayomi-Server 接入可选的漫画源扩展、在线阅读和批量下载有权保存的章节。

## 下载

- [Windows 下载与发行记录](https://github.com/krs097577-png/KomaSnap/releases)。在线漫画版需要完整解压发行包，保留程序旁边的 `suwayomi` 文件夹。阅读时按 Esc 返回书架，点窗口关闭按钮收进系统托盘。
- [Android 1.8.2（APK）](https://github.com/krs097577-png/KomaSnap/releases/download/v1.8.2/KomaSnap-Android-1.8.2.apk)。

## 源码与构建

| 平台 | 技术栈 | 运行或构建 |
| --- | --- | --- |
| [Windows](windows/) | WPF / .NET Framework | 在 Windows 下运行 PowerShell 脚本 `windows/Build.ps1`，需要 Windows 8.1 SDK 的 WinRT 元数据。生成文件位于 `outputs/`。 |
| [Android](android/) | Android Framework / Java | 最低 Android 7.0。使用 JDK 17 和 Android SDK 构建，见 [Android 构建说明](android/README.md)。 |
| [网页](web/) | HTML / CSS / JavaScript | 在 `web/` 运行 `node serve.mjs`，或将 `web/dist/` 放到静态站点。第三方依赖的许可证随 `web/dist/vendor/` 提供。 |

Windows 在线漫画功能需要 Suwayomi-Server v2.4.2373 及其 Java 运行环境；详见 [Windows 使用与构建说明](windows/README.md)。扩展由用户选择并确认安装，不随程序预装。不同来源的可用性取决于网站和网络，网站验证可能导致阅读失败；仅下载有权保存的内容。

Android 1.8.2 支持白色、护眼和夜间主题。在阅读器顶部选择「翻页方式 → 仿真卷页」可拖动卷页。覆盖安装已有版本时，需要沿用原签名密钥。

