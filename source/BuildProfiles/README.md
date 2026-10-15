# Game build profiles

`current.json` is the authored definition for the supported Korean CE executable.
It binds the exact EXE SHA-256 to a layout ID, Native ABI contract, shared addresses
and storage offsets, exact original function signatures, and the hashes of the
embedded runtime patch and trait-list relocation resources.

Only product-embedded profiles enter `GameBuildProfiles`. User preferences,
`allowed_builds.txt`, and discovered pointers cannot register a verified profile.
The existing unknown-build discovery and write checks remain separate.

The current layout adapter remains compiled for this one supported profile.
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
engine guards, and unchanged patch/relocation resources. They reject unknown EXE
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
