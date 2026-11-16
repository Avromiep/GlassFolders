# GlassFolders — project rules

GlassFolders is a per-user Windows desktop app (C# WPF, .NET 10) that gives the
desktop iOS/Android-style folders. This is a **public** repository. These rules
apply to all new and changed code. Keep every rule short and plain.

## Releasing
- Never tag, never create or publish a GitHub Release, and never push a tag.
  Nothing is released until the owner types the exact words **"release it"**.
- Do not add a GitHub Actions workflow (there is none; keep it that way).
- Stop before starting work if `main` is not the exact base commit SHA given in
  the task prompt. Say so and do not begin.

## Versioning
- Bump the version in exactly two places: `src/GlassFolders.csproj` (`<Version>`)
  and `src/App.xaml.cs` (`AppVersion`). Convention is patch +1, `v0.3.N`.
- The installer takes the version from `/DAppVersion`; it has no hard-coded
  version. The installer stays per-user (no administrator prompt).

## Privacy, network and security
- No telemetry, no analytics, and no crash reporting.
- No new network use beyond the existing Check-for-updates path (GitHub
  Releases). Stay offline otherwise.
- Nothing elevated: never request administrator rights, never use the `runas`
  verb, never start anything elevated.
- Secrets live only in GitHub secrets — never in the repository, the app, chat,
  or prompts.
- Do not scan the whole disk.
- Never run anything through `cmd` or any other interpreter. Build process
  arguments with `ArgumentList`, never a single command string. Validate any ID
  or argument that ends up in a shortcut or a command (use a pure, testable
  validator).

## Data and storage
- Private folder data stays on the device under `%LOCALAPPDATA%\GlassFolders`.
  Updates and installs must never wipe it.
- Each folder is a real directory of `.lnk` files; the `.lnk` is the source of
  truth. Do not introduce a new storage format, and do not add an AUMID (or any
  other identity) field to the model.
- Start Menu, `%ProgramData%`, and other system folders are **read-only**. Write
  only inside Glass Folders' own data folders. Never overwrite a user's files.

## Launching
- Launch stays `Process.Start` on the `.lnk` with `UseShellExecute = true`, with
  no arguments and no verb.
- Never launch anything while listing or picking apps — not from a picker, not
  during enumeration. Apps launch only when the user clicks or opens an item in a
  folder, with normal privileges.

## Logging
- Do not log app names, AUMIDs, or paths. Exception messages must not include
  paths. Never log private text. Keep any diagnostic for a new feature to a
  count only (or none). This is beyond existing behaviour only; do not rewrite
  existing diagnostics just to match.

## Dependencies and UI
- No new NuGet packages unless unavoidable. The default is none; if one is truly
  required, justify it in the commit message.
- Write UI labels in full (no cramped abbreviations).

## Testing / done
- "Done" means `dotnet build` succeeds **and** `--fixtest` is green: every
  existing fixtest line still passes, and every new fixtest line prints `PASS` or
  `FAIL`. Keep merge/search/validation/path logic in testable helpers. New
  fixtest lines use only synthetic temporary files and names — never print real
  Start Menu or AppsFolder app names, AUMIDs, or paths (counts only).
- Prefer verifying through `--fixtest` hooks over launching the app; never run a
  listed app just to inspect it.

## Accepted-by-design (do not change unless asked)
The installer force-closes the running app; hidden `--` test switches ship in the
build; the updater has no paid code-signing certificate; the single-instance
named pipe has no extra ACL; shortcut paths use the 260-character limit;
exports can contain the current username in stored paths; a folder-open request
can be dropped if the app is busy. These are known and intentional.
