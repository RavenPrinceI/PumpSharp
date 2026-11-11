# Third-party notices

PumpSharp is not affiliated with, endorsed by or maintained by Pump.fun.
"Pump" and "PumpSwap" are used only to name the on-chain programs this library
reads.

## Pump program IDLs

`src/PumpSharp/protocol/pump.json`, `pump_amm.json` and `pump_fees.json` are
copied unmodified from `src/idl/` of the npm package
[`@pump-fun/pump-sdk`](https://github.com/pump-fun/pump-sdk) version 4.3.1
(author: pump-fun). The package declares the MIT licence in its
`package.json`; it does not ship a separate licence text. The files are
embedded in the PumpSharp assembly so their hashes can be checked at runtime.

| File | SHA-256 |
|---|---|
| `pump.json` | `c53b8f5d39887afd…` (full hash: `PumpIdl.Pump.Sha256`) |
| `pump_amm.json` | `c2f8ce0bc0a740d9…` (full hash: `PumpIdl.PumpSwap.Sha256`) |
| `pump_fees.json` | `f111d2e5c9aa3d4e…` (full hash: `PumpIdl.PumpFees.Sha256`) |

## Test vectors

The JSON files in `tests/PumpSharp.Tests/parity/` were produced by running the
generator scripts beside them against `@pump-fun/pump-sdk` 4.3.1 and
[`@pump-fun/pump-swap-sdk`](https://github.com/pump-fun/pump-swap-sdk) 2.2.0
(both MIT). They contain computed numbers, not SDK code. The SDKs are not
redistributed.
