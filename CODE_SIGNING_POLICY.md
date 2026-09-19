# Code signing policy

Free code signing is planned to be provided by [SignPath.io](https://signpath.io/), with a certificate provided by [SignPath Foundation](https://signpath.org/).

## Source and build integrity

- Signed binaries must be produced from this public repository by the checked-in GitHub Actions release workflow.
- Release builds use the committed project file, dependency lock information produced by NuGet restore, and the tagged source revision.
- Signing requests must refer to a public release tag and its corresponding automated build artifact.
- Binaries are never modified after signing.
- Each signed release requires manual approval.

## Team roles

- Committer and reviewer: [Sina Sabzevari](https://github.com/sina-sabzevari)
- Signing approver: [Sina Sabzevari](https://github.com/sina-sabzevari)

Repository and signing-service accounts used by project members must have multi-factor authentication enabled.

## Privacy

See the project [Privacy Policy](PRIVACY.md). The application does not transfer information to networked systems unless the user explicitly requests an SQL Server or Redis operation.
