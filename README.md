# MirrorPulse SMB Adapter

This is the official repository for the MirrorPulse SMB Adapter.

The Worker consumes the fixed published SDK 0.2.1 and negotiates protocol v2 over
the current-user Named Pipe. Each enabled root has its own authorized UNC
`networkPath`. Identical file names, cursors, streams, and stable operation IDs
remain bound to their root. Disabled roots do not resolve paths, request
credentials, or access a share.

Without a `credentialReference`, a root uses the current Windows identity. With
one, the Host supplies its password over the pipe; root configuration supplies
`username` and optional `domain` (omit domain for a UPN). The Worker creates a
separate `LOGON32_LOGON_NEW_CREDENTIALS` token for remote servers and applies it
across every asynchronous source operation. Local servers instead use
`LOGON32_LOGON_NETWORK_CLEARTEXT` so the local authorization SID matches the
configured account. DNS results mixing local and remote addresses are refused.
Transfer leases are created and cleaned without impersonation, under the MP user.
The Worker does not create a mapped drive, change an
existing network connection, save a password, or write persistent settings.
Secrets are never included in normal diagnostics. Explicit gMSA authentication
is unsupported by this Windows logon type.

UNC device paths, traversal, Windows aliases, alternate streams and reparse
points are refused. File operations reuse the reviewed Windows handle and
sharing policy from the official Local Adapter. Cross-root moves are explicitly
unavailable: the Host must orchestrate separately accepted copy and delete steps
instead of claiming an atomic cross-share rename. Pending uploads use the Host
transfer cache and are cleaned on cancellation or process exit. Session receipts
are bounded; the Host owns durable recovery.

Conditional replacement holds the accepted object and its ancestor handles,
stages content under an exclusive name, and publishes without overwriting a
competing destination. A retained `.mp-recovery-<operationId>` copy is explicit
recovery evidence. Nonempty directory deletion is refused; it never recursively
accepts new children. Directory moves are refused with `DirectoryMoveUnavailable`:
an accepted directory timestamp cannot prove an unchanged child set on an SMB
server. File moves within one root, directory creation, and empty deletion are
supported. Transport errors or lost mutation acknowledgments are
reported as `MutationOutcomeAmbiguous`, and that operation is fenced within the
session. The Host must reconcile it after restart. The receipt limit also stops
new mutations when unresolved outcomes exhaust the session budget.
An unavailable server can retain a hidden `.mp-upload-<operationId>-<random>`
stage. Cleanup does not mask the original acceptance or recovery result; durable
reconciliation and later removal belong to the Host.

Run `pwsh ./eng/verify.ps1` for locked restore, Release, formatting, and path
boundaries. CI additionally uses `-RequireNative` with disposable local accounts
and actual SMB shares to check reads, uploads, per-root credential isolation,
offline roots, cancellation, stable replay, and cursor scope. Fixture setup is
restricted to GitHub Actions runners and always removes its shares and accounts.
Both x64 and ARM64 CI runners execute the same source and signed private-runtime
boundaries without skips. These include actual whole-share metadata, long UNC
paths, revision-pinned read rejection, source writer conflicts, nonempty directory
refusal, interrupted-replacement recovery, reparse redirection, and a real share
disconnect/reconnect while another root keeps working. Signed releases are
produced by the repository workflow.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Release governance

Provider publication follows the shared template controller. A reviewed,
classified `develop` to `main` merge creates a stable version; explicit Preview
dispatches on `develop` create `X.Y.Z-preview.N` versions without taking stable
`latest`. Manual dispatch defaults to a verified candidate without publication.
Version, event, branch, source SHA and confirmation inputs are validated before
paths or builds are created. Build has no signing secrets; signing uses the
branch-restricted `adapter-signing` environment. Stable publication uses the
protected `stable` environment and its human approval rule.

Run `pwsh ./eng/verify-release.ps1` for hostile input rejection and a dual-RID
package signed with a disposable in-memory key. Production keys are read only
from signing-step environment variables. No private key file is read or exported.
The embedded inventory is verified before upload; MirrorPulse independently
verifies publisher trust at installation.

Publication freezes one package, detached signature and public verification key
with source, version, length and SHA256 metadata. Both native jobs verify these
same assets before the publication job; approval never rebuilds or replaces them.
Existing releases and tags are immutable. Actual repository and environment
protection must be verified separately from YAML names.
Source packages now require protocol v2 and include private runtimes for both RIDs.
Previously released v1 packages retain their original identities and payloads.

The release workflow runs eighteen actual SMB share/Worker cases on both native
architectures and verifies production installation, Host-owned credentials,
Named Pipe routing, CfSharp demand reads and conditional mutations using fixed
MirrorPulse source `879c9ab1283de75d23e972a335734ea15b2fa291`. It records both source
commits, the exact published SDK 0.2.1 source and the candidate package hash.
Official candidates must pass the product's fixed publisher trust; exported public
keys authorize only disposable dry-run verification. Previously published v1
assets remain available until a reviewed stable v2 release supersedes `latest`.
