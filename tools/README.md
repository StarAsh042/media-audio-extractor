# 内置第三方组件

本目录存放程序运行时依赖的两个命令行工具。**它们没有被提交到版本库**（体积过大：yt-dlp 约 17 MB、ffmpeg 约 84 MB，
且 GitHub 单文件限制 100 MB、不建议把二进制放进仓库），请用下面的脚本自动获取：

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-tools.ps1
```

脚本会下载并放置：

| 文件 | 来源 | 说明 |
|---|---|---|
| `tools\yt-dlp.exe` | [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe) | 解析并下载 1000+ 站点的音视频流（Unlicense） |
| `tools\ffmpeg.exe`  | [gyan.dev essentials build](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip) | 转码 / 裁剪 / 生成试听文件（GPL 构建） |

已存在且大小正常时会跳过，加 `-Force` 可强制重新下载。`build.ps1` 在缺少这两个文件时会自动调用本脚本。

## 许可证提示

- **yt-dlp**：The Unlicense（公有领域）。
- **ffmpeg**：gyan.dev 的 essentials 构建启用了 x264/x265 等 GPL 组件，属于 **GPL**。
  如果你要再分发打包好的 exe，请一并遵守 GPL（提供对应源码或获取途径）。
- 本仓库自身的代码为 MIT，见根目录 `LICENSE`。
