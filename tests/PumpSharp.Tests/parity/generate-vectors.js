// Generates parity vectors by running the official Pump SDK.
//
// The expected values come from running the SDK. Copying its formulas here
// would only show that the copy agrees with itself.
//
// Nothing here reimplements the maths except the `selfCheck` block, which
// recomputes the core formula only to confirm the zero-fee setup: if the
// exported entry point does not reduce to the core helper when every rate
// is zero, the fee layer is still active and the "curve" vectors would
// include fees. The check never produces a vector.
//
// Usage: node generate-vectors.js <install-dir> <output-file>

const installDir = process.argv[2];
const outputFile = process.argv[3];

const sdk = require(installDir + '/node_modules/@pump-fun/pump-sdk');
const BN = require(installDir + '/node_modules/bn.js');
const { PublicKey } = require(installDir + '/node_modules/@solana/web3.js');

const bn = (v) => new BN(v.toString());

// Standard Pump launch state, from the Global defaults the program uses to
// seed a curve.
const INITIAL_VIRTUAL_TOKEN = '1073000000000000';
const INITIAL_VIRTUAL_QUOTE = '30000000000';
const INITIAL_REAL_TOKEN = '793100000000000';
const TOTAL_SUPPLY = '1000000000000000';

function makeGlobal({ protocolFeeBps = 0, creatorFeeBps = 0, mayhem = false } = {}) {
  return {
    initialized: true,
    authority: PublicKey.default,
    feeRecipient: PublicKey.default,
    initialVirtualTokenReserves: bn(INITIAL_VIRTUAL_TOKEN),
    initialVirtualSolReserves: bn(INITIAL_VIRTUAL_QUOTE),
    initialRealTokenReserves: bn(INITIAL_REAL_TOKEN),
    tokenTotalSupply: bn(TOTAL_SUPPLY),
    feeBasisPoints: bn(protocolFeeBps),
    withdrawAuthority: PublicKey.default,
    enableMigrate: false,
    poolMigrationFee: bn(0),
    creatorFeeBasisPoints: bn(creatorFeeBps),
    feeRecipients: Array(7).fill(PublicKey.default),
    setCreatorAuthority: PublicKey.default,
    adminSetCreatorAuthority: PublicKey.default,
    createV2Enabled: true,
    whitelistPda: PublicKey.default,
    reservedFeeRecipient: PublicKey.default,
    mayhemModeEnabled: mayhem,
    reservedFeeRecipients: Array(7).fill(PublicKey.default),
    isCashbackEnabled: false,
    buybackFeeRecipients: Array(8).fill(PublicKey.default),
    buybackBasisPoints: bn(0),
    initialVirtualQuoteReserves: bn(INITIAL_VIRTUAL_QUOTE),
    whitelistedQuoteMints: [PublicKey.default],
    creatorFeeConfigurable: false,
    maxConfigurableCreatorFeeBps: bn(0),
    holderRewardClaimAuthority: PublicKey.default,
    isHolderRewardEnabled: false,
    maxCurveDepth: 5,
  };
}

function makeCurve({
  virtualToken = INITIAL_VIRTUAL_TOKEN,
  virtualQuote = INITIAL_VIRTUAL_QUOTE,
  realToken = INITIAL_REAL_TOKEN,
  realQuote = '0',
  complete = false,
  creator = PublicKey.default,
  mayhem = false,
  quoteMint = PublicKey.default,
} = {}) {
  return {
    virtualTokenReserves: bn(virtualToken),
    virtualQuoteReserves: bn(virtualQuote),
    realTokenReserves: bn(realToken),
    realQuoteReserves: bn(realQuote),
    tokenTotalSupply: bn(TOTAL_SUPPLY),
    complete,
    creator,
    isMayhemMode: mayhem,
    isCashbackCoin: false,
    quoteMint,
    creatorFeeBps: bn(0),
    canEditCreatorFee: false,
    isHolderReward: false,
    creatorFee: bn(0),
    protocolFees: bn(0),
    depth: 0,
    initialVirtualQuoteReserves: bn(INITIAL_VIRTUAL_QUOTE),
    postCompleteBaseOut: bn(0),
    postCompleteQuoteIn: bn(0),
  };
}

// Setup check, not a vector.

// The exported buy prices on `(gross - 1) * 10_000 / (bps + 10_000)`, so at
// zero rates it prices on `gross - 1`. To get the core result for a net
// input N (what PumpBondingCurve.TryQuoteBuy takes), ask the exported
// function for N + 1.
//
// The number still comes from the SDK's code; only the input is chosen
// here.
const coreBuy = (curve, netIn) =>
  sdk.getBuyTokenAmountFromSolAmount({
    global: makeGlobal(), feeConfig: null, mintSupply: bn(TOTAL_SUPPLY),
    bondingCurve: curve, amount: bn(netIn).addn(1),
  });

const coreSell = (curve, baseIn) =>
  sdk.getSellSolAmountFromTokenAmount({
    global: makeGlobal(), feeConfig: null, mintSupply: bn(TOTAL_SUPPLY),
    bondingCurve: curve, amount: bn(baseIn),
  });

function selfCheck() {
  const global = makeGlobal();
  const curve = makeCurve();
  const net = bn('1000000000');

  const fee = sdk.getFee({
    global, feeConfig: null, mintSupply: bn(TOTAL_SUPPLY),
    bondingCurve: curve, amount: net, isNewBondingCurve: false,
  });

  if (!fee.isZero()) {
    throw new Error(`zero-rate setup is not fee-free: getFee returned ${fee}`);
  }

  // floor(in * B / (Q + in)), recomputed only to confirm that the +1 input
  // reaches the core helper and the fee layer is neutral. Never emitted.
  const core = net
    .mul(bn(INITIAL_VIRTUAL_TOKEN))
    .div(bn(INITIAL_VIRTUAL_QUOTE).add(net));

  const isolated = coreBuy(curve, net);
  if (!isolated.eq(core)) {
    throw new Error(
      `the +1 adjustment does not isolate the core buy helper: ` +
      `official ${isolated} vs core ${core}`);
  }

  const coreS = net
    .mul(bn(INITIAL_VIRTUAL_QUOTE))
    .div(bn(INITIAL_VIRTUAL_TOKEN).add(net));

  const isolatedS = coreSell(curve, net);
  if (!isolatedS.eq(coreS)) {
    throw new Error(
      `the zero-fee sell does not reduce to the core helper: ` +
      `official ${isolatedS} vs core ${coreS}`);
  }

  // Also record the gross-budget behaviour: the exported buy on a gross
  // amount is not the core helper.
  const gross = sdk.getBuyTokenAmountFromSolAmount({
    global, feeConfig: null, mintSupply: bn(TOTAL_SUPPLY),
    bondingCurve: curve, amount: net,
  });

  return {
    netIsolatedMatchesCore: true,
    coreForNet: core.toString(),
    exportedOnSameNumberAsGross: gross.toString(),
    grossMinusCore: gross.sub(core).toString(),
  };
}

// Vectors.

const vectors = [];

function emit(group, name, inputs, outputs) {
  vectors.push({ group, name, inputs, outputs });
}

const BUY_AMOUNTS = [
  '1', '2', '3', '1000', '999999', '1000000',
  '10000000', '100000000', '1000000000', '5000000000',
  '10000000000', '30000000000', '85000000000', '100000000000',
];

const SELL_AMOUNTS = [
  '1', '2', '1000', '1000000', '1000000000',
  '1000000000000', '34600000000000', '100000000000000', '793100000000000',
];

// Curve states: fresh, part-way along, and near completion.
const STATES = [
  { name: 'fresh', virtualToken: INITIAL_VIRTUAL_TOKEN, virtualQuote: INITIAL_VIRTUAL_QUOTE, realToken: INITIAL_REAL_TOKEN, realQuote: '0' },
  { name: 'early', virtualToken: '1038400000000000', virtualQuote: '31000000000', realToken: '758500000000000', realQuote: '1000000000' },
  { name: 'midway', virtualToken: '700000000000000', virtualQuote: '46000000000', realToken: '420100000000000', realQuote: '16000000000' },
  { name: 'late', virtualToken: '300000000000000', virtualQuote: '107300000000000', realToken: '20100000000000', realQuote: '77000000000' },
  { name: 'tiny-reserves', virtualToken: '1000', virtualQuote: '1000', realToken: '500', realQuote: '500' },
];

const zeroGlobal = makeGlobal();
const mintSupply = bn(TOTAL_SUPPLY);

// Group 1: the curve alone, every rate zero.
for (const state of STATES) {
  const curve = makeCurve(state);

  for (const amount of BUY_AMOUNTS) {
    const out = coreBuy(curve, amount);

    emit('curve-buy', `${state.name}/${amount}`,
      { ...state, amountIn: amount },
      { tokensOut: out.toString() });
  }

  for (const amount of SELL_AMOUNTS) {
    const out = coreSell(curve, amount);

    emit('curve-sell', `${state.name}/${amount}`,
      { ...state, amountIn: amount },
      { quoteOut: out.toString() });
  }
}

// Group 2: fee rounding and composition. Reading the source suggested each
// component is rounded up separately; these vectors confirm it.
const FEE_CASES = [
  { protocolFeeBps: 0, creatorFeeBps: 0 },
  { protocolFeeBps: 1, creatorFeeBps: 0 },
  { protocolFeeBps: 95, creatorFeeBps: 5 },
  { protocolFeeBps: 100, creatorFeeBps: 100 },
  { protocolFeeBps: 30, creatorFeeBps: 70 },
  { protocolFeeBps: 9999, creatorFeeBps: 1 },
];

const FEE_AMOUNTS = ['1', '2', '3', '7', '9999', '10000', '10001', '1000000', '1000000007'];

for (const rates of FEE_CASES) {
  // A creator that is not the default key, so the creator component applies.
  const withCreator = makeCurve({ creator: new PublicKey('11111111111111111111111111111112') });
  const global = makeGlobal(rates);

  for (const amount of FEE_AMOUNTS) {
    const total = sdk.getFee({
      global, feeConfig: null, mintSupply,
      bondingCurve: withCreator, amount: bn(amount), isNewBondingCurve: false,
    });

    emit('fee', `p${rates.protocolFeeBps}/c${rates.creatorFeeBps}/${amount}`,
      { ...rates, amount },
      { feeUnits: total.toString() });
  }
}

// Group 3: a curve with no creator pays no creator fee. The condition is in
// the SDK source and easy to miss.
for (const amount of FEE_AMOUNTS) {
  const global = makeGlobal({ protocolFeeBps: 95, creatorFeeBps: 5 });

  emit('fee-no-creator', amount,
    { protocolFeeBps: 95, creatorFeeBps: 5, amount },
    {
      feeUnits: sdk.getFee({
        global, feeConfig: null, mintSupply,
        bondingCurve: makeCurve(), amount: bn(amount), isNewBondingCurve: false,
      }).toString(),
    });
}

// Group 4: states this library refuses to price, recorded so the parity
// test can check that both agree on which cases have no price.
const REFUSALS = [
  { name: 'complete', state: { complete: true } },
  { name: 'zero-virtual-token', state: { virtualToken: '0' } },
  { name: 'migrated', state: { virtualToken: '0', virtualQuote: '0' } },
];

for (const refusal of REFUSALS) {
  const curve = makeCurve(refusal.state);
  let buy = null;
  let sell = null;

  try {
    buy = coreBuy(curve, '1000000000').toString();
  } catch (error) {
    buy = `throws: ${error.message}`;
  }

  try {
    sell = coreSell(curve, '1000000000').toString();
  } catch (error) {
    sell = `throws: ${error.message}`;
  }

  emit('unpriceable', refusal.name, refusal.state, { tokensOut: buy, quoteOut: sell });
}

// Boundary cases added after the first grid.

// A zero-rate FeeConfig: one tier from market cap zero, charging nothing.
// The v3 crossing path requires a FeeConfig, so this is how its fee layer
// is turned off.
const zeroFees = { lpFeeBps: bn(0), protocolFeeBps: bn(0), creatorFeeBps: bn(0) };
const zeroFeeConfig = {
  bump: 0,
  admin: PublicKey.default,
  flatFees: zeroFees,
  feeTiers: [{ marketCapLamportsThreshold: bn(0), fees: zeroFees }],
  stableFeeTiers: [{ marketCapLamportsThreshold: bn(0), fees: zeroFees }],
  exoticFlatFees: zeroFees,
};

// Group 5: virtual quote reserves with the high bit set. Pump's docs say
// the field may be negative, but the IDL declares it as u64, and which is
// right changes the sign of a price. This library refuses to price such a
// curve; these vectors record what the SDK does, so the difference is
// measured.
const HIGH_BIT = '2' ** 0; // placeholder, replaced below
const NEGATIVE_QUOTES = [
  '9223372036854775808',   // 2^63, the sign bit alone
  '9223372036854775809',   // 2^63 + 1
  '10223372036854775808',  // 2^63 + 1e18
  '18446744073709551615',  // u64 max, -1 read signed
];

for (const virtualQuote of NEGATIVE_QUOTES) {
  const curve = makeCurve({ virtualQuote });
  let buy = null;
  let sell = null;

  try { buy = coreBuy(curve, '1000000000').toString(); }
  catch (error) { buy = `throws: `; }

  try { sell = coreSell(curve, '1000000000000').toString(); }
  catch (error) { sell = `throws: `; }

  emit('negative-quote-reserves', virtualQuote,
    { virtualQuote, buyAmountIn: '1000000000', sellAmountIn: '1000000000000' },
    { tokensOut: buy, quoteOut: sell });
}

// Group 6: a curve quoted in a mint other than SOL. The reserve fields are
// the same, so the curve maths should match and only the fee schedule
// should differ (and at zero global rates it does not). If these disagree
// with the SOL vectors for the same numbers, the basis is affecting the
// arithmetic.
const QUOTE_MINT = new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v');

for (const state of STATES) {
  const curve = makeCurve({ ...state, quoteMint: QUOTE_MINT });

  for (const amount of ['1', '1000', '1000000000', '10000000000']) {
    emit('quote-mint-buy', state.name + '/' + amount,
      { ...state, amountIn: amount },
      { tokensOut: coreBuy(curve, amount).toString() });
  }

  for (const amount of ['1000000', '1000000000000', '100000000000000']) {
    emit('quote-mint-sell', state.name + '/' + amount,
      { ...state, amountIn: amount },
      { quoteOut: coreSell(curve, amount).toString() });
  }
}

// Group 7: the synthetic migration crossing. A budget larger than the
// remaining supply fills partly on the curve and partly on the future
// pool, using pump-amm's floor((in - 1) * B / (Q + in - 1)). These states
// are also compared directly with PumpCurveV3Quote, alongside the larger
// v3 grid.
const CROSSING_STATES = [
  { name: 'near-complete', virtualToken: '300000000000000', virtualQuote: '107300000000000', realToken: '20100000000000', realQuote: '77000000000' },
  { name: 'almost-done', virtualToken: '290000000000000', virtualQuote: '110000000000000', realToken: '1000000000', realQuote: '85000000000' },
];

for (const state of CROSSING_STATES) {
  const curve = makeCurve(state);

  for (const amount of ['1000000000', '10000000000', '100000000000', '1000000000000']) {
    let out = null;
    try {
      out = sdk.getBuyV3TokenAmountFromQuoteAmount({
        global: makeGlobal(), feeConfig: zeroFeeConfig, mintSupply,
        bondingCurve: curve, amount: bn(amount),
        // The curve's token account holds the remaining supply plus the reserve
        // that becomes the pool on migration, so it is larger than
        // realTokenReserves. Passing realTokenReserves alone leaves the future
        // pool empty, and the SDK says so.
        curveBaseTokenBalance: bn(state.realToken).add(bn('206900000000000')),
      }).toString();
    } catch (error) {
      out = 'throws: ' + ((error && error.constructor && error.constructor.name) || 'Error') + ': ' + ((error && error.message) || '(no message)');
    }

    emit('crossing', state.name + '/' + amount,
      { ...state, amountIn: amount },
      { tokensOut: out });
  }
}

const report = {
  source: '@pump-fun/pump-sdk',
  version: require(installDir + '/node_modules/@pump-fun/pump-sdk/package.json').version,
  generatedBy: 'executing the official SDK, not transcribing it',
  setupCheck: selfCheck(),
  vectorCount: vectors.length,
  vectors,
};

require('fs').writeFileSync(outputFile, JSON.stringify(report, null, 1) + '\n');

console.log(`version ${report.version}`);
console.log(`setup check: official ${report.setupCheck.official} == core ${report.setupCheck.core}`);
console.log(`${vectors.length} vectors written to ${outputFile}`);

const byGroup = {};
for (const v of vectors) byGroup[v.group] = (byGroup[v.group] || 0) + 1;
for (const [group, count] of Object.entries(byGroup)) console.log(`  ${group}: ${count}`);
