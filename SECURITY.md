# Security policy

PumpSharp is read-only. It does not sign transactions, hold keys or submit
anything. The most serious bugs are wrong numbers, such as a quote that
disagrees with the on-chain program or a decoder that accepts a layout it
should refuse.

## Reporting

Report a vulnerability privately through GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository ("Security" tab → "Report a vulnerability"). Please don't
open a public issue for it.

A quote or decode that disagrees with the official SDK, without security
impact, is an ordinary bug: open an issue with the inputs.

## Supported versions

Only the latest released version receives fixes.
