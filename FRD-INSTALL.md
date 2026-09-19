# ArdUi 内置 FRD 部署

ArdUi v2.pre3 的 `install.ps1` 会同时部署 FRD v1.pre8，两端使用同一安装入口。FRD 位于 `%LOCALAPPDATA%\ArdUi\frd\v1.pre8`，由 ArdUi 按会话启动；安装过程不会启动 FRD，也不会保存 FRD 会话 token。

FRD v1.pre8 改为依赖框架发布，需用户自行安装 .NET 10 Runtime x64。ArdUi 本身继续依赖 .NET 8 Runtime x64。安装器在修改安装目录前检查两项运行时，缺少时明确提示，不下载运行时、不申请管理员权限。FRD 使用已发布的 v1.pre8 二进制，不重新编译本机源码。

安装脚本先逐一校验目标目录中的发布文件；完整时不再下载。需要修复时，优先复用 `%LOCALAPPDATA%\Programs\FRD\v1.pre8` 中每个文件都匹配发布 SHA-256 的完整运行目录，再检查 ArdUi 的 `cache`，最后下载并校验公开分发包。可通过 `ARDUI_FRD_SOURCE` 指定另一份已有运行目录；该目录仍须通过全部发布文件的哈希校验。缺失或损坏的运行依赖可通过重新执行安装命令修复，ArdUi 的 `data`、永久身份和授权列表保留。

运行包只包含 `FRD.exe`、公开的 `codec-config.json`、七个 FFmpeg DLL、FFmpeg 许可证和第三方说明。PDB、旧 ZIP、测试、命令行播放器不随包分发。FRD 通过 FFmpeg.AutoGen 直接调用 DLL，不依赖 `ffmpeg.exe`、`ffprobe.exe` 或 `ffplay.exe`。

最终分发包为 `FRD-v1.pre8-win-x64.tar.xz`，大小 75,285,360 字节（约 71.8 MiB），SHA-256 为 `35470b1634774605486a6154abe6555ec49c458224d9e1daec6c0c91d6570147`。归档及逐文件 SHA-256 固定在安装脚本中。安装器使用 Windows 自带的 `tar.exe` 解压，无需用户安装 7-Zip。

NJ 部署复用已验证的 FFmpeg 文件，仅上传压缩后的 FRD 应用文件（12,468,908 字节）及 ArdUi 部署增量。服务器按本地元数据重组完整包，最终归档哈希必须与本机一致后才能发布。此处部署增量仅用于到 NJ 的传输；客户端仍可下载完整 FRD 压缩包。

`tests/package_frd.py` 生成压缩包、逐文件清单和可供 NJ 重组的元数据。归档 SHA-256 与每个发布文件的 SHA-256 都固定在安装脚本中。`tests/install_frd.ps1` 在隔离目录验证缓存、完整目录复用、依赖修复和身份保留，不启动 FRD。

FFmpeg 的许可及第三方来源说明随运行文件保留。安装器不会把访问密码或会话 token 加入任何配置、缓存、manifest 或诊断文件。
