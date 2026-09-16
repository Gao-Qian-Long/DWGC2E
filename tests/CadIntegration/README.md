# Real CAD runtime acceptance

`Test-CadRuntimeWriteback.ps1` runs the installed CAD executable against the current release plugin. This is opt-in, not part of ordinary unit tests. It creates its own synthetic drawings under a **new** evidence directory, never opens user drawings, does not build the APP, and does not use cloud translation or payment services.

Example (PowerShell, repository root):

```powershell
./tests/CadIntegration/Test-CadRuntimeWriteback.ps1 `
  -CadExe 'D:\haochenCAD\浩辰CAD 2024\gcad.exe' `
  -EvidenceRoot 'D:\DWGC2E\artifacts\cross-end-completion-20260916\cad-runtime\new-run'
```

Requires the release plugin to expose `DWGLAYOUTREGRESSION`, `DwgTranslateWrite` and `DWGVERIFYLAYOUT`. Does not alter CAD trust/security settings. Each owned CAD process receives a script and exits normally. If a process is still alive after 90 seconds, the script reports its PID and stops without launching another instance or force-killing it. Inspect that process before any rerun.

Checks:
- Six synthetic DBText/MText entities at 0, 90 and about 40 degrees: exact replacement text in the seed pass.
- Real command configuration and session-specific completion signals: success, existing-output rejection, same-source rejection, missing-source rejection.
- Original and existing-output SHA256 unchanged, nonempty successful output, no output for missing source, no leaked temporary DWGs.
- Read-back geometry within the existing available-space policy and no newly overlapping text.

Important: `DWGLAYOUTREGRESSION` separately compares against the **original text bounding box**. In the September 16 run all six content checks passed but all six strict original-box checks failed. The existing available-space audit passed 6/6 with no text overlap. Both reports are retained; the available-space pass does not prove strict original-box containment, visual readability, unchanged font size, or adherence to every possible user layout requirement. Do not relabel the strict result as a pass.

Evidence: `artifacts/cross-end-completion-20260916/cad-runtime/command-acceptance/verification.json`. This is not the full APP scheduling/extraction/translation/writeback pipeline, a cloud translation test, a CAD in-flight crash test, an attribute/table/leader matrix, or other CAD-version acceptance.
