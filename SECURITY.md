# Security Policy

## Supported versions

Only the latest release of DackelFXP receives security fixes. Please update before reporting an issue.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub instead:
**[Security → Report a vulnerability](https://github.com/keksi2k/dackelfxp/security/advisories/new)**

Helpful details:

- affected version and operating system
- what an attacker needs (e.g. a malicious FTP server, a crafted listing or file name)
- steps to reproduce or a proof of concept
- the impact you expect

You will get an answer as soon as possible, usually within a few days. Once a fix is released, the
advisory is published and you are credited unless you prefer to stay anonymous.

## Scope

DackelFXP is a desktop FTP/FTPS/FXP client. Typical relevant issues include path traversal through file
names from a server, TLS/certificate validation flaws, leaking stored passwords, and command injection
into the FTP control connection. Release builds are self-contained and bundle the .NET runtime, so
outdated runtime versions in a release are also in scope.
