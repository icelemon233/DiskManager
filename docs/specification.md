# DiskManager 1.0

Confirmed with the user on 2026-11-03 through grilling.

- Windows desktop, initially Windows 11 x64. React/TypeScript, Electron, .NET and SQLite.
- Scan one local or external drive at a time from a drive-letter selector. This replaces the original multi-location and folder picker at the user's request on 2026-11-04. Existing folder and multi-location snapshots remain readable and refreshable. Startup requests UAC for a dedicated read-only NTFS helper reused until application exit. Directory traversal handles other filesystems and permission fallback. This startup behavior supersedes the original on-demand permission choice.
- Directory/table and treemap-focused layouts can be switched. Resizable regions, coordinated selection, type statistics and filters. Allocated size is the default, logical size is available.
- Complete scans automatically save file-level history. Added, removed, resized and identifiable moved/renamed files. Retain ten records per scope with a configurable 2 GiB history budget and pinned records.
- Content-verified duplicates across selected locations. Recommend keepers by preferred directory, user-approved batch selection, retain at least one physical file per group. Identify hard links separately.
- Windows Temp, Chrome/Edge and npm/pnpm/pip/uv/NuGet/Pub caches. Separate download caches, supported unused-package pruning and full shared dependency cleanup. Use tool-supported cleanup where required and disclose dependency restore effects.
- Custom cleanup rules with roots, name/extension, size, modification time and exclusions. Always preview and confirm.
- Explorer reveal, copy path, recycle, permanent delete, move and rename. Default deletion recycles. Protect critical directories/root objects and app-owned state. Revalidate files before mutation, detect conflicts and changed files, verify cross-volume copies, record individual results.
- Manual refresh plus affected-directory refresh after app operations. Jobs report progress and support cancellation.
- Online-only cloud files remain metadata-only by default. Explicit opt-in content verification shows download estimate and sync implications.
- Export filtered/all CSV, reopenable snapshots and treemap PNG.
- Cinna light/dark/system theme, sidebar/window/tray conventions and business-specific character icon. Default directory layout, remember layout and splitter dimensions.
- Generate a Cinna installer after validation. Installation is a separate explicit first-install action.

## Verification

Synthetic fixtures cover hard links, sparse files, Unicode, inaccessible paths, reparse points, cancellation, snapshot retention/import/diff, duplicates and operation safety. Real read-only volume tests compare sizes and performance against WizTree when elevation is available. UI and installer are validated using isolated app data. Record actual results and remaining limitations.
