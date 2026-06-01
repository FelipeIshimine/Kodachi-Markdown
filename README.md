# Kodachi Markdown

An Editor-only tool for browsing every Markdown file in a Unity project from one window.

## Usage

**Kodachi → Markdown Browser**

- The left pane is a `TreeView` of every `.md` file found anywhere under the project root —
  `Assets/`, `Packages/`, and loose files like the root `CLAUDE.md` or `docs/`.
- Folder chains that contain no Markdown of their own and only a single sub-folder are **collapsed
  into one node**, with the folder names kept visible in the path (e.g. `Scripts/Editor`). This keeps
  deep documentation folders shallow without hiding where they live.
- **Select** a file to preview its contents in the right pane.
- **Double-click** (or press Enter) to open it. Files under `Assets/` open through Unity's asset
  pipeline (your configured external editor); files outside open with the OS default app.
- **Reveal** shows the file in the system file browser.
- The search field filters by any substring of a file's relative path.

## Excluded folders

Build/tooling folders are skipped during the scan: `Library`, `Temp`, `Logs`, `obj`, `Bee`,
`Build(s)`, `.git`, `.idea`, `.vs`, `.vscode`, `.gradle`, `node_modules`, `UserSettings`,
`MemoryCaptures`, `Recordings`.

## Layout

- `Scripts/Editor/MarkdownFileTree.cs` — pure (no-UI) discovery + collapse model.
- `Scripts/Editor/MarkdownBrowserWindow.cs` — the UI Toolkit `EditorWindow`.
