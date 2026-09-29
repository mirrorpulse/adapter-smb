# MirrorPulse SMB Adapter

This is the official repository for the MirrorPulse SMB Adapter.

The repository contains the independently buildable Adapter SDK and an SMB protocol Worker. The Worker runs in its own process over the current-user Named Pipe protocol, confines paths to the configured UNC share, supports bounded range reads, conditional staged uploads, and transfer-cache cleanup for x64 and ARM64 packages.

Run `pwsh ./eng/verify.ps1` to validate the SDK and Worker. Signed releases are produced by the repository workflow.

Licensed under Apache-2.0. See [LICENSE](LICENSE).
