<img src="assets/icon.png" width="96" alt="DiskManager icon" />

# DiskManager

English | [简体中文](./README.zh-CN.md)

DiskManager is a Windows desktop app for finding large files, comparing disk usage over time and reviewing files before cleanup. It belongs to the Cinna application family. The interface is in Simplified Chinese.

## Explore your disks

- Select a drive letter to scan one local or external disk at a time. NTFS volumes use a read-only MFT helper authorized at startup. Other filesystems use directory traversal.
- Switch between a directory table and a treemap-focused layout. Sort by allocated space or file size, inspect file types and filter the file ranking.
- Compare saved scans down to individual files. Complete scans are saved automatically, with configurable retention and pinned records.
- Verify duplicate content across selected locations and suggest copies to retain using directory preferences.
- Preview Windows, browser and developer-cache cleanup, or save custom rules. Recycle, permanent deletion, moves and renames require confirmation and recheck file identity before execution.
- Export CSV, a reopenable scan snapshot or a treemap PNG.

Results become available before history finishes saving. DiskManager displays the save status and waits for pending saves when quitting normally.

![Disk analysis with synthetic test files](docs/assets/analysis.png)

## Start

Download the Windows x64 installer from [GitHub Releases](https://github.com/icelemon233/DiskManager/releases/latest). Windows 11 x64 is the validated target.

Run `DiskManager-1.0.8-windows-x64-setup.exe`, then select a drive letter and click **扫描** in **空间分析**. DiskManager requests Windows administrator permission at startup and reuses the scan helper until the app exits. If you decline, directory scanning remains available and **重新授权** lets you retry.

The current package is unsigned. System locations are read-only in the file-operation workflow. Online-only cloud files are skipped during content verification unless you opt in to downloading them. Moving files to the Recycle Bin keeps their disk space occupied until the bin is emptied.

For detailed workflows, see the [usage guide](docs/usage.md). Build instructions and verification notes are in the [development guide](docs/development.md).

## Build from source

Use Node.js 24, pnpm 11 and the .NET 10 SDK on Windows.

```powershell
pnpm install --frozen-lockfile
pnpm native
pnpm build
pnpm desktop
```

Third-party notices are included in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
