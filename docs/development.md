# 开发说明

## 环境与入口

项目使用 React、严格 TypeScript、Electron、.NET 10 和 SQLite，依赖版本由锁文件固定。Node.js 24 与 pnpm 11 用于前端构建。通过 `DOTNET_ROOT` 指定 .NET SDK，安装包构建另需 Inno Setup 7.1.0 与 Zig 0.15.2，分别使用 `INNOSETUP_ISCC` 和 `CINNA_ZIG` 指定路径。

```powershell
pnpm install --frozen-lockfile
pnpm native
pnpm build
pnpm desktop
```

## 模块边界

- `src/features` 组织分析、重复文件、清理、历史与设置界面。共享表格和树图位于 `src/shared`。
- `desktop` 负责窗口、托盘、原生对话框和校验后的 IPC。主界面维持普通用户权限。
- `native` 负责读取元数据、分析、SQLite 历史、内容校验、操作预览与执行。启动时授权的只读进程通过当前用户受限的控制管道接收扫描请求，每次扫描使用独立认证数据管道。数据管道关闭可取消当前扫描，控制管道关闭后授权进程退出。
- `installer/inno` 使用 Cinna 共用原生安装界面和事务式升级流程。应用资料与程序文件分离。

扫描先构建不可变内存索引并发布可浏览结果，历史写入在单独队列中进行。每次记录有稳定 ID。较早的保存完成不能替换较新的当前视图，正常退出等待保存任务。历史数据库以封存后提交的方式更新状态。

MFT 诊断随快照计时保存，包含文件表体量、实际读取字节、记录数量与读取／解析耗时。分配位图只用于跳过空区间，结束前复核这些区间。历史概览直接读取已保存总量，全盘类型聚合顺序访问记录，目录范围仍使用路径索引。

## 验证与打包

```powershell
pnpm lint
pnpm build
pnpm native
native/publish/DiskManager.Native.exe --self-test
node scripts/integration.mjs
pnpm package
```

原生自检与集成测试仅在独立夹具上修改文件。覆盖硬链接、稀疏和压缩文件、备用数据流、Unicode 长路径、重复内容、变更后拒绝删除、跨盘移动、历史差异、导入导出及关闭后的恢复。真实磁盘验证只读。

`pnpm package` 构建独立暂存目录，验证安装素材，运行隔离的安装、升级、卸载和取消回滚测试，然后把安装包写入 `installers`。源码归档、哈希和构建记录保存在 `.packaging`。测试安装使用独立注册项与资料目录。

界面自检通过 `--qa-data` 指定 `.runtime` 下的独立资料，再传入 `--qa-capture`。它使用真实 Electron 渲染与合成扫描数据，覆盖两种布局、深浅主题、窄窗口与列设置。纯界面自检省略 UAC，加入 `--qa-authorize E:\` 可验证正常启动授权、只读扫描复用、取消和退出，UAC 由用户手动确认。自动截图与桌面原生输入的验收分开记录。

当前交付是未签名 Windows x64 安装包，首次日常安装由用户发起。性能和实际验收结果见[验证记录](verification.md)。
