# Security Policy

How to report a suspected vulnerability in Skarp itself.

## Scope

Skarp is not a vulnerability scanner. It does not look for security problems in your code, and its findings are not security findings.

This policy is about vulnerabilities in Skarp. Examples: unsafe handling of repository content or settings files, or a problem in the hooks that Skarp installs into a project. A vulnerability in your own code that Skarp did not report is not in scope.

## Reporting

Use GitHub's "Report a vulnerability" button on the Security tab of https://github.com/sirphilliptubell/skarp-sharp. This opens a private advisory that only you and the maintainer can see. Please do not open a public issue for a suspected vulnerability.

Include the Skarp version (`skarp --version`), your operating system, the steps to reproduce, and what you expected to happen. Do not attach private code.

## Supported versions

Only the latest 0.x release receives fixes.

## What to expect

Skarp is maintained on a best-effort basis. There is no guaranteed response time. The maintainer will read the report, tell you whether it is accepted, and fix it in a new release when it is. You will be credited in the release notes if you wish.

## See also

- [CONTRIBUTING.md](CONTRIBUTING.md)
- [Git hooks](docs/git-hooks.md)
- [Check and agents](docs/check-and-agents.md)
