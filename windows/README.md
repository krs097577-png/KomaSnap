# Windows 版

运行发行包时，请完整解压并打开 `KomaSnap-Batch.exe`，保持旁边的 `suwayomi` 文件夹完整。主界面的“漫画源”用于安装经用户确认的扩展；“在线漫画”支持搜索、阅读及多选章节下载，下载的 CBZ 会加入本地书架。关闭窗口后程序可能仍在系统托盘中运行；右键托盘图标可以退出。

从源码构建需要 Windows、.NET Framework 编译器、Windows 8.1 SDK 的 `Windows.winmd` 和 PowerShell。运行 `windows/Build.ps1`，构建脚本会从 NuGet 下载并校验指定版本的 HTML 解析依赖。默认输出到 `outputs/KomaSnap.exe`。

在线功能还需要将 Suwayomi-Server v2.4.2373 Windows x64 发行包内的 `bin` 与 `jre` 放在可执行文件旁的 `suwayomi` 文件夹。可以参考仓库中的发布工作流，它会校验官方发行包的 SHA-256 后打包这些组件。Suwayomi-Server 采用 MPL-2.0，源代码和许可见 https://github.com/Suwayomi/Suwayomi-Server/tree/v2.4.2373 ；JRE 许可随其 `jre/legal` 分发。HTML 解析依赖的许可随构建资源提供。

扩展仓库本身不等于内容授权。请仅安装信任的扩展并确认有权下载内容；扩展安装有用户确认，但当前不验证 APK 签名。搜索目前仅取每个源第一页；部分网站可能因网络或 Cloudflare 验证而无法访问。

执行 `windows/Build.ps1 -BatchVerify` 可构建隔离的批量下载测试，测试入口需要一个可写的临时目录参数；该测试使用本地自制图片与模拟服务，不访问第三方漫画网站。

