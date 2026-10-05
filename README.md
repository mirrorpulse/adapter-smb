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
Signed releases are produced by the repository workflow.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Release governance

The release scripts and pinned staged workflow follow the template at commit
544c594. Version/tag inputs enter scripts through environment data and are
validated before paths or builds are created. Build has no signing secrets;
signing uses the `adapter-signing` environment; publishing alone has write
permission and uses `adapter-release`. Manual dispatch defaults to a verified
signed artifact without publishing a tag or Release.

Run `pwsh ./eng/verify-release.ps1` for hostile input rejection and a dual-RID
package signed with a disposable in-memory key. Production keys are read only
from signing-step environment variables. No private key file is read or exported.
The embedded inventory is verified before upload; MirrorPulse independently
verifies publisher trust at installation.

The repository owner must configure environment reviewers, trusted branch/tag
rules and signing-secret scope. YAML environment names alone do not enforce those
protections. Existing organization secrets remain compatible until that migration.
Historical v1 releases remain immutable. V2 packages contain private runtimes for
x64 and ARM64, including the exact restored .NET runtime license and third-party
notices. Native conformance disables normal runtime lookup and verifies that the
actual Worker loads `coreclr.dll` from its own signed payload. No global .NET
installation is required by the Worker.
The existing release controller deliberately retains its earlier product gate;
v2 publication requires the new native controller before any formal release.

The release workflow also verifies the newly signed candidate using MirrorPulse
16c6742 and real Local/WebDAV/SMB/FTP/SFTP Host/Worker fixtures on a disposable
runner. It records both source commits and the candidate package hash. Publishing
requires that protocol gate; signed dry-run assets remain unpublished.
