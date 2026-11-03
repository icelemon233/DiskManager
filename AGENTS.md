# DiskManager

Cinna-series Windows disk-space analyzer. Product name DiskManager, developer Cinna, app ID local.cinna.diskmanager. Follow cinna-app-engineering, natural-writing, cinna-windows-ui, cinna-web-ui and frontend-architecture.

## Boundaries

- React and strict TypeScript render the interface. Electron owns window, tray, dialogs and IPC validation.
- The .NET worker owns scanning, SQLite indexes, history, duplicate verification, cache rules and file-operation plans. Startup requests UAC for a separate read-only NTFS helper reused for the application session through authenticated current-user named pipes.
- All displayed scan data is real. Report partial scans and unsupported metadata. Keep immutable historical snapshots separate from the current index.
- File mutations use a preview plan, native confirmation and execution-time identity checks. Protect system locations, app data and link traversal. Permanent deletion is a separate confirmed action. Test mutations only on disposable fixtures.
- Keep large indexes in the worker. UI queries are bounded and virtualized. Jobs have cancellation and terminal states.
- Use Cinna semantic theme tokens and shared shell/installer implementations. Identity artwork shows Cinna with disk-analysis equipment.
- Avoid unnecessary secondary labels and descriptions. Let layout and primary controls communicate structure. Keep information needed for scope, permissions, destructive actions and failure recovery at the relevant action.

## Validation

`pnpm typecheck`, `pnpm build`, `dotnet build native/DiskManager.Native.csproj`, native `--self-test`, `node scripts/integration.mjs`, real Electron window QA and installer tests. Development .NET SDK is discovered via DOTNET_ROOT or installed SDK paths. No machine-specific SDK path in published runtime.

First release implements the complete scope in docs/specification.md. The public repository is icelemon233/DiskManager, with Windows installers distributed through Releases. Remote publication follows the user's authorization for the active task. Preserve user files and unrelated projects.
