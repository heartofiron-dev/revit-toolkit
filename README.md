# Revit Toolkit

A consolidated collection of Autodesk Revit plug-ins and related BIM utilities.

## Included tools

| Component | Revit version | Purpose | Folder |
| --- | --- | --- | --- |
| Model Difference Highlighter | 2024 | Highlights elements that differ between old and new models | `plugins/model-difference-highlighter/` |
| Component ID Synchronizer | 2024 | Synchronizes component IDs between old and new models | `plugins/component-id-synchronizer/` |
| Pipe & Hanger Visibility | 2024 and 2025 | Keeps pipe supports and hangers hidden in sync with their pipes | `plugins/pipe-hanger-visibility/` |
| Drawing Title Checker | 2025 | Checks drawing titles against Excel naming conventions | `plugins/drawing-title-checker/` |
| Drawing Revision Checker | 2025 | Checks whether drawing revisions match the submitted revision data | `plugins/drawing-revision-checker/` |
| SVFZIP Converter | Standalone | Packages an exported SVF folder as an `.svfzip` file | `tools/svfzip-converter/` |

## Repository layout

```text
revit-toolkit/
├── plugins/
│   ├── component-id-synchronizer/
│   ├── drawing-revision-checker/
│   ├── drawing-title-checker/
│   ├── model-difference-highlighter/
│   └── pipe-hanger-visibility/
│       ├── revit-2024/
│       └── revit-2025/
└── tools/
    └── svfzip-converter/
```

Each component keeps its original source, README, add-in manifest, and compiled binary where those files were available. The folders are intentionally separate because several projects use the same source filename (`Class1.cs`) and target different Revit versions.

## Original repositories

- [Highlight the parts with differences between old and new models](https://github.com/heartofiron-dev/Highlight-the-parts-with-differences-between-the-old-and-new-models)
- [Synchronize component IDs of old and new models](https://github.com/heartofiron-dev/Synchronize-component-IDs-of-old-and-new-models)
- [Pipe and hanger visibility — Revit 2024](https://github.com/heartofiron-dev/Synchronized-hiding-of-model-pipeline-supports-and-hangers)
- [Pipe and hanger visibility — Revit 2025](https://github.com/heartofiron-dev/Synchronized-hiding-of-model-pipeline-supports-and-hangers-2025)
- [Drawing title checker](https://github.com/heartofiron-dev/revit-plugin-checking-drawing-title)
- [Drawing revision checker](https://github.com/heartofiron-dev/revit-plugin-checking-drawing-Version-submitted-for-review)
- [SVFZIP converter](https://github.com/heartofiron-dev/Convert-exported-file-to-SVFZIP-code)

## Notes

- Verify the Revit version before installing an add-in.
- Review and rebuild source code before using compiled binaries in production.
- Some components contain source and compiled output but no Visual Studio solution or project file.
