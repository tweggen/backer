# Third-party components redistributed by Backer

The Windows installer (`BackerInstaller.iss`) bundles two external command-line
tools under `{app}\contrib`. Backer invokes both as separate processes; neither
is linked into any Backer binary.

## rclone

| | |
|---|---|
| Version | see `contrib\rclone.exe` (`rclone version`) |
| Installed to | `{app}\contrib\rclone.exe` |
| Upstream | https://rclone.org/ — https://github.com/rclone/rclone |
| Licence | MIT |

Used by `worker/WorkerRClone` as the file transfer engine for every non-git
storage technology. The installer writes its absolute path into
`RCloneService:RClonePath`.

## MinGit (Git for Windows)

| | |
|---|---|
| Version | 2.55.0.windows.5 (`MinGit-2.55.0.5-64-bit.zip`) |
| Installed to | `{app}\contrib\git` (entry point `cmd\git.exe`) |
| Upstream | https://gitforwindows.org/ — https://github.com/git-for-windows/git |
| Exact release | https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5 |
| Licence | GPL-2.0 (plus the licences of its bundled components, e.g. OpenSSL, zlib, curl) |

Used by `worker/WorkerGit` as the transfer engine for `git` storages. The
installer writes the absolute path of `cmd\git.exe` into `GitWorker:GitPath`,
because a Windows service does not inherit the installing user's `PATH` and the
agent refuses to advertise the `git` capability without a working `git`
≥ 2.31 (`WorkerGit/Services/GitWorkerService.cs`).

**Redistribution.** MinGit is bundled **unmodified**, exactly as published in
the release linked above; `contrib/fetch-mingit.ps1` pins that release and
verifies its SHA-256 before unpacking. MinGit's own `LICENSE.txt` sits at the
root of the bundled tree and is installed alongside the binaries at
`{app}\contrib\git\LICENSE.txt`.

**Written offer.** The complete corresponding source code for the bundled Git
for Windows build is published by the Git for Windows project at
https://github.com/git-for-windows/git (tag `v2.55.0.windows.5`). On request we
will also supply that source; contact the address in the repository's `LICENSE`
/ project metadata.

## Updating the pinned MinGit

1. Edit `$MinGitVersion`, `$MinGitTag` and `$MinGitSha256` in
   `contrib/fetch-mingit.ps1`.
2. Run `powershell -ExecutionPolicy Bypass -File contrib\fetch-mingit.ps1 -Force`.
   It fails loudly on a hash mismatch, a missing `cmd\git.exe`, a version below
   the agent's 2.31 floor, or a missing `LICENSE.txt`.
3. Update the version and release link in this document.
