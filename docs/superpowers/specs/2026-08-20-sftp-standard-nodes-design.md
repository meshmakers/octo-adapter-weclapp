# SFTP standard nodes: SftpList@1 and SftpDownload@1

**Date:** 2026-08-20 · **Branch:** `c2-sftp-standard-nodes` (base `e159e26`) · **Work item:** AB#4846 stage 2, absorbing AB#4842 · **Repos:** `octo-mesh-adapter` (new product nodes) and `octo-adapter-weclapp` (consumer)

## Problem

The AR/BE return path reads DILOS files from the LKV SFTP server through adapter-owned code:
`DilosFileFetchStepNode` lists a remote directory, filters by glob and file age, downloads the
content and seeds `$.files`; `DilosFileConfirmNode` keeps or deletes each file once the
downstream write succeeded. Everything below the node surface - session handling, globbing,
text decoding - lives in `SftpFileSystem` / `SshNetSftpFileSystem` inside the adapter.

Three consequences:

1. **The SFTP mechanics are not reusable.** The product already ships `SftpUpload@1` for the
   write direction; the read direction exists only in this adapter, so the next integration
   would copy it.
2. **The keep/delete mode is configured twice.** `deleteAfterSuccess` sits on the fetch node
   *and* on the confirm node, and the two must match, or files are reprocessed forever
   (flipped to `true` on the confirm side alone) or deleted although nothing was written
   (flipped on the fetch side alone). Today a YAML comment, a contract test and a review habit
   hold that together. Both values live in tenant-side pipeline definitions and are editable
   in the Studio, so a half flip is one click away. That is AB#4842.
3. **No host key verification exists in either direction.** `SftpUploadNode.CreateSftpClient`
   never handles `HostKeyReceived`, so SSH.NET accepts whatever key the server presents. The
   go-live acceptance for G11 currently records this as accepted risk.

## Goal

The product owns the SFTP *mechanics*; the adapter owns the DILOS *policy*.

```yaml
# before                                # after
- type: DilosFileFetchStep@1            - type: SftpList@1
  serverConfiguration: LkvSftp            serverConfiguration: LkvSftp
  remoteDirectory: "/"                    remoteDirectory: "/"
  filePattern: AR*TXT                     filePattern: AR*TXT
  minFileAgeSeconds: 60                   minFileAgeSeconds: 60
  deleteAfterSuccess: false               targetPath: $.files

                                        - type: DilosFileGate@1
                                          deleteAfterSuccess: false

- type: ForEach@1                       - type: ForEach@1
  iterationPath: $.files                  iterationPath: $.files
  keyPath: $.current                      keyPath: $.current
  transformations:                        transformations:
                                            - type: SftpDownload@1
                                              serverConfiguration: LkvSftp
                                              remotePathPath: $.current.fullPath
                                              encoding: iso-8859-1
                                              targetPath: $.fileContent

    - type: WeClappArWrite@1                - type: WeClappArWrite@1
      fileNamePath: $.current.fileName        fileNamePath: $.current.name
      contentPath: $.current.content          contentPath: $.fileContent

    - type: DilosFileConfirm@1              - type: DilosFileConfirm@1
      serverConfiguration: LkvSftp            path: $.current
      deleteAfterSuccess: false
      path: $.current
```

`deleteAfterSuccess` exists exactly once after this change, on `DilosFileGate@1`. The confirm
node reads the mode and the server identity from the file element it is handed, so a mismatch
is no longer expressible - the acceptance criterion of AB#4842.

## Design

### Product: `SftpList@1` (extract)

Lists one remote directory and emits metadata only - no content.

| Property | Meaning |
|---|---|
| `serverConfiguration` | Name of the GlobalConfiguration entry holding the connection settings |
| `remoteDirectory` | Directory to list |
| `filePattern` | Glob: `*` any run, `?` one character, anchored, case-insensitive |
| `minFileAgeSeconds` | Entries whose last write is younger are omitted (partial-file guard) |
| `targetPath` | Where the array is written |

Each element carries `name`, `fullPath`, `length`, `lastWriteTimeUtc` and a nested `source`
object with `serverConfiguration`, `remoteDirectory` and `filePattern`. The source stamp is not
decoration: it lets a consumer derive a stable per-listing scope without re-declaring the same
three values in its own configuration, which would reintroduce exactly the mismatch class this
change removes.

`lastWriteTimeUtc` must be emitted in a round-trip-stable representation, and a consumer building
an identity from it must use that emitted representation rather than a re-parsed timestamp. Today
the file key is computed in-process from `SftpFileEntry.LastWriteTimeUtc.Ticks` and never crosses
a serialization boundary; after the split it does. If the emitted form is not stable tick by tick,
every listing produces new keys, the keep-mode skip never matches, and every file is reprocessed
forever - a failure that looks like "keep mode does not work" and is invisible in the logs.

Directory entries are filtered out, the result is ordered by name using ordinal comparison, and
an empty listing still writes an empty array. The last point is load-bearing: a downstream
`ForEach@1` aborts with `PathMustBeArray` when its `iterationPath` is missing or not an array.

Glob semantics and ordering are lifted unchanged from `DilosFileFetchCore.GlobMatch` and
`ListMatchingFiles`, so file matching does not shift under the migration.

### Product: `SftpDownload@1` (extract)

Downloads exactly one file - the mirror image of `SftpUpload@1`, which uploads exactly one.

| Property | Meaning |
|---|---|
| `serverConfiguration` | As above |
| `remotePath` / `remotePathPath` | Static path, or data-context path resolving to one |
| `encoding` | Text encoding, default `utf-8`; validated when the configuration is bound |
| `onEncodingError` | `Replace` (default) or `Fail`, the same enum as the upload node |
| `targetPath` | Where the decoded content is written |

The encoding option is not symmetry for its own sake. `SshNetSftpFileSystem.DownloadText`
(`SshNetSftpFileSystem.cs:59-66`) decodes with `DilosFile.Encoding`, i.e. ISO-8859-1. Without an
explicit `encoding: iso-8859-1` in the AR/BE pipelines the migration would silently change how a
Latin-1 umlaut byte is read.

The node repeats `serverConfiguration` in the YAML although the element it works on already names
its origin. Reading the server name from the element would couple a generic download node to one
producer's element shape; a wrong literal, by contrast, fails loudly on connect and cannot lose
data.

**Sessions per tick.** One session for the listing, plus one per downloaded file, plus the confirm
node's own session per deleted file - against one session for the whole listing today. The gate is
what keeps this small: already-processed files never reach the download, so the per-file sessions
scale with newly arrived files, not with the directory. That is also why the filter has to sit
before the download rather than after it.

### Product: shared connection layer

`SftpServerConfiguration`, `CreateSftpClient` and the per-server semaphore are private members of
`SftpUploadNode` today. They move into a shared connection component that all three nodes use;
`SftpUpload@1` is refactored onto it in the same PR, and its existing tests are the regression
guard for that refactor.

The semaphore keeps the scope it has today: the counters live on the ETL context, keyed by the
server configuration name. That is what the pre-seam upload node did and what `EMailSender@1`
still does, down to the parallel `MaxConcurrentEmails` setting, so a redeployed pipeline picks up
a changed limit. Moving them onto a singleton would have frozen the limit until the process
restarted and widened its reach beyond one pipeline registration, neither of which this change
set out to do.

The resolver, the fingerprint check and the SSH.NET implementation are `internal`, matching
`IHttpRequestService` and every helper in that assembly. `ISftpSessionFactory`, `ISftpSession`,
`SftpEntry` and `SftpServerSettings` are public because a public node names them in its
constructor and the language requires it.

This is also what makes one pinning implementation cover both directions.

### Product: host key pinning

The connection settings gain an optional `hostKeyFingerprint`. It belongs to the server entry,
not to the node configuration: the fingerprint identifies the *server*, one entry serves every
pipeline that talks to it, a key rotation touches one place, and no fingerprint ends up in
tenant YAML that is edited in the Studio.

Semantics:

- absent - connect as today, no verification; the compatibility path, so nothing breaks on rollout
- set - compare against `HostKeyEventArgs.FingerPrintSHA256` in the `HostKeyReceived` handler and
  report the verdict through `CanTrust`. SSH.NET reads that value back and refuses the key
  exchange itself, which is the path its teardown is written for; the caller translates the
  resulting connection failure into an error naming both the expected and the presented
  fingerprint. Throwing from the handler skips that path, so the peer sees a bare socket close
  instead of a protocol-level failure
- present but blank - refused. Leaving the property out disables pinning deliberately, but an
  empty string is a typo or an unset template variable, and accepting it silently would leave an
  operator believing the server is pinned

SSH.NET 2026.0.0 documents `FingerPrintSHA256` as "the same format as the ssh command, i.e.
non-padded base64, but without the `SHA256:` prefix", so the configured value is what
`ssh-keygen -lf` prints, minus that prefix.

### Adapter: `DilosFileGate@1`

`DilosFileFetchStep@1` loses its SFTP half and is renamed to what remains: the state gate between
listing and processing. It reads `$.files`, and for each element

- skips it in keep mode when the key is already marked kept on the server,
- retries a delete that `DilosFileConfirm@1` recorded as pending in an earlier tick, without
  emitting or re-processing the file,
- otherwise stamps `deleteAfterSuccess` and the server configuration name into the element and
  lets it through.

Scope key and file key keep their current shape (`DilosFileFetchCore.ScopePrefix` and `FileKey`),
but the three scope components now come from the element's `source` stamp rather than from the
node's own configuration, and the file key is built from the emitted `lastWriteTimeUtc`
representation rather than from a `DateTime.Ticks` value the gate never sees. Cross-tick memory
stays in the `DilosFileFetchState` singleton, unchanged, including its documented restart
behaviour.

Configuration: `deleteAfterSuccess`, plus `path` (default `$.files`) naming the array to work on.
The mode lives here and nowhere else; `path` has to exist because the listing node's `targetPath`
is configurable, and a gate that hard-codes `$.files` would quietly do nothing if someone lists
into a different path.

The gate writes the filtered array back to the same path, adding to each surviving element the
`key` that `DilosFileConfirm@1` reads, plus the mode and server stamp.

**One ordering change.** Today the age check runs inside the per-file loop, after the keep and
pending-delete checks; afterwards it sits in `SftpList@1`, before them. A file younger than
`minFileAgeSeconds` is therefore not listed at all, so a delete retry pending on it waits for the
age window to pass. Files with a pending delete were processed in an earlier tick and are older
than the window by construction, so this is a theoretical difference, not a practical one - but it
is a difference, and it should not be discovered later in a log.

### Adapter: `DilosFileConfirm@1`

Loses `serverConfiguration` and `deleteAfterSuccess`; both are read from the element the gate
stamped. A missing stamp is an error, never a default - assuming either mode silently is exactly
the failure this change exists to prevent.

The YAML comments that today warn about keeping the two values in sync, and the contract test
that asserts it, are removed with them.

### The file element, before and after

The element shape changes, so both pipeline YAMLs need more than a node swap:

| Today, seeded by `DilosFileFetchStep@1` | Afterwards | Read by |
|---|---|---|
| `fileName` | `name`, from `SftpList@1` | `WeClappArWrite@1` / `WeClappBeWrite@1` (`fileNamePath`) |
| `content` | gone from the element; the content lands at the download node's `targetPath` | the write nodes (`contentPath`) |
| `fullPath` | unchanged, from `SftpList@1` | `SftpDownload@1`, `DilosFileConfirm@1` |
| `key` | unchanged in meaning, stamped by `DilosFileGate@1` | `DilosFileConfirm@1` |
| `lastWriteTimeUtc` | unchanged, from `SftpList@1` | the gate, for the file key |
| - | new: `source`, from `SftpList@1` | the gate, for the scope |
| - | new: mode and server stamp, from the gate | `DilosFileConfirm@1` |

Concretely: `fileNamePath: $.current.fileName` becomes `$.current.name`, and
`contentPath: $.current.content` becomes the download node's target path, in both the AR and the
BE pipeline.

### Why the download moved into the loop

`ForEachNode` creates a dedicated child data context per iteration (`ForEachNode.cs:237-262`),
seeds the item at `keyPath` and runs the children as a sequence inside it. A download node placed
there writes into that iteration context: visible to the later children of the same iteration,
isolated from the other iterations. Verified by reading the node, not assumed.

Keeping the download outside - listing and downloading in one node, filtering afterwards - would
download every already-processed file on every tick for as long as keep mode is active. Whether
keep mode ends at go-live is an open question with LKV (G13), so the design must not depend on it.

## Error handling

| Situation | Behaviour |
|---|---|
| `serverConfiguration` entry missing or half configured | Fail before connecting, naming the entry |
| `filePattern` empty | Fail with an explicit message; the YAML deserializer does not enforce `required` |
| Host key mismatch | Refuse the connection, naming expected and presented fingerprint |
| Listing fails | Node fails, the run fails - no partial silent listing |
| Download of one file fails | That `ForEach` iteration fails; `continueOnError: true` isolates it, the remaining files run, the run reports the failure at the end |
| Unencodable content with `onEncodingError: Fail` | Fail before writing to the target path |
| Confirm receives an element without a stamp | Fail; never assume a mode |

The per-file behaviour changes deliberately. Today `DilosFileFetchStepNode` catches per entry,
logs and continues, so a broken file leaves a green run. After the split a failed file is a failed
iteration: isolated, but visible in the run status - which is also what the planned AR error alert
needs to fire on.

## Dry run

Reads run in dry-run, because the downstream chain must see data; state writes and remote deletes
do not. That is the contract the current nodes already implement, carried over unchanged.

## Tests

Written test-first, following the AB#4785 pattern.

**Product.** Glob semantics (anchored, case-insensitive, `*`, `?`); directory entries excluded;
ordinal ordering; `minFileAgeSeconds` boundary; empty listing writes an empty array; source stamp
present on every element. Download: ISO-8859-1 round trip with a real Latin-1 umlaut byte, UTF-8
default unchanged, `onEncodingError` in both settings, static and path-resolved remote path,
missing remote file. Connection layer: matching fingerprint connects, mismatching one is refused
with both fingerprints in the message, absent one connects; semaphore honoured per server entry.
Existing `SftpUploadNodeTests` stay green.

**Adapter.** Keep-skip only in keep mode, delete-retry only in delete mode, scope isolation
between the AR and BE pipelines sharing one state singleton, pruning, stamp written; confirm
refuses an unstamped element. One test pins the risk the split introduces: two consecutive
listings of an unchanged file must produce the identical key, so the keep-mode skip still matches
after the timestamp has crossed the serialization boundary. Plus one end-to-end test that pushes a
golden-sample AR file through the new path and compares the decoded content against today's - the
C2 counterpart of the byte-level verification C1 does for the upload direction.

Contract tests for the rewritten pipeline YAMLs cover node names, `continueOnError`,
`maxDegreeOfParallelism: 1`, `targetPath`, and the explicit `encoding: iso-8859-1`. Once PR #15 is
merged, every new fact must also be listed by name in `CLAUDE.md`, or the documentation guard test
fails the suite.

## Rollout

1. Product PR in `octo-mesh-adapter`: both nodes, connection layer, pinning, `SftpUpload@1`
   refactored onto the layer.
2. Release, then chart bump on staging.
3. Adapter PR in `octo-adapter-weclapp`: gate node, confirm node, AR/BE YAML, contract tests,
   `CLAUDE.md`. It can be written in parallel but goes green only once the product release is on
   nuget.org - the local `../nuget/` feed lags and would not know the new node types.
4. Staging verification: AR and BE ticks in rhythm, one real file through the new path, keep
   behaviour (the file stays and is not written again on the following tick), delete path
   exercised dry.

Between the image deploy and the tenant YAML re-import the stored definition still names
`DilosFileFetchStep@1` and still carries the removed confirm properties, and the strict
deserializer rejects both, so the AR/BE pipelines do not register during that window.

The window is bounded in two ways. It affects only those two pipelines:
`TryRegisterPipelineCoreAsync` catches a `PipelineSerializationException` per pipeline, records it
as `PipelineDeserializationError` and continues the loop
(`octo-communication-sdk`, `PipelineRegistryService.cs:318-355`), so AS, AI and CK register
normally. And at a 15-minute cadence it costs at most one tick, with the runbook sequencing deploy
and re-import back to back. prod-2 imports the new YAML from the start, so the window does not
exist there.

After the staging verification `DilosFileFetchStep@1` is unreferenced and its code is removed with
the following train, per the cleanup rule. What does **not** go with it: `DilosFileFetchCore` and
the `ISftpFileSystem` seam. The legacy polling trigger still lists through
`DilosFileFetchCore.ListMatchingFiles` and `FileKey` (`DilosFileFetchTriggerNode.cs:149-160`), and
the gate and confirm nodes still need the SFTP seam for deletes. Both die with AB#4843, not here.

## Deliberate non-goals

- **Dropping the delete retry.** Removing it would let the gate work without any SFTP access: a
  file whose delete failed would simply come round again on the next tick. It requires proof that
  the BE write-back tolerates a repeat run, and that proof does not exist. Candidate for later.
- **Moving the consume lifecycle into the product.** A product-side concept of a consumed file
  with process-local state, or a product delete node, would put pipeline policy into the product.
  The boundary stays: product does mechanics, adapter does policy.
- **A processed subdirectory as a third mode.** The classic SFTP consumer pattern, and it would
  survive a pod restart where the in-memory state does not - but it writes into the partner's
  directory and needs their agreement. Out of scope here.

## Known limitation carried over

The keep-mode memory is process-local. A pod restart re-emits every file still on the server,
which the downstream write absorbs through its replay skip. Should keep mode become the permanent
production mode, this deserves a durable marker - see the processed-subdirectory note above.

## Follow-ups outside this repo

- The plan line "fingerprint into the as/ai YAML after rollout" becomes "into the `LkvSftp`
  GlobalConfiguration entry".
- AB#4846 stage 2 names one new node today; it becomes two, `SftpList@1` and `SftpDownload@1`.

## Revisions from review

Three independent reviews of the product PR changed the following. They are recorded here because
the adapter-side PR builds on this contract.

- **The age guard only applies when asked for.** `minFileAgeSeconds` defaults to 0 on the product
  node rather than the 60 the adapter node used, and at 0 no comparison happens at all. An
  unconditional comparison drops a file whose modification time runs ahead of the pod's clock,
  and the two clocks are independent. The AR and BE pipelines set 60 explicitly.
- **A listing entry has to name one member of the listed directory.** A name carrying a path
  separator is reported and dropped: it comes from a misbehaving or hostile server and would
  otherwise steer the following download outside the listed directory.
- **The emitted timestamp is spelled out rather than round-tripped.** The round-trip specifier
  renders according to the value's `Kind`, so a local value would carry a daylight-saving
  dependent offset. The identity a consumer derives from it has to be stable regardless.
- **The glob runs non-backtracking.** Each wildcard becomes an independent `.*`, and the file name
  comes from the remote server, so a name chosen against a multi-wildcard pattern took minutes on
  the backtracking engine. The anchors are `\A` and `\z` because `$` also matches before a
  trailing newline, and matching is culture-invariant because case folding otherwise follows the
  pod's culture.
- **Three optional timeouts**, all defaulting to SSH.NET's current behaviour: 30 seconds to
  connect, no limit on an operation, no limit on waiting for a free slot. A finite default would
  have been the first place in that codebase to bound an SFTP operation and would have changed
  behaviour for every existing pipeline. A server whose transfer sizes are known sets them in its
  configuration entry, which is what the LKV entry does.
- **Credentials never print.** The settings record overrides `ToString` to redact the password,
  the private key and its passphrase, and the private key file is disposed with the session.

One promised test could not be written as specified: "semaphore honoured per server entry" needs a
successful connection to hold a slot, so it needs a live server. What is verified instead is that
the counters are created in the expected place and scope.
