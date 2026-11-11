// Runs the official SDK; no quote formulas are copied here.
// Usage: node generate-swap-boost-vectors.js INSTALL_DIR NEW_OUTPUT
const fs = require('fs');
const path = require('path');
const root = path.resolve(process.argv[2]);
const output = process.argv[3];
const packageRoot = path.join(root, 'node_modules/@pump-fun/pump-swap-sdk');
const version = JSON.parse(fs.readFileSync(path.join(packageRoot, 'package.json'), 'utf8')).version;
if (version !== '2.2.0') throw new Error('Expected official swap SDK 2.2.0');
const sdk = require(packageRoot);
const pump = require(path.join(root, 'node_modules/@pump-fun/pump-sdk'));
const BN = require(path.join(root, 'node_modules/bn.js'));
const { PublicKey } = require(path.join(root, 'node_modules/@solana/web3.js'));
const bn = value => new BN(String(value));
const baseMint = new PublicKey('So11111111111111111111111111111111111111112');
const creator = pump.pumpPoolAuthorityPda(baseMint);
const coinCreator = new PublicKey(new Uint8Array(32).fill(7));
const rates = (lp, protocol, creatorFee) => ({ lpFeeBps: bn(lp), protocolFeeBps: bn(protocol), creatorFeeBps: bn(creatorFee) });
const schedules = {
  flat: [{ marketCapLamportsThreshold: bn(0), fees: rates(30, 20, 5) }],
  tiered: [
    { marketCapLamportsThreshold: bn(0), fees: rates(30, 20, 5) },
    { marketCapLamportsThreshold: bn(1500000), fees: rates(100, 50, 25) },
  ],
};
const globalConfig = {
  admin: PublicKey.default, lpFeeBasisPoints: bn(30), protocolFeeBasisPoints: bn(20),
  disableFlags: 0, protocolFeeRecipients: [], coinCreatorFeeBasisPoints: bn(5),
  adminSetCoinCreatorAuthority: PublicKey.default, whitelistPda: PublicKey.default,
  reservedFeeRecipient: PublicKey.default, mayhemModeEnabled: false, reservedFeeRecipients: [],
  isCashbackEnabled: false, buybackFeeRecipients: [], buybackBasisPoints: bn(0),
  boostAuthority: PublicKey.default, boostEnabled: true, creatorFeeConfigurable: false,
  maxConfigurableCreatorFeeBps: bn(0),
};
const states = [
  { name: 'ordinary', vault: '1000000', virtual: '0', protocol: '0', creatorFee: '0' },
  { name: 'ordinary-fees', vault: '1100000', virtual: '-100000', protocol: '60000', creatorFee: '40000' },
  { name: 'boost', vault: '1000000', virtual: '1000000', protocol: '0', creatorFee: '0' },
  { name: 'boost-fees', vault: '1000000', virtual: '900000', protocol: '60000', creatorFee: '40000' },
  { name: 'negative-virtual-boost', vault: '1100000', virtual: '-50000', protocol: '60000', creatorFee: '40000' },
  { name: 'negative-effective-difference', vault: '1000000', virtual: '-500000', protocol: '0', creatorFee: '0' },
  { name: 'no-real-liquidity', vault: '100000', virtual: '900000', protocol: '60000', creatorFee: '40000' },
];
const vectors = [];
for (const state of states) {
  for (const schedule of Object.keys(schedules)) {
    const feeConfig = { admin: PublicKey.default, flatFees: rates(30,20,5),
      feeTiers: schedules[schedule], stableFeeTiers: [], exoticFlatFees: rates(0,0,0) };
    for (const hasCreator of [false, true]) {
      const args = {
        baseReserve: bn(1000000), quoteReserve: bn(state.vault), virtualQuoteReserves: bn(state.virtual),
        feeBucketsTotal: bn(state.protocol).add(bn(state.creatorFee)), globalConfig,
        baseMintAccount: { supply: 1000000n, decimals: 6, isInitialized: true }, baseMint, creator,
        coinCreator: hasCreator ? coinCreator : PublicKey.default, feeConfig, slippage: 0,
        quoteMint: new PublicKey('So11111111111111111111111111111111111111112'), isMayhemMode: false,
      };
      const resolved = sdk.computeFeesBps({ ...args, baseMintSupply: bn(1000000),
        quoteReserve: args.quoteReserve.add(args.virtualQuoteReserves) });
      for (const amount of ['1', '999', '1000', '10000', '100000', '999000', '1000000', '2000000']) {
        for (const method of ['buyBaseInput', 'sellBaseInput']) {
          const row = { state: state.name, schedule, method, baseReserve: '1000000', quoteVault: state.vault,
            virtualQuote: state.virtual, protocolFees: state.protocol, creatorFees: state.creatorFee,
            hasCreator, amount, lpBps: resolved.lpFeeBps.toString(), protocolBps: resolved.protocolFeeBps.toString(),
            creatorBps: resolved.creatorFeeBps.toString() };
          try {
            const quote = sdk[method]({ ...args, base: bn(amount) });
            row.expected = { net: (quote.internalQuoteAmount ?? quote.internalQuoteAmountOut).toString(), total: quote.uiQuote.toString() };
          } catch (error) { row.error = error.message; }
          vectors.push(row);
        }
      }
    }
  }
}
// This boundary separates real liquidity from what the user receives: with
// an LP fee of 3, gross=1000 uses 997 real units while the user gets 994.
// Expected values and refusals still come only from the SDK.
for (const real of ['993', '994', '996', '997', '998', '1000']) {
  const feeConfig = { flatFees: rates(30,20,5), feeTiers: schedules.flat, stableFeeTiers: [], exoticFlatFees: rates(0,0,0) };
  const args = { base: bn(1000), baseReserve: bn(1000), quoteReserve: bn(real), virtualQuoteReserves: bn(2000).sub(bn(real)),
    feeBucketsTotal: bn(0), globalConfig, baseMintAccount: { supply: 1000n }, baseMint, creator, coinCreator, feeConfig, slippage: 0 };
  const row = { state: 'settlement-boundary', schedule: 'flat', method: 'sellBaseInput', baseReserve: '1000',
    quoteVault: real, virtualQuote: args.virtualQuoteReserves.toString(), protocolFees: '0', creatorFees: '0', hasCreator: true,
    amount: '1000', lpBps: '30', protocolBps: '20', creatorBps: '5' };
  try { const quote = sdk.sellBaseInput(args); row.expected = { net: quote.internalQuoteAmountOut.toString(), total: quote.uiQuote.toString() }; }
  catch (error) { row.error = error.message; }
  vectors.push(row);
}
fs.writeFileSync(output, JSON.stringify({ source: '@pump-fun/pump-swap-sdk', sourceVersion: version,
  generator: path.basename(__filename), feeSchedules: Object.fromEntries(Object.entries(schedules).map(([name, tiers]) =>
    [name, tiers.map(tier => ({ threshold: tier.marketCapLamportsThreshold.toString(),
      lpBps: tier.fees.lpFeeBps.toString(), protocolBps: tier.fees.protocolFeeBps.toString(),
      creatorBps: tier.fees.creatorFeeBps.toString() }))])), vectorCount: vectors.length, vectors }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ vectorCount: vectors.length, success: vectors.filter(row => row.expected).length,
  refused: vectors.filter(row => row.error).length }));
