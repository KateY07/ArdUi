# ArdUi v2.pre3 / FRD v1.pre8

2026-09-19。本次仅升级客户端 FRD 依赖及发布版本，不合入第一轮性能实验，不修改锁定 README、ARD 协议、目录服务或中继服务。

## 发布内容

- ArdUi v2.pre3：.NET 8、依赖框架的单 EXE；SHA-256 `3e59c4f84f04fea1eb791bf4983f1f3f9b7e99d3b73517870a43ef17989322b9`。
- FRD v1.pre8：复用本机已发布文件，用户自行安装 .NET 10 Runtime x64；安装器在修改目录或下载前检查运行时。
- FRD 压缩包：75,285,360 字节，SHA-256 `35470b1634774605486a6154abe6555ec49c458224d9e1daec6c0c91d6570147`。
- 安装脚本：ASCII、无 BOM，SHA-256 `6c7fd75fae07a23f193153c37cc7c3da9ca51af590f946e1a589c5ea81eeb17c`。
- 底层 ARD 仍为 2.0.0-pre.6，哈希仍为 `04ebed96baecc2fd5b67318b1d02742f777b0351c84ee5b1c1b163a05dc98b5d`。

## 本地验证

- 正式 Release 单文件发布通过；身份创建/重装保留/损坏拒绝与 Avalonia 无头界面通过。
- 加密、重放拒绝、TCP/UDP、多路复用、10 MiB 完整性、原连接切换/回退、空闲 UDP 恢复、半关闭与诊断脱敏通过。
- FRD 适配协议通过：临时鉴权、UDP 端口重写、视频/诊断/反馈、input/cursor/clipboard 透传、断开清理。协议测试不注入系统输入或操作系统剪贴板。
- 真实 FRD v1.pre8：第二个隔离环境 `dist/v2-integration-20260919-214148` 基础路径接收/渲染 399 帧、候选中继 392 帧，撤销后关闭相关进程和权限。两个阶段依次运行，不代表业务多路径同时收帧。
- 第一个环境基础路径接收/渲染 320 帧，但候选请求出现一次 HTTP 发送错误。保留 `dist/v2-integration-20260919-213914` 诊断；未修改候选策略，全新隔离环境复测通过。
- Windows PowerShell 5.1 安装回归通过：含空格目录、缓存零下载、重复安装、身份/授权/被控设置保留、损坏/缺失依赖修复、本地完整 FRD 目录复用、坏归档重新下载、错误 SHA-256 三次拒绝。
- 模拟缺少 .NET 10：在安装目录创建或下载之前明确拒绝，并提示用户自行安装；不自动下载 .NET。

## 上传与发布校验

FRD 变化应用包 12,468,908 字节，ArdUi 增量及重组清单包 260,254 字节；合计 12,729,162 字节（约 12.14 MiB）。分块上传显示进度、速度和预计剩余时间，NJ 合并后两包 SHA-256 均校验通过。复用 NJ 的 FFmpeg 文件前逐一检查哈希。

NJ 从原 v2.pre2 重建的新 ArdUi EXE 已匹配本机哈希。FRD 完整包必须匹配本地固定哈希；`server/publish-frd.sh` 再检查完整 manifest 后才能发布。发布保留旧版二进制，并备份旧安装脚本和页面。

线上验证使用 `tests/verify_client_release.py --version v2.pre3` 检查安装脚本/页面完整哈希、公开 manifest、`/ardui` 页面、二进制 HEAD 大小与 Range 内容；`tests/install_http.ps1` 用 Windows PowerShell 5.1 验证真实 `irm` 返回文本的版本、哈希和无 BOM 编码。

2026-09-19 13:48:10 UTC 已发布到 NJ，以上公开 HTTPS 校验全部通过。旧安装入口备份于 `/var/backups/ardui-before-v2.pre3-20260919T134810Z`；旧版本二进制保留。8080/8090 端口及原有目录服务保持运行，本次未重启或替换它们。
