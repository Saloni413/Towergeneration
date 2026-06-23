# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## ⚠ MUST READ FIRST — before doing anything else

At the start of every session — even for a simple question — read this file before reading any source code or running any tool. If `.claude/` session notes exist, read the latest one too (see **Session Workflow** at the bottom).

These constraints are the project's memory. Skipping them means redoing work or undoing decisions that were already made.

---

## Working conventions (MANDATORY)

1. **Always enter plan mode before execution.** Before writing code, editing files, or running any change-making command, enter plan mode, present the plan, and get the user's approval. Never start executing changes first.
2. **Never touch the architecture without asking.** The structure described under *Hard Constraints* is fixed. Do not add new commands, new files, new external dependencies, new data models, or change how the plugin loads/registers without explicit user approval first.
3. **Use only the tech stack already in the project.** Do not introduce any new NuGet package, framework, or library. The frozen stack is listed under *Tech Stack Lock*. If a task seems to require a new dependency, stop and ask first.

---

## Project Overview

A single **C# .NET 8 (`net8.0-windows`, x64) class-library plugin** for **AutoCAD 2026 + Advance Steel**. It builds a steel lattice/transmission **tower** from a JSON definition, then produces a Bill of Materials (BOM) and 2D drawing views.

- Builds to `Towergeneration.dll` and is loaded into AutoCAD via **NETLOAD**.
- Commands are registered through the `[assembly: CommandClass(...)]` and `[assembly: ExtensionApplication(...)]` attributes in [myCommands.cs](Towergeneration/myCommands.cs).
- It is **not** a standalone executable, web service, or library consumed by other code — it is an in-process AutoCAD/Advance Steel add-in.

**Input:** a JSON file (currently a hardcoded path constant `JsonPath` in [myCommands.cs](Towergeneration/myCommands.cs)) describing tower members with start/end coordinates in mm.

**Outputs:** a 3D Advance Steel model (in the active drawing), a BOM (WPF window + PdfSharp PDF), and 2D projection DWG files (top / front / back / right / left views).

---

## Tech Stack Lock (READ BEFORE TOUCHING ANYTHING)

The project targets `net8.0-windows`, x64 only, with `ImplicitUsings`, `Nullable`, WinForms and WPF enabled. See [Towergeneration.csproj](Towergeneration/Towergeneration.csproj).

| Dependency | Source | Purpose |
|---|---|---|
| `AutoCAD.NET` 25.1.0 | NuGet | AutoCAD 2026 managed API (`Autodesk.AutoCAD.*`) — DB, geometry, editor, entities (Polyline, DBText), `Database.SaveAs` |
| `Newtonsoft.Json` 13.0.4 | NuGet | Deserializing the tower JSON into `TowerData` |
| `PdfSharp` 6.2.0 | NuGet | Rendering the BOM to PDF |
| Advance Steel managed API | Local refs from `C:\Program Files\Autodesk\AutoCAD 2026\ADVS\` (`ASMgd`, `ASObjectsMgd`, `ASGeometryMgd`, `ASCADLinkMgd`, `ASProfilesMgd`) | Creating `StraightBeam` members, AS geometry (`Point3d`/`Vector3d`), document/transaction management |
| WPF + WinForms | .NET SDK | BOM / elevation UI windows |

**Rules of thumb:**
- **Do not add any new NuGet package or external reference.** No alternative JSON libs, no alternative PDF libs, no UI frameworks beyond WPF/WinForms, no logging/DI/test frameworks unless the user explicitly approves.
- **Do not change package versions** or the target framework / platform.
- Two JSON serializers coexist deliberately: the command code uses **Newtonsoft.Json** with `TowerData`/`MemberData`; `MembersJsonFile`/`MemberRecord` use **System.Text.Json**. Do not unify or swap these without asking — they serve different models.
- The Advance Steel DLLs are referenced from a fixed install path and loaded by AutoCAD at runtime (`<Private>True</Private>`). Do not vendor them into the repo or change the hint paths without asking.

---

## Architecture (FIXED — do not restructure without approval)

### Source files (all under `Towergeneration/`)

| File | Contents |
|---|---|
| [myCommands.cs](Towergeneration/myCommands.cs) | The whole plugin surface: `MyCommands` (all `[CommandMethod]` commands + private helpers), `BomRow` DTO, and `WinFontResolver` (`IFontResolver` for PdfSharp). Also the two assembly attributes that register the command class and extension. |
| [JsonData.cs](Towergeneration/JsonData.cs) | `TowerData` + `MemberData` — the Newtonsoft model the commands actually deserialize. |
| [MemberJsonModels.cs](Towergeneration/MemberJsonModels.cs) | `MembersJsonFile` + `MemberRecord` — a separate System.Text.Json model for `members_*.json` exports. |
| [MemberLine.cs](Towergeneration/MemberLine.cs) | `MemberLine` — start/end as Advance Steel `Point3d`. |
| [PluginExtension.cs](Towergeneration/PluginExtension.cs) | `PluginExtension : IExtensionApplication` — prints the command banner on load. |

### Commands (registered `[CommandMethod]`s in `MyCommands`)

| Command | Does |
|---|---|
| `GENERATEFROMJSON` | Reads JSON → for each `Angle` member, calls `CreateLinearMember`, which builds an Advance Steel `StraightBeam` inside a locked-document AS transaction. |
| `GENERATEBOM` | Reads JSON → computes length/weight per `Angle` member, groups by `Mark`, shows a WPF BOM window (`BuildBomWindow`) and writes the PDF (`WriteBomPdf`). |
| `GENERATEELEVATION` | Builds the elevation preview window (`BuildElevationWindow`). |
| `SAVETOPVIEWDWG` / `SAVEFRONTVIEWDWG` / `SAVEBACKVIEWDWG` / `SAVERIGHTVIEWDWG` / `SAVELEFTVIEWDWG` | Each projects members onto a plane and writes a 2D DWG via `SaveTopViewDwg` / `SaveFrontViewDwg` (mirror=back) / `SaveRightViewDwg` (mirror=left). |

### Key data flow

```
JSON file (JsonPath constant)
   │  JsonConvert.DeserializeObject<TowerData>
   ▼
TowerData.Members (MemberData[])
   ├── GENERATEFROMJSON → CreateLinearMember → Advance Steel StraightBeam.WriteToDb()  (AS transaction, locked doc)
   ├── GENERATEBOM      → group by Mark → BuildBomWindow (WPF) + WriteBomPdf (PdfSharp)
   └── SAVE*VIEWDWG     → project to 2D → AutoCAD Polyline/DBText in a fresh in-memory Database → SaveAs(.dwg)
```

### Conventions in the code (match these)

- **Type aliases** disambiguate the three overlapping namespaces — keep using them:
  - `ASPoint3d` / `ASVector3d` = `Autodesk.AdvanceSteel.Geometry.*`
  - `AcadApp` = `Autodesk.AutoCAD.ApplicationServices.Application`
  - `Wpf*` (`WpfBrushes`, `WpfColor`, `WpfGrid`, `WpfOrientation`) = `System.Windows.*` (vs. AutoCAD/AS types of the same name).
- Advance Steel 3D model writes happen inside `DocumentManager.LockCurrentDocument()` + `TransactionManager.StartTransaction()`; 2D view DWGs are written into a **separate fresh `new Database(true, true)`** and `SaveAs`'d — these two paths are intentionally different.
- `CreateLinearMember` chooses `vUp` (X-axis vs Z-axis) based on whether the member is near-vertical (`|dz/len| > 0.7`).
- `GetLegSize` parses the leg dimension from `Description` via regex (`[Hh]?[Ll](\d+)`), defaulting to 100.
- Section constants live at the top of `MyCommands`: `AngleProfile = "L100x10"`, `KgPerMetre = 15.1` (EN 10056-1), `SectionGrade = "S355JR"`, `SectionDesc = "L100X10"`.
- Commands swallow exceptions and report via `ed.WriteMessage` rather than throwing — preserve this pattern so AutoCAD stays stable.

---

## Building & Running

- Build in Visual Studio (the [Towergeneration.slnx](Towergeneration.slnx) solution, x64) or `dotnet build` — output is `Towergeneration.dll`.
- Run by launching AutoCAD 2026 (`launchSettings.json` has an `Acad` profile pointing at `acad.exe`), then `NETLOAD` the DLL and run a command (`GENERATEFROMJSON`, `GENERATEBOM`, `GENERATEELEVATION`, `SAVE*VIEWDWG`).
- There are **no automated tests** and no test framework — do not add one without asking. Verification is manual inside AutoCAD.

---

## Things to confirm before changing (do NOT do silently)

- The hardcoded `JsonPath` and output file paths — if a change touches these, confirm the target path with the user.
- Anything that alters which member `Type`s are processed (currently only `"Angle"`), weight/length formulas, or the BOM PDF layout.
- Adding/removing/renaming a `[CommandMethod]`.
- Touching the AS transaction/locking pattern or the in-memory `Database` view-export pattern.

---

## Session Workflow

**Always enter plan mode before execution.** Implement one change at a time and report what was changed and whether it was verified (and that verification here is manual, inside AutoCAD).

Optional session memory: if you create session/worklog tracking files, place them under `.claude/` and author them as **self-contained `.html`** documents (not Markdown), recording goal, files changed, decisions made, and anything left incomplete.
