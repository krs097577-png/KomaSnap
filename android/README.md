# KomaSnap Android — 工程说明

KomaSnap Android 1.8.2 是使用原生 Android Java 开发的漫画与电子书阅读器。桌面对应源码位于 [windows](../windows/)。

- 无 Gradle / AGP / AndroidX / Kotlin / Material —— 只用 Android Framework API。
- 构建链：`aapt2` → `javac` → `d8` → `jar` → `zipalign` → `apksigner`（脚本见 `tools/`）。
- 启动图标资源已放在 `app/res`，原始素材在 `app/icon-source.png`。
- 设备最低 Android 7.0（API 24），编译/目标 API 34。

## 目录

```
app/
  AndroidManifest.xml
  res/                       主题、按钮/进度条 drawable、自适应图标、字符串
  src/com/komasnap/reader/
    Book.java                页面模型 + (页, 渲染宽度) LRU 位图缓存（限 16 张/160 MB）
    ArchiveBook.java         ZIP/CBZ/EPUB 解析（自然排序、OPF spine、XHTML→文本/图片、加密检测）
    PdfBook.java             PdfRenderer 封装（尺寸探测、失败后重建一次）
    TextPages.java           EPUB 文本分页（760×1080、58×48 边距、38/49pt 行网格）
    Bitmaps.java             图片尺寸探测与按目标宽度采样解码
    XmlText.java / HtmlEntities.java  容错 XML/HTML 文本与属性解析
    NaturalSort.java         移植 Windows StrCmpLogicalW（2.jpg < 10.jpg）
    Streams.java             带大小上限的流读取
    Store.java               应用私有书籍/封面/状态目录，导入去重
    Shelf.java               书架目录（TSV）、封面缓存
    State.java               设置与阅读位置（settings.properties + sha256.page）
    Books.java               按书名打开
    ReaderController.java    阅读界面与控制器之间的契约
    PagedReader.java         单/双页阅读视图（适配/适宽、缩放、拖动翻页、点击区、错误页）
    ScrolledReader.java      连续纵向阅读（可见页窗口解码、惯性滚动）
    ReaderLayout.java        在分页/滚动两种视图间切换
    ReaderActivity.java      阅读页：顶栏选项、底栏页码/进度、全屏、翻页方式、状态保存
    ShelfActivity.java       书架：导入（SAF 多选）、封面、进度、长按删除、外部分享打开
verify/
  stubs/                     JVM 测试用的 android.* 最小桩（Bitmap/Paint/StaticLayout…）
  src/…/LogicTest.java       48 项逻辑测试
tools/
  build-full.ps1            完整构建（推荐）
  MakeIcon.java             用 Java2D 生成各密度启动图标
docs/使用说明.md             安装与功能对照
```

## 与桌面版源码的对应关系

| 桌面版（C#） | 安卓版（Java） |
| --- | --- |
| `ReaderBook`（缓存、`MakeSpreads`、渲染宽度 128 对齐/上限 3200） | `Book` |
| `ArchiveBook`（CBZ/EPUB、`StrCmpLogicalW`、FlowDocument 分页） | `ArchiveBook` + `TextPages` + `NaturalSort` |
| `PdfBook`（`Windows.Data.Pdf`） | `PdfBook`（`android.graphics.pdf.PdfRenderer`） |
| `ReaderState`（settings.xml、`SHA256(路径).page`） | `State`（properties + `SHA256(书名).page`） |
| `Bookshelf`（bookshelf.xml、covers/*.png） | `Shelf`（bookshelf.tsv、covers/*.png） |
| `Reader.xaml` + `Reader.cs`（单/双页、方向、封面、适宽、缩放、翻页、滚动、全屏） | `ReaderActivity` + `ReaderLayout` + `PagedReader` + `ScrolledReader` |
| `PageTurnEffect` / `PageSlideEffect`（WPF 3D 卷页、滑动） | `PagedReader` 的横向滑动与 `PageCurl` 的触控卷页 |
| `ContinuousReader`（Canvas 纵向排版、可见页 ±2） | `ScrolledReader` |

保持一致的细节：双页配对规则（宽页不配对、封面单页）、渲染宽度 quantization、
阅读位置节流保存（400 ms）、书架按 `lastOpened` 再 `added` 排序、错误页占位色
（DarkSlateGray）、纸张 FloralWhite、强调色 `#F6CD81`、面板色 `#22252A` 等。

## 构建

```powershell
./tools/build-full.ps1 -Jdk "D:/path/to/jdk17" `
    -BuildTools "D:/path/to/android-sdk/build-tools/34.0.0" `
    -AndroidJar "D:/path/to/android-sdk/platforms/android-34/android.jar" `
    -Keystore "D:/path/to/signing.jks"
```

要求 Windows、PowerShell 7、JDK 17、Android platform-34 和 build-tools 34。
签名前设置 `KOMASNAP_SIGN_STORE_PASSWORD`。密钥口令不同时设置 `KOMASNAP_SIGN_KEY_PASSWORD`。
构建升级包时，用已安装版本对应的签名密钥。

脚本输出已签名的 `build/<构建编号>/KomaSnap-Android-1.8.2.apk` 路径。

## 验证

```powershell
$sources = @(Get-ChildItem verify/stubs -Recurse -Filter '*.java' | ForEach-Object FullName)
$names = 'Book','NaturalSort','ArchiveBook','Bitmaps','HtmlEntities','Streams','State','TextPages','XmlText'
$sources += @($names | ForEach-Object { "app/src/com/komasnap/reader/$_.java" })
javac -encoding UTF-8 -d verify/classes @sources verify/src/com/komasnap/reader/LogicTest.java
java -cp verify/classes com.komasnap.reader.LogicTest
```

JVM 测试覆盖缓存并发、双页规则、文件排序和 EPUB 解析。触屏动画和界面视觉效果需要在 Android 设备上验证。
