// Runs both official SDKs. Expected fee totals and quotes come only from
// their exported functions.
// Usage: node generate-mayhem-vectors.js INSTALL NEW_OUTPUT
const fs = require('fs');
const path = require('path');
const root = path.resolve(process.argv[2]);
const from = name => require(path.join(root, 'node_modules', name));
for (const [name, version] of [['@pump-fun/pump-sdk','4.3.1'], ['@pump-fun/pump-swap-sdk','2.2.0']]) {
  if (JSON.parse(fs.readFileSync(path.join(root, 'node_modules', name, 'package.json'), 'utf8')).version !== version)
    throw new Error('Reference SDK version differs');
}
const pump = from('@pump-fun/pump-sdk');
const swap = from('@pump-fun/pump-swap-sdk');
const BN = from('bn.js');
const { PublicKey } = from('@solana/web3.js');
const bn = value => new BN(String(value));
const key = value => new PublicKey(new Uint8Array(32).fill(value));
const baseMint = key(1), creator = key(2);
const quotes = { sol: new PublicKey('So11111111111111111111111111111111111111112'),
  stable: new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v'), exotic: key(3) };
const fees = (lp, protocol, creatorFee) => ({ lpFeeBps: bn(lp), protocolFeeBps: bn(protocol), creatorFeeBps: bn(creatorFee) });
const tier = (threshold, lp, protocol, creatorFee) => ({ marketCapLamportsThreshold: bn(threshold), fees: fees(lp,protocol,creatorFee) });
const feeConfig = legacy => ({ admin: PublicKey.default, flatFees: fees(15,6,3),
  feeTiers: [tier(0,30,20,5), tier('1000000000000',50,40,7), tier('2000000000000',70,60,9)],
  stableFeeTiers: legacy ? [] : [tier(0,25,10,2), tier('500000000000',45,30,4), tier('1500000000000',65,50,6)],
  exoticFlatFees: fees(40,4,4) });
const global = configurable => ({ initialized: true, authority: PublicKey.default, feeBasisPoints: bn(0), creatorFeeBasisPoints: bn(0),
  creatorFeeConfigurable: configurable, maxConfigurableCreatorFeeBps: bn(500), tokenTotalSupply: bn('1000000000000000'),
  lpFeeBasisPoints: bn(0), protocolFeeBasisPoints: bn(0), coinCreatorFeeBasisPoints: bn(0), buybackBasisPoints: bn(0),
  mayhemModeEnabled: true, disableFlags: 0 });
const curve = (quote, mayhem, hasCreator, reserve) => ({
  virtualTokenReserves: bn(1000000), virtualQuoteReserves: bn(reserve), realTokenReserves: bn(800000),
  realQuoteReserves: bn(reserve), tokenTotalSupply: bn('1000000000000000'), complete: false,
  creator: hasCreator ? creator : PublicKey.default, isMayhemMode: mayhem, isCashbackCoin: false,
  quoteMint: quotes[quote], creatorFeeBps: bn(25), canEditCreatorFee: false, isHolderReward: false,
  creatorFee: bn(0), protocolFees: bn(0), depth: 0, initialVirtualQuoteReserves: bn(reserve),
  postCompleteBaseOut: bn(0), postCompleteQuoteIn: bn(0) });
const poolCases = [], curveCases = [], curveMath = [];
for (const supply of ['1000000', '1000000000000000', '2000000000000000']) {
  for (const reserve of ['999','1000','1001','1999','2000','2001']) {
    for (const mayhem of [false,true]) {
      for (const quote of Object.keys(quotes)) {
        for (const legacy of [false,true]) {
          for (const configurable of [false,true]) {
            const config = feeConfig(legacy), globals = global(configurable);
            const common = { supply, baseReserve: '1000000', quoteReserve: reserve, mayhem, quote, legacy, configurable, creatorBps: '25' };
            for (const canonical of [false,true]) {
              const args = { globalConfig: globals, feeConfig: config, baseMintSupply: bn(supply),
                baseMintAccount: { supply: BigInt(supply) }, baseMint, baseReserve: bn(1000000), quoteReserve: bn(reserve),
                isMayhemMode: mayhem, quoteMint: quotes[quote], creatorFeeBps: bn(25),
                creator: canonical ? pump.pumpPoolAuthorityPda(baseMint) : key(4), coinCreator: creator, slippage: 0 };
              const resolved = swap.computeFeesBps(args);
              const buy = swap.buyBaseInput({ ...args, base: bn(500000) });
              const sell = swap.sellBaseInput({ ...args, base: bn(500000) });
              poolCases.push({ ...common, canonical,
                protocolBps: resolved.protocolFeeBps.toString(), lpBps: resolved.lpFeeBps.toString(), resolvedCreatorBps: resolved.creatorFeeBps.toString(),
                buyNet: buy.internalQuoteAmount.toString(), buyTotal: buy.uiQuote.toString(),
                sellNet: sell.internalQuoteAmountOut.toString(), sellTotal: sell.uiQuote.toString() });
            }
            for (const hasCreator of [false,true]) {
              const account = curve(quote, mayhem, hasCreator, reserve);
              const args = { global: globals, feeConfig: config, mintSupply: bn(supply), bondingCurve: account, isNewBondingCurve: false };
              // At 10,000 the SDK's fee equals the rate in basis points. The no-creator
              // and creator variants isolate each component without copying the SDK's
              // supply-basis logic into this script.
              const protocol = pump.getFee({ ...args, bondingCurve: { ...account, creator: PublicKey.default }, amount: bn(10000) });
              const both = pump.getFee({ ...args, bondingCurve: { ...account, creator }, amount: bn(10000) });
              curveCases.push({ ...common, hasCreator, protocolBps: protocol.toString(), resolvedCreatorBps: both.sub(protocol).toString(),
                feeTotals: ['1','1000','1000001'].map(amount => ({ amount, fee: pump.getFee({ ...args, amount: bn(amount) }).toString() })) });
            }
          }
        }
      }
    }
  }
}
for (const mayhem of [false,true]) {
  for (const reserve of ['1000','30000000000','107300000000']) {
    const account = { ...curve('sol', mayhem, true, reserve), virtualTokenReserves: bn('1073000000000000'),
      realTokenReserves: bn('793100000000000'), creatorFeeBps: bn(0) };
    for (const amount of ['1','1000','1000000','1000000000','100000000000000']) {
      const args = { global: global(false), feeConfig: null, mintSupply: bn('1000000000000000'), bondingCurve: account };
      curveMath.push({ mayhem, virtualBase: account.virtualTokenReserves.toString(), virtualQuote: reserve,
        realBase: account.realTokenReserves.toString(), realQuote: account.realQuoteReserves.toString(), amount,
        // +1 gets the net curve input past the exported buy's one-unit shave.
        buyBase: pump.getBuyTokenAmountFromSolAmount({ ...args, amount: bn(amount).addn(1) }).toString(),
        sellQuote: pump.getSellSolAmountFromTokenAmount({ ...args, amount: bn(amount) }).toString() });
    }
  }
}
const serializeTier = t => ({ threshold: t.marketCapLamportsThreshold.toString(), lp: t.fees.lpFeeBps.toString(),
  protocol: t.fees.protocolFeeBps.toString(), creator: t.fees.creatorFeeBps.toString() });
const config = feeConfig(false);
fs.writeFileSync(process.argv[3], JSON.stringify({ pumpVersion: '4.3.1', swapVersion: '2.2.0',
  solTiers: config.feeTiers.map(serializeTier), stableTiers: config.stableFeeTiers.map(serializeTier),
  poolCases, curveCases, curveMath }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ poolCases: poolCases.length, curveCases: curveCases.length, curveMath: curveMath.length }));
