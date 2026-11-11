# Contributing

Protocol layouts and maths must come from the official Pump IDLs and SDKs, not
from another repository or a blog post.

## Build and test

```bash
dotnet build PumpSharp.slnx -c Release
dotnet test --solution PumpSharp.slnx -c Release
```

The .NET 10 SDK is required. Warnings are errors.

## Changing quote maths or decoding

1. Change the code.
2. Regenerate or extend the vectors in `tests/PumpSharp.Tests/parity/` by
   running the matching `generate-*.js` script against the pinned official SDK
   installed outside this repository, for example:
   ```bash
   mkdir /some/dir && cd /some/dir && npm install @pump-fun/pump-sdk@4.3.1 @pump-fun/pump-swap-sdk@2.2.0
   node /path/to/PumpSharp/tests/PumpSharp.Tests/parity/generate-vectors.js /some/dir out.json
   ```
3. Don't write expected values by hand from the formulas. A value derived from
   the code under test only shows that the code agrees with itself.
4. Every mismatch must be explained. If the library intentionally differs from
   the SDK (for example, an SDK wrapper's one-unit shave), the test must check
   that relationship.

## Upgrading the Pump SDK version

Replace all three IDLs together from the same SDK release, regenerate every
vector set, update `PumpProtocolFingerprint.Current` and the coverage audit,
and record the change in `CHANGELOG.md`. Don't mix files from different SDK
releases.

## Pull requests

Keep each pull request to one change. Describe what changed in the protocol,
or why the existing behaviour was wrong, and link the official source.
