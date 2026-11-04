<p align="center"><img src="./assets/icon.png" alt="DiskManager 图标" width="96"></p>

<h1 align="center">DiskManager</h1>

<p align="center"><a href="./README.md">English</a> | 简体中文</p>

在 Windows 上查找大文件、比较磁盘空间变化，并在清理前核对待处理文件。

[下载 Windows 版](https://github.com/icelemon233/DiskManager/releases/latest) · [从源码构建](./docs/development.md)

已验证的平台为 Windows 11 x64，界面为简体中文。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./docs/readme/overview-dark.png">
  <img src="./docs/readme/overview-light.png" alt="DiskManager 的目录表与树图，使用合成测试文件" width="960">
</picture>

*截图使用合成测试文件。*

## 磁盘分析

- 通过目录表和树图查看空间分布，按实际占用或文件大小排序。
- 比较已保存的扫描，查看具体文件的变化。
- 跨位置校验重复内容，选择需要保留的副本。
- 预览缓存清理、回收、删除、移动和重命名，再确认执行。
- 导出 CSV、可重新打开的扫描快照和树图图片。

## 开始使用

1. 安装并打开 **DiskManager**。
2. 在“空间分析”中选择一个本地磁盘或外接盘，点击“扫描”。
3. 浏览结果、比较历史扫描，或选择文件查看操作清单。

完整扫描会自动保存。历史保存期间仍可浏览结果，正常退出时会等待保存完成。

## 权限与文件操作

应用启动时会请求 Windows 授权，启动用于 NTFS 快速扫描的只读辅助进程。取消授权后可继续普通目录扫描，也可点击“重新授权”重试。其他文件系统使用目录遍历。Windows 安装包尚未签名。

文件操作需要确认，执行前会重新核对文件身份。系统关键位置受到保护。在线云文件默认跳过内容校验，主动选择下载后才读取。移入回收站的文件仍占空间，清空回收站后才释放。

扫描、重复检查、清理和历史记录的详细操作见[使用说明](./docs/usage.md)。依赖许可证见[第三方声明](./THIRD_PARTY_NOTICES.txt)。
