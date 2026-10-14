# Kodachi Markdown

An Editor-only tool for browsing every Markdown file in a Unity project from one window, and for
generating a package's API reference from its compiled assemblies.

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

## API reference generator

**Kodachi → Generate API Reference** writes one Markdown page per public type of a package, plus an
index, into `<package>/Documentation~/Reference/`. A page shows the type's declaration, its members
grouped by kind (inspector fields, constructors, properties, methods, events, fields, enum values),
its nested and derived types, and links to every other documented type its signatures use.

Which assemblies get pages is decided by the summary files: each
`Documentation~/Reference/Summaries/<AssemblyName>.md` documents that assembly. The file holds a
blockquote saying what the assembly adds, then one line per public type:

```markdown
# KodachiGames.Tabletop.View

← [API reference](../README.md#kodachigamestabletopview)

> The core view: pieces, zones, commands, timing, choices, highlights, visuals and inspection.

## Identity

- `ViewId`: A non-zero number naming one view object; `ViewId.None` names nothing.
- `Slot`: A position on the table: a zone key plus an index inside that zone.
```

Headings and other lines are ignored, so group entries however reads best. Generic types are keyed
with their parameter names (`CardCatalog<T>`), nested types with a dot (`Outer.Inner`). Keep links
out of summaries; they are copied onto pages in a different folder.

The command runs over every package that has a `Summaries` folder and refuses to write anything
when the summaries and the code disagree. The error lists:

- public types with no summary, as lines ready to paste into the file;
- summaries naming a type that is not public in that assembly;
- an assembly that is not loaded in the Editor, or two types with the same name in one package.

Pages are rewritten only when their text changes, and pages for types that no longer exist are
deleted. Everything in `Documentation~/Reference/` except `Summaries/` is generated; never edit it by
hand.

## Excluded folders

Build/tooling folders are skipped during the scan: `Library`, `Temp`, `Logs`, `obj`, `Bee`,
`Build(s)`, `.git`, `.idea`, `.vs`, `.vscode`, `.gradle`, `node_modules`, `UserSettings`,
`MemoryCaptures`, `Recordings`.

## Layout

- `Scripts/Editor/MarkdownFileTree.cs` — pure (no-UI) discovery + collapse model.
- `Scripts/Editor/MarkdownBrowserWindow.cs` — the UI Toolkit `EditorWindow`.
- `Scripts/Editor/Reference/` — the API reference generator: `ApiReferenceGenerator` (menu command),
  `SummaryFile` (parses summaries), `Signatures` and `TypeNames` (print C# declarations),
  `ReferencePageWriter` (writes the pages).
