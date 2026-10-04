# Markdown Browser window

**Kodachi > Markdown Browser**

Finds every Markdown (`.md`) file in the project, including ones outside `Assets/` and `Packages/`
such as the root `CLAUDE.md` or a `docs/` folder, and shows them rendered inside Unity.

## Example

Search `flow` and the tree narrows to `Packages/Kodachi-ActionSystem/Documentation~/Windows/flow-graph.md`.
Select it and it renders on the right, headings, tables and code blocks included.

## Tree

- Folders that contain only one sub-folder are merged into one row (`Scripts/Editor`), so deep paths
  stay short.
- **Search** filters by file name. **Refresh** rescans the disk. Clearing the search keeps the open
  file selected and scrolls the tree to it.
- **History**, under the tree, lists recently opened files, newest first. Click one to reopen it.
  Drag the bar between tree and history to resize.
- **Hide Tree / Show Tree** gives the document the whole window.
- Double-click a file to open it in your external editor.

## Document pane

- **Ctrl+F** opens a find bar above the open file (here and in the pinned popup). Matches are
  highlighted; the current one is orange. **Enter** / **F3** goes to the next match, **Shift+Enter**
  to the previous, **Esc** closes the bar. In the raw view the match is selected, and closing the bar
  puts the cursor on it.

- **Formatted:** on, the file is rendered. Off, it is shown as plain text you can edit; changes are
  written to disk as you type.
- Checkboxes (`- [ ]`) in a rendered file can be ticked, and the file is updated.
- **Pin** adds the file to the pinned quick view (Ctrl+Shift+W).
- **Open** opens it in your external editor; **Reveal** shows it in the file explorer.

## Viewer windows

Other windows' help (**?**) buttons open their guide in a separate **document window**: the rendered page
alone, with no tree. Its toolbar has **Reload**, **Open Externally**, and **Show In Browser**, which
opens the same file here.
