# Task persistence process-interruption regression

Run from the repository root:

```powershell
dotnet run --project tests/TaskStoreCrashProbe -c Release
```

This probe uses the real `JsonTaskStore`. It starts only its own child processes in a newly generated temporary directory, observes a save boundary, terminates that specific child, and reopens the committed queue. It never launches or terminates the desktop APP, and never reads user settings or drawings.

Seven cases cover interruption during input enumeration, empty temporary-file creation (twice), nonempty temporary-file presence (three times), and acknowledged commit. Every case requires a complete old or new queue, exact expected payload for the new queue, and successful subsequent load/save. Pre-save must retain the old queue; acknowledged save must retain the new queue. Invalid/mixed JSON fails.

The nonempty temporary observation does not prove interruption during an individual OS write: filesystem scheduling may have completed the write or commit before termination. The report records the observed length and actual surviving version instead of claiming a stronger boundary. This is process termination, not machine power loss or physical disk-full testing. CAD process termination and APP recovery UI are separate acceptance items.

Fresh abandoned temporary files must survive the first subsequent successful save. The probe then ages only its own terminated-child candidates beyond the 24-hour retention window and verifies that the next successful save removes them using the production owner-aware cleanup policy. This tests real process identity and restart cleanup, not merely hand-authored file names. All generated probe files are removed only within this invocation's unique temporary directory.

The normal desktop publishing gate runs this probe before replacing the installed release.

## Installed APP recovery prompt (2026-09-16)

The separate evidence under `artifacts/cross-end-completion-20260916/app-process-recovery` starts the installed desktop executable with a private `DWGC2E_DATA_DIR`, seeds six unfinished states plus terminal records, terminates only the exact owned process, and confirms the recovery prompt reappears with unchanged fixture bytes. This does not interrupt an actual CAD operation. `tests/DwgTranslator.App.UiSmoke/RecoveryDecisionSmoke.cs` is the permanent publishing gate for the recovery decision: close/defer preserve state; explicit clear removes only unfinished records and retains completed/cancelled history. Native capture/input limitations are recorded separately from WPF-event smoke results.
