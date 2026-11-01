# Repository Guidelines

严禁在用户未要求的情况下擅自添加测试

## Structure

- `Class1.cs` owns plugin lifecycle, game endpoint and WebSocket route registration, and result projection.
- `SkillPlanning.cs` adapts the packet and Host catalog into immutable purchase/condition/evolution choices. `SkillOptimizer.cs` solves the joint CP-SAT model.
- `SkillTipsDisplayRenderer.cs` renders immutable display snapshots with Terminal.Gui.
- `SkillTipsSettings.cs` owns sorting settings and the native configuration dialog.
- `SkillTipsDialogs.cs` schedules native dialogs on the application iteration outside Terminal.Gui's timer lock.
- `SkillTipsBridge.cs` owns the loopback WebSocket session, training identity, and request/response contracts.
- `native/` owns the Hachimi v3 DLL, calibrated game profile, and outbound WinHTTP WebSocket client.
- `GradeRank.cs` contains score thresholds and rank labels.
- `i18n/ParseSkillTipsResponse*.resx` contains user-facing text.

## Build Safety

Keep `<IsUraPlugin>true</IsUraPlugin>` and the `UmamusumeResponseAnalyzer` package reference with `Version="*"` in `SkillTipsResponseAnalyzer.csproj`; do not add a Host project reference.

From this repository, build without packaging or local deployment:

```powershell
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## Code and Localization

- Preserve the wildcard `check_event`, `load`, and `finish` registrations and the owned workspace lifecycle when changing adapters or UI behavior.
- Keep rules in the Host catalog, optimization in `SkillOptimizer.cs`, and rendering in `SkillTipsDisplayRenderer.cs`. Rendering must not alter costs, scores, or evolution choices.
- Configuration must work before `Initialize`. Save the default sort only after dialog acceptance; apply it to subsequent results. Workspace sort changes affect only the current display and never persist. Keep solver output independent of display settings.
- Update the base, `zh-CN`, `en-US`, and `ja-JP` resource files together. Do not edit generated `*.Designer.cs` files by hand.
- Keep game skill IDs separate from evolved display names. Bridge updates redraw the existing table without recalculating the plan or replacing its view.
- Unsafe selection unlock belongs to one recommendation view and is never persisted. Confirm replacement only when the current pending selection contains skills outside the prepared target set, including game-required lower skills.
- Unlock input depends on the active workspace, not an inner control's focus. Exclude editable fields and command mode. Table mouse input controls focus and scrolling without TextView's editor hit testing.
- Native game access runs on the game main thread. Track only the current Controller's model; temporary preparation models must not publish selection events. Validate the page, training baseline, and revision before replacing selections. Never invoke final learning or submit a learning request.
- Use the Host's `/SkillTipsResponseAnalyzer/bridge` WebSocket route and existing listener. Read the native destination from `hachimi/httpforward.json` on every connection attempt, retaining the configured port and base path. Accept only native loopback clients, one active session, protocol version 2, and UTF-8 text messages of at most 65536 bytes including all fragments. Disconnect clears state, preparation and pending requests; reconnect never replays commands.
- Root `package.ps1` writes `artifacts/SkillTipsResponseAnalyzer.zip`, the adjacent `artifacts/skill_tips_bridge.dll`, and `artifacts/SHA256SUMS.txt`, replacing the same build outputs. Before copying, it checks that the native destination is writable; exit any game loading this DLL first. Native `build.ps1` builds `target/x86_64-pc-windows-msvc/release/skill_tips_bridge.dll` and verifies its PE architecture and export. Local builds must not install into the game directory or trigger game actions.
- The SDK and Host runtime must include `SkillCatalog`, single-rounding `SkillScoring`, and native ZIP loading, with matching `skill_planning.br` data. Local validation uses SDK `2026.11.1.2`. Use a temporary NuGet source mapping and an isolated package cache for every consumer. `package.ps1` accepts `RestoreConfigFile` and `RestorePackagesPath`; inspect `project.assets.json` and `.nupkg.metadata` to verify the resolved version and source.
- CI runs the shared managed validation with publishing disabled and runs `native/build.ps1` in a separate job. The tag publication job requires both to succeed, checks the manifest version, and publishes the ZIP, separate Hachimi bridge DLL, and SHA256SUMS together. OR-Tools native dependencies belong inside the ZIP at `runtimes/win-x64/native/`; the Hachimi bridge DLL remains separate.

## Offline validation

The user authorized optimizer and rule validation for this rewrite. Run `tests/SkillOptimizer.Tests` with the matching Host runtime (`HostRuntimeDirectory`, or CI's `UraTestHostRoot`). Run `tools/Validate-SkillTips.ps1` against explicit frozen packet, data, baseline, package, and history-manifest paths. Never replace the fixed packet by selecting the newest capture. Keep captured packets, frozen binaries, master data, and reports under ignored `artifacts/`.

## Security Boundary

Treat packet bodies and endpoint DTOs as untrusted input. Fail clearly on incompatible data; do not guess schemas or add silent fallbacks. Do not commit captured packets, local paths, credentials, or generated plugin packages.
