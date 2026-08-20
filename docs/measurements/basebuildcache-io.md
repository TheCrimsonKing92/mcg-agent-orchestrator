# Base build cache I/O measurement

Date: 2026-08-20

## Scope

This document creates the previously absent `docs/measurements/` directory. The measurement covers the
content-hash/stat verification phase and the tree-copy phase of `DotnetBaseBuildCache` before and after the
manifest fast path.

The source was a throwaway copy of an existing `Mcg.AgentOrchestrator.App` base-build-cache entry under
`%USERPROFILE%\AppData\LocalLow\mcg-dotnet-isolated\base-build-cache`. The measured tree contained 108 files
and 80,849,532 bytes; its root `manifest.json` was excluded, matching cache verification and restore behavior.
The live cache entry was never modified.

## Method

A temporary PowerShell harness reproduced the cache's exact hash recipe: relative path encoded as UTF-8,
a zero byte, complete file content, and a trailing zero byte, ordered by relative path. It also reproduced
the new manifest check using relative path, length, and UTC last-write ticks. Copy measurements used the
existing directory enumeration plus `File.Copy(..., overwrite: true)` behavior into a fresh destination.

Each value below is the median of five iterations. Destination deletion and changed-tree setup were outside
the timed regions. The unchanged case compared the recorded stats to the untouched tree. For the changed
case, one byte was changed in one file so the stat comparison fell back to the full content hash. "Before"
hash always ran the full content hash; "after" hash ran the manifest-stat fast path and, on mismatch, the
same full hash. Before/after copy timings are separate observations of the unchanged copy implementation.

## Results

| Tree case | Before hash | After hash | Before copy | After copy |
| --- | ---: | ---: | ---: | ---: |
| Unchanged | 82.17 ms | 7.16 ms | 241.40 ms | 239.18 ms |
| Changed | 78.40 ms | 90.83 ms | 219.39 ms | 218.90 ms |

The unchanged fast path avoided all file-content reads. The changed case paid the expected stat pass plus
the authoritative full hash. There is no required speedup threshold; these numbers record the observed
tradeoff rather than establish a performance assertion.

## Hard-link decision

NTFS hard links were evaluated and rejected for all three current copy call sites. Restore copies from the
cache into build outputs that MSBuild and tests may modify. Publish copies from live build outputs that may
later change into the cache. `ProjectOutputHash` copies from a source that remains writable. A hard link at
any of those sites would alias independently writable paths and could mutate cached output, weakening the
integrity guarantee. All sites therefore retain real copies, which is why no copy speedup is expected here.
