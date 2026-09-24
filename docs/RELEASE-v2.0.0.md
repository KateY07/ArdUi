# ArdUi V2 正式版（v2.0.0）

2026-09-22，本机离线发布。

沿用 v2.pre11 的功能实现，更新应用、安装入口及组件包版本号为 2.0.0；打包器支持正式版版本号。ARD v3.pre1、FRD v1.pre13 及其文件哈希保持不变。没有更改锁定 README 或正在运行的双机安装。

- 安装包：`D:\pub\ArdUi\v2.0.0.zip`
- 校验文件：`D:\pub\ArdUi\v2.0.0.zip.sha256`
- ZIP SHA-256：`ccfdd60775be0681eb3c8a18d37b445678ed9cc06546c2e2099bf5a693e4e40e`
- ArdUi.exe SHA-256：`3373192012d09f504755e8b61409698ab742d3d7b2e3c1768258b28b28f8afd0`

解压运行 `install.bat`，全离线安装或升级。用户需预先安装 .NET 8 和 .NET 10 x64 运行时。

## 验证

- 正式版 EXE 无头自检通过：身份创建、重装保留、损坏身份拒绝、FRD 路径选择及 Avalonia 布局渲染。日志：`dist/v2.0.0-self-test.log`。
- Windows PowerShell 5.1 下，从实际 pre11 离线包升级正式包，再重复安装，全部通过；配置、密钥、机器编号及授权列表保持不变，零下载请求。日志：`dist/v2.0.0-offline-upgrade.log`；结果：`dist/offline-upgrade-v2.0.0-07383618f14d4cee9b68f8df3bf00b4d/result.json`。
- FRD 功能联调依据 [pre11 正式二进制联调记录](FRD-PRE13-VALIDATION-20260922.md)，本轮仅改版本和正式版打包支持，未重复双机交互测试。真实键鼠和剪贴板人工验收边界仍按该记录说明。
- 内部组件打包出现 NU5104 提示：正式 ArdUi 包依赖预发布名称的 ARD/FRD。为保留依赖实际版本号未改名；本次完整离线包已验证，没有发布到 NuGet。

新发行文件使用禁止覆盖方式创建，旧版发行包保留。本轮没有进行 GitHub、NJ 或 NuGet 在线发布。
