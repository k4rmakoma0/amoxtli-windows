# SignPath Foundation setup

The project must be accepted by [SignPath Foundation](https://signpath.org/apply)
before publicly trusted production signatures can be requested. Approval is
manual and is not guaranteed. The program also evaluates the project's
verifiable reputation; publication in Microsoft Store is supporting evidence,
not an automatic approval.

## Application

1. Publish the MIT license, [Code signing policy](../CODE_SIGNING.md),
   [Windows privacy policy](../PRIVACY.md), build scripts and workflow in this
   repository.
2. Run the `Windows installer` workflow with `sign: false` to provide a
   verifiable unsigned installer sample from GitHub-hosted runners. Relevant
   pushes to `main` also build an unsigned sample automatically. Make an
   unsigned release available if requested during the application review, with
   a clear `unsigned` label and a link to the Code signing policy.
3. Apply at <https://signpath.org/apply>. The maintainer must review and accept
   the Foundation's conditions and privacy terms personally.
4. Enable multi-factor authentication on GitHub and SignPath. Install the
   SignPath GitHub App for this repository when the approved setup requires it.

No API token or private signing key is included in this checkout.

## SignPath project configuration after approval

Use a GitHub.com trusted build system linked to
`k4rmakoma0/amoxtli-windows`, with GitHub-hosted runners required. Configure a
production signing policy that requires manual maintainer approval and limits
source refs to `main` and reviewed version tags.

Create two artifact configurations using the XML files in this directory:

- `payload.xml`: ZIP containing `app/Amoxtli.exe`, `app/Amoxtli.dll` and
  `uninstaller/Amoxtli-uninstaller.exe`. Only these application-specific files are signed.
- `installer.xml`: ZIP containing `Amoxtli-*-windows-x64-setup.exe`.

The action uploads ZIP artifacts, so both configurations have a `zip-file` root.
Both restrict the product name to `Amoxtli` and the product version to the
required `version` signing parameter. Review these configurations with SignPath
before enabling production signing.

## GitHub Actions configuration

Create one repository Actions secret:

| Secret | Value |
| --- | --- |
| `SIGNPATH_API_TOKEN` | Token for the approved SignPath CI submitter |

Create these repository Actions variables with the exact values from SignPath:

| Variable | Value |
| --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | Organization ID |
| `SIGNPATH_PROJECT_SLUG` | Project slug |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Approved production signing policy slug |
| `SIGNPATH_PAYLOAD_CONFIGURATION` | Slug of the payload artifact configuration |
| `SIGNPATH_INSTALLER_CONFIGURATION` | Slug of the installer artifact configuration |

## Build and sign

Run `.github/workflows/windows-release.yml` from `main` or a reviewed version
tag, supplying the version and `sign: true`. Approve both signing requests in
SignPath while the workflow waits: first the app and uninstaller, then the
finished installer. Each request can wait up to 30 minutes for approval.

Inno Setup first exports its unsigned uninstaller as a `.e32` or `.e64` cache
file and stops, requesting an external signature. This preparation step succeeds
only when the expected compiler exit and exactly one uninstaller are present.
The PE file is uploaded as `Amoxtli-uninstaller.exe`, then the signed result is
restored under its original cache filename. The same compiler is used to embed
it in the final installer.

The `amoxtli-windows-signed-VERSION` workflow artifact contains the verified
installer and its final SHA-256 checksum. Attach those files to GitHub Releases
and link the Code signing policy in the release notes. This workflow does not
publish releases automatically.

An unsigned, untrusted, wrong-publisher or non-timestamped file causes the
production packaging or final verification step to fail. There is no fallback
that labels an unsigned file as signed.

References: [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[artifact configurations](https://docs.signpath.io/artifact-configuration/),
[Inno signed uninstallers](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm).
