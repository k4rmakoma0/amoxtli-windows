# Code signing policy

## Current status

The GitHub installer is currently unsigned. This repository is preparing an
application to the SignPath Foundation free open-source code signing program.
Preparation of this workflow does not mean the project has been approved or
that any existing GitHub binary has a trusted production signature.

The Microsoft Store distribution is signed separately by Microsoft.

## Signing process after approval

Release binaries must be built from this public repository on GitHub-hosted
Windows runners. Signing requests are restricted to `main` or version tags and
must be approved manually by a release approver in SignPath.

The workflow requests signatures for Amoxtli's own executable and managed DLL,
and Inno Setup's generated uninstaller for this application. It then packages
those signed files and requests a signature for the final installer. Third-party
runtime binaries are not re-signed with the project's signing identity.

A release is labelled signed only after its Authenticode signature and timestamp
validate against Windows trust and the signer is SignPath Foundation. The
SHA-256 checksum is generated after the final signature has been applied.
Until approval, unsigned sample builds are clearly labelled `unsigned`.

After approval, the signing attribution will be:

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Maintainers and approvals

- Authors and reviewers: [Alan Anaya / k4rmakoma0](https://github.com/k4rmakoma0).
- Release signing approver: [Alan Anaya / k4rmakoma0](https://github.com/k4rmakoma0).
- Contributors' changes must be reviewed by the maintainer before release.
- Maintainers must enable multi-factor authentication for GitHub and SignPath
  before production signing is enabled.

## Privacy

See the [privacy policy for Amoxtli for Windows](PRIVACY.md). Opening documents
does not upload them to the developer. Remote images may contact the servers
specified by the document while that feature is enabled.

## Setup

The [signing setup guide](signing/README.md) documents the account configuration
and GitHub Actions settings needed after approval. Private API tokens must be
stored in GitHub Actions secrets, never in the repository or release files.
