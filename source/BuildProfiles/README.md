# Game build profiles

`current.json` is the authored definition for the Korean CE executable with
runtime patches and Native features enabled.
It binds the exact EXE SHA-256 to a layout ID, Native ABI contract, shared addresses
and storage offsets, exact original function signatures, and the hashes of the
embedded runtime patch and trait-list relocation resources.

Only product-embedded profiles enter `GameBuildProfiles`. User preferences,
`allowed_builds.txt`, and discovered pointers cannot register a verified profile.
The data profile in `GameBuildProfiles` binds another exact EXE hash to its
world pointer, session checks, and independently reviewed field write guards.
It also records the original ability table and rule table code guards. Rule
table writes require a stable paused session, original executable code, and
writable data pages. Rule preferences are stored by EXE hash for this profile,
separately from the legacy profile file.
The data profile is independent of the full runtime patch registry.
Linked officer transfers, officer likes/dislikes, and bilateral force friendships
use separately reviewed serializer fields. Force array edits check both the
parent record serializer and the nested array serializer. The service value at
officer `+0x1BE` is two bytes; the merit at `+0x1C0` must remain separate.
`Reverse/editor_linked_fields_audit.py` replays both pinned captures offline at
archive IO boundaries and checks widths/counts against the regression vector.
`Reverse/editor_complete_fields_audit.py` extends this to every ordinary editor
field, nested officer/unit arrays, the native military-cap jump table, and the
play-record singleton/unlock/ancient-officer mapping. Archive IO and the unlock
eligibility query are explicit boundaries; fixture success is not live gameplay
or save/load validation. `CompleteEditorCoverageTests` derives fields from their
definitions and requires admission, matching native widths and exact code pins.
City/gate morale, officer compatibility, and personality effect kinds are bytes;
neighboring bytes must survive edits and Undo. Military balances retain their
force/leader/current-cap guard, and personality retargeting authenticates the
current holders before clearing their activation bits.

Both registered builds have reviewed settings-migration routes. Rule imports
still require matching schemas, catalogs and record IDs, retain target defaults,
and disable automatic application. Runtime imports copy only user settings.
Startup preparation hashes the installed executable, selects that build's
settings file, and rechecks the launched identity and original runtime bytes.
The approved live basic-field trial remains limited to its original 34 fields.
`november2026.json` binds the same newer executable to its own season and
environment Native contract, with separately pinned bridges and original code.
Runtime rules use an independently pinned patch and trait-list profile for that
executable. Its camera command has a separate native guard. AI uses an
independent stock tree digest and versioned decision map for that executable.
An unregistered executable has no approved session layout for editor attachment.

Adding an EXE hash is not sufficient to support another game version: review its
layout/ABI and dependencies, add the appropriate generated adapter and Native
bundle, and validate that version independently. Matching RTTI names or a unique
function-search candidate does not certify a record layout or calling convention.

## Generate and verify

Run with a Python 3 interpreter; the generator needs only the standard library:

```text
python source/BuildProfiles/generate.py
python source/BuildProfiles/generate.py --check
python source/BuildProfiles/test_generate.py
```

The generator validates signature/guard agreement and resource hashes before
writing `GameBuildFacts.g.cs`, the identical bridge headers, and exact signature
headers. `generated.lock.json` records the output hashes. `Validate.ps1` requires
the exact definition, output inventory, output bytes, and resource bytes. Core
MSBuild and both Native build scripts run this check; neither silently regenerates
or accepts stale output. Byte-preserving Git attributes cover these inputs.
The November definition has a separate lock and emits its bridge headers under
`November2026`. Pass `-November2026` to the Native build and packing scripts
to use that version.

Both DLLs export read-only `San14BuildProfileIdentity` data. Managed code inspects
the on-disk PE export without loading or calling the DLL and checks it against
the profile ID/digest/ABI contract. Each Native build receipt also carries those
identifiers. Existing exact DLL, manifest, source snapshot, engine code, page
protection, resident ownership, and recovery checks remain in force. The Season
and Environment wire protocol versions and struct layouts are unchanged.

After Native fixture validation, `PackSeasonBridge.ps1` and
`PackEnvironmentBridge.ps1` use explicitly reviewed DLL/manifest pins and
`PackNative.ps1` to update the **source-embedded** bundles. This is not editor
deployment or a portable release ZIP. Update the corresponding Core pins together.
Never use another checkout's app binaries to deploy this workspace.

## Offline regression evidence

`Tests/Vectors/GameBuildProfileBaseline.json` was captured from the pre-refactor
definitions. It is an independent regression reference, not a generator output.
The tests compare 131 storage facts, eight Native ABI declarations, nine exact
engine guards, and pinned patch/relocation resources. The save/load fix intentionally
updates the patch resource baseline for the cap decoder at `0x22591C`; the storage,
ABI, engine guards and relocation resource remain unchanged. They reject unknown EXE
hashes, unverified identities, changed resources, mismatched Native metadata,
changed compiled identity exports, and missing exports without loading a library.

Run the existing runtime transaction/relocation/recovery fixtures as well as the
profile tests. Explicitly exclude
`SeasonControllerTests.ProductionDateSourceReadsTheCurrentVerifiedGameWithoutWriting`
from offline runs: it reads the actual game. `--runtime-rules-offline`,
`--runtime-rule-persistence`, `--public-loader-offline`, and the Native WARP checks
use owned fixtures. Do not invoke live-game switches or the editor's normal
startup as an offline test. Keep Native fixture success distinct from live-game
loading, visible rendering, gameplay effects, and deployment.

`--automatic-settings-offline` checks legacy and November intent storage, startup
preparation, failure deduplication/retry, and pending-edit retention. Both automatic
process discovery and installed-EXE discovery use injected fixtures. The automatic
preparation path selects preferences and receipts from the observed process build,
checks the same supported runtime profile as explicit preparation, and rechecks
the selected intent immediately before its guarded transaction. Both developer
and public flavors run this check in the compatibility verifier.

The local developer menu also provides a separate complete-editor trial tab.
It previews targets before running product planners and guarded writes. Each trial
uses one process freeze for apply, ordinary Undo, and exact journal recovery.
Personality holder marks and play-record dirty state are logged separately from
ordinary Undo semantics. The byte journal permits only declared field-sized writes
and restores attempted writes in reverse after validating record identity and
exact current bytes; it never overwrites whole baseline records. Stop requests
are honored after recovery. Missing targets remain unverified.

Developer-menu checks exercise these paths with owned memory, partial writes,
log failures, changed guards/neighbors, failed recovery, and cancellation. The
generated `RenderChecks/artifacts/complete-editor-trials.jsonl` is independently
checked by `Reverse/editor_roundtrip_log.py` against both Native serializer-width
vectors. The same parser accepts live JSONL, verifies original/changed/Undo/final
bytes and hashes, and distinguishes valid partial evidence from full completion.
All trial methods, types, and UI entries are absent from public builds.

`DeveloperChecks/Run.ps1` invokes the same Core preview and round-trip methods
without a window. Build its project with `LocalDeveloperPackage=true` and
`DeveloperPackageBuild=true`, then supply `-Mode preview -GameProcessId <pid>
-Output <new-directory>`. Review `targets.json` and `availability.json` before
using `-Mode run -ExpectedManifest <preview>/targets.json` with another new
directory. `-Keys` selects existing available cases. Ctrl+C or an output-directory
`stop.request` stops after the current case has been restored. The verifier checks
both runner flavors without game access; a public runner cannot execute trials.

An explicitly approved seeded-duration case is selected with
`-SeedProvokeCommander <officer-id>` (CLI `--seed-provoke-commander=<id>`) or a
named player unit in the developer tab. Its own preview/run sequence contains
only the selected case, marks `seededProvocation=true`, and injects precisely
one duration byte 0→1 before using the product decrement plan 1→0. Ordinary
Undo and journal recovery finish before the same process freeze is released.
The remembered target fields remain unchanged. This verifies editing with an
injected input; it does not verify Native provocation activation or gameplay AI.
The independent parser enforces this exact shape and rejects changed units,
target fields, duration values, or missing intermediate readbacks.

The separate held-provocation action uses `Reverse/provocation_plan.py` to replay
the unmodified November setter and target-consumption code against read-only
copies of the current save. It runs actual immunity/resistance/target helpers,
suppresses presentation only, authenticates executed instructions against the
reviewed capture, and exports code/input guards plus the complete Native result.
Its current local reference captures are required under `.cache`.

The developer tab can load that `plan.json`, apply the three field-sized writes,
wait for the user's in-game confirmation, then decrease using the product planner,
Undo and restore the original fields. The same Core is available through CLI
`provocation-apply --pid=<pid> --plan=<plan> --output=<new-directory>` and
`provocation-finish --pid=<pid> --receipt=<apply>/receipt.json --confirmed=true
--output=<new-directory>`. `provocation-restore` skips the decrease and restores.
Never issue finish before the user actually confirms. Recovery intent is flushed
before writes; no date advance, save, movement or attack is performed. The held
state survives the CLI process, so retain its receipt. `verify_provocation_hold.py`
independently checks the bytes, user confirmation and final record/array hashes.
This applies a verified Native replay result; it does not remotely call the setter
inside the live process (`nativeReplay=true`, `liveNativeCall=false`).

The offline verifier checks the runtime-mode transitions and injected rollback
failures for both versions. To also check the actual captured PE bytes, pass
`-LegacyRuntimeImage`, `-LegacyRuntimeBase`, `-NovemberRuntimeImage` and
`-NovemberRuntimeBase` to `VerifyCompatibility.ps1` with Python, Capstone and
Unicorn available. The runtime profile audit reproduces patch bytes, callback
addresses, table relocation coverage and the growth helper's results. The
command audit records unique candidates, ambiguity, and missing matches by
operation. Its camera replay checks the actual flight request at the virtual
camera receiver. The output directory contains the per-stage logs and JSON
report. A candidate report requires further verification before registering a
new build or executing a command in a game.

`Reverse/editor_consumer_audit.py` is an additional offline consumer check. Supply
the same two pinned images/bases, the real complete-editor JSONL as `--log`, its
Native layout proof as `--layout-evidence`, and a new `--output` JSON file. It
first independently verifies the real round-trip log, then replays original
policy membership, officer tactic filtering and personality effect selection on
the recorded before/changed/restored record images. Original virtual validity
checks execute against controlled lookup records. Per-query input hashes,
record read offsets/widths, results, executed instructions and code pins are
recorded. The additional conditions cover all 35 policy slots, preparation days,
ordinary/special officer tactic filtering, and signed personality value clamps.

This is an offline experiment with declared dependencies, not a replay of the
complete old live world. The force log ends at 0x120; the unrecorded tail is a
zero-valued fixture. Policy collection observes its visitor boundary and does
not execute policy level/effect aggregation or merit bonuses. Tactics use plain
flags without positive reference matches. Personality checks concern effect
kinds/values, not condition/filter evaluation or holder activation. Missing live
snapshot pages are never filled from a newer game session. No live test action,
process connection, game write or screen access is added by this script.

`Reverse/editor_effects_cases.py` removes the earlier policy visitor boundary.
It runs the original force collector, officer policy visitor, merit bonus,
integer accumulation, level-10 cap and final policy-effect decoder, including
plain/encoded vectors. It also runs the original personality dispatcher and
condition handlers in both phases, records returned target lists, and checks
all 256 range-condition bytes across legacy/custom trait indices and signed
number boundaries. The earlier and November range helpers have different code
shapes; both are executed and checked independently against this matrix.

Supply `--legacy-image`, `--legacy-base`, `--november-image`, `--november-base`
and a new `--output` JSON path. Optional `--log` and `--layout-evidence` first
verify the recorded live round trip, then reuse its original/changed/restored
policy and personality fields in declared controlled contexts. This does not
reconstruct unrecorded historical dependencies. Native input/neighbor writes
and fixture attempts to change executable instructions are rejected. Every
query records Native reads, trace hashes and owned writes. Only CRT mutex
lock/unlock and exact memset semantics cross an emulator host boundary; no
gameplay predicate, visitor, bonus, decoder, or filter is stubbed. Event-based
conditions 30/39/40 have no dispatcher handler: rejection here is checked and
is not evidence that their separate attack/destruction event effects ran.
`VerifyCompatibility.ps1` runs these controlled cases when both images are
provided. This verification never opens a game or adds a live developer action.
