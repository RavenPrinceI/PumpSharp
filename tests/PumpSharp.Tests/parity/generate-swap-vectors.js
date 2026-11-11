// Generates parity vectors for PumpSwap pool quotes by running the official
// PumpSwap SDK.
//
// As in generate-vectors.js, the expected values come from running the SDK,
// not from copying its formulas. The only maths written here is the
// `selfCheck` block, which confirms that zero fee rates really remove the
// fee layer. Without it, the "curve" vectors could quietly include a fee.
// The bonding-curve SDK has exactly this kind of quirk: its buy entry point
// prices on `amount - 1`.
//
// Usage: node generate-swap-vectors.js <install-dir> <output-file>
//
// <install-dir> holds `npm install @pump-fun/pump-swap-sdk`. It is passed
// in so the downloaded package stays outside this repository.

const installDir = process.argv[2];
const outputFile = process.argv[3];

const swap = require(installDir + '/node_modules/@pump-fun/pump-swap-sdk');

// Tier schedules apply only to a canonical pump pool. The SDK decides that
// from the pool's creator: `isPumpPool(baseMint, poolCreator)` compares it
// with `pumpPoolAuthorityPda(baseMint)`. Any other pool pays `flatFees`,
// whatever its quote mint or market cap. The first version of this grid
// passed the default key as the creator, and every SOL case came back on
// the flat schedule.
const pump = require(installDir + '/node_modules/@pump-fun/pump-sdk');
const BN = require(installDir + '/node_modules/bn.js');
const { PublicKey } = require(installDir + '/node_modules/@solana/web3.js');
const fs = require('fs');

const bn = (v) => new BN(v.toString());

// The mints that select each fee schedule, taken from the SDK's exported
// constants so they follow any program upgrade.
const WSOL = swap.SOL_LIKE_QUOTE_MINTS[swap.SOL_LIKE_QUOTE_MINTS.length - 1];
const USDC = swap.STABLE_QUOTE_MINTS[0];
const EXOTIC = new PublicKey('4k3Dyjzvzp8eMZWUXbBCjEvwSkkk59S5iCNLY3QrkX6R');

// A coin creator that is set. A pool whose coin creator is the default key
// pays no creator fee, whatever the schedule says.
const COIN_CREATOR = new PublicKey('CoinCrea1orFeeRecipient111111111111111111111');

const fees = (lp, protocol, creator) => ({
  lpFeeBps: bn(lp),
  protocolFeeBps: bn(protocol),
  creatorFeeBps: bn(creator),
});

const globalConfig = (overrides = {}) => ({
  admin: PublicKey.default,
  lpFeeBasisPoints: bn(0),
  protocolFeeBasisPoints: bn(0),
  disableFlags: 0,
  protocolFeeRecipients: Array(8).fill(PublicKey.default),
  coinCreatorFeeBasisPoints: bn(0),
  adminSetCoinCreatorAuthority: PublicKey.default,
  whitelistPda: PublicKey.default,
  reservedFeeRecipient: PublicKey.default,
  mayhemModeEnabled: false,
  reservedFeeRecipients: Array(8).fill(PublicKey.default),
  isCashbackEnabled: false,
  buybackFeeRecipients: Array(8).fill(PublicKey.default),
  buybackBasisPoints: bn(0),
  boostAuthority: PublicKey.default,
  boostEnabled: false,
  creatorFeeConfigurable: false,
  ...overrides,
});

// One tier from zero market cap, so a flat schedule is expressible as a tier
// list. The tiered cases below supply their own thresholds.
const feeConfig = (f, overrides = {}) => ({
  admin: PublicKey.default,
  flatFees: f,
  feeTiers: [{ marketCapLamportsThreshold: bn(0), fees: f }],
  stableFeeTiers: [],
  exoticFlatFees: fees(0, 0, 0),
  ...overrides,
});

// A migrated pump pool's opening state: roughly the real reserves a
// completed bonding curve hands over.
const BASE_RESERVE = '206900000000000';
const QUOTE_RESERVE = '85000000000';
const BASE_SUPPLY = '1000000000000000';

const mintAccount = (supply) => ({
  mintAuthorityOption: 0,
  mintAuthority: PublicKey.default,
  supply: bn(supply),
  decimals: 6,
  isInitialized: true,
  freezeAuthorityOption: 0,
  freezeAuthority: PublicKey.default,
});

const vectors = [];

// The SDK refuses some inputs instead of pricing them, such as a quote
// budget too small to cover the fees, which it reports as an Error rather
// than zero. A refusal is a parity target like any number: the library must
// refuse the same inputs, so the refusal is recorded as the vector.
function attempt(call) {
  try {
    return call();
  } catch (error) {
    return {
      referenceThrew: error.constructor.name,
      referenceMessage: error.message,
    };
  }
}

function emit(kind, name, state, input, result, extra = {}) {
  vectors.push({
    kind,
    name,
    baseReserve: state.baseReserve.toString(),
    quoteReserve: state.quoteReserve.toString(),
    baseSupply: state.baseSupply.toString(),
    virtualQuoteReserves: (state.virtualQuoteReserves ?? bn(0)).toString(),
    lpFeeBps: state.schedule.lpFeeBps.toString(),
    protocolFeeBps: state.schedule.protocolFeeBps.toString(),
    creatorFeeBps: state.schedule.creatorFeeBps.toString(),
    hasCoinCreator: state.hasCoinCreator === true,
    quoteSchedule: state.quoteSchedule,
    isMayhemMode: state.isMayhemMode === true,
    input: input.toString(),
    ...result,
    ...extra,
  });
}

function stateOf({
  schedule,
  hasCoinCreator = false,
  quoteMint = WSOL,
  quoteSchedule = 'sol',
  baseReserve = BASE_RESERVE,
  quoteReserve = QUOTE_RESERVE,
  baseSupply = BASE_SUPPLY,
  virtualQuoteReserves = null,
  isMayhemMode = false,
  feeConfigOverrides = {},
  globalOverrides = {},
}) {
  return {
    schedule,
    hasCoinCreator,
    quoteSchedule,
    isMayhemMode,
    baseReserve: bn(baseReserve),
    quoteReserve: bn(quoteReserve),
    baseSupply: bn(baseSupply),
    virtualQuoteReserves: virtualQuoteReserves === null ? null : bn(virtualQuoteReserves),
    args: {
      baseReserve: bn(baseReserve),
      quoteReserve: bn(quoteReserve),
      globalConfig: globalConfig(globalOverrides),
      feeConfig: feeConfig(schedule, feeConfigOverrides),
      baseMintAccount: mintAccount(baseSupply),
      baseMint: PublicKey.default,
      coinCreator: hasCoinCreator ? COIN_CREATOR : PublicKey.default,
      creator: PublicKey.default,
      quoteMint,
      isMayhemMode,
      slippage: 0,
      ...(virtualQuoteReserves === null ? {} : { virtualQuoteReserves: bn(virtualQuoteReserves) }),
    },
  };
}

// Setup check. Never produces a vector.

function selfCheck() {
  const state = stateOf({ schedule: fees(0, 0, 0) });
  const base = bn('1000000000');
  const got = swap.buyBaseInput({ ...state.args, base }).internalQuoteAmount;

  // Plain constant product with a ceiling, computed here only to confirm the
  // fee layer does nothing at zero rates.
  const br = bn(BASE_RESERVE);
  const qr = bn(QUOTE_RESERVE);
  const numerator = qr.mul(base);
  const denominator = br.sub(base);
  const floor = numerator.div(denominator);
  const expected = numerator.mod(denominator).isZero() ? floor : floor.add(new BN(1));

  if (!got.eq(expected)) {
    throw new Error(
      'Zero fee rates do not reduce to constant product: reference '
      + got.toString() + ' vs ' + expected.toString()
      + '. The fee layer is not neutralised, so the curve vectors would carry a fee.');
  }
}

// The grid.

const SIZES = ['1000000', '100000000', '1000000000', '50000000000', '1000000000000'];
const QUOTE_SIZES = ['100000', '1000000', '100000000', '1000000000', '10000000000'];

function curveCases() {
  const state = stateOf({ schedule: fees(0, 0, 0) });

  for (const size of SIZES) {
    const base = bn(size);
    emit('curve', 'buy_base_input', state, base, attempt(() => {
      const buy = swap.buyBaseInput({ ...state.args, base });
      return {
        internalQuote: buy.internalQuoteAmount.toString(),
        uiQuote: buy.uiQuote.toString(),
      };
    }));

    emit('curve', 'sell_base_input', state, base, attempt(() => {
      const sell = swap.sellBaseInput({ ...state.args, base, feeBucketsTotal: bn(0) });
      return {
        internalQuote: sell.internalQuoteAmountOut.toString(),
        uiQuote: sell.uiQuote.toString(),
      };
    }));
  }

  for (const size of QUOTE_SIZES) {
    const quote = bn(size);
    emit('curve', 'buy_quote_input', state, quote, attempt(() => {
      const buy = swap.buyQuoteInput({ ...state.args, quote });
      return {
        base: buy.base.toString(),
        internalQuote: buy.internalQuoteWithoutFees.toString(),
      };
    }));

    emit('curve', 'sell_quote_input', state, quote, attempt(() => {
      const sell = swap.sellQuoteInput({ ...state.args, quote, feeBucketsTotal: bn(0) });
      return {
        base: sell.base.toString(),
        internalQuote: sell.internalRawQuote.toString(),
      };
    }));
  }
}

function feeCases() {
  // Fee composition. The rates are chosen so one ceiling of the summed rate
  // and two ceilings summed give different answers, which is the only way a
  // vector can tell them apart.
  const schedules = [
    fees(20, 5, 0),
    fees(20, 5, 5),
    fees(25, 0, 0),
    fees(1, 1, 1),
    fees(0, 0, 30),
    fees(99, 1, 1),
  ];

  for (const schedule of schedules) {
    for (const hasCoinCreator of [false, true]) {
      const state = stateOf({ schedule, hasCoinCreator });

      for (const size of ['1000000', '1000000000', '50000000000']) {
        const base = bn(size);

        emit('fees', 'buy_base_input', state, base, attempt(() => {
          const buy = swap.buyBaseInput({ ...state.args, base });
          return {
            internalQuote: buy.internalQuoteAmount.toString(),
            uiQuote: buy.uiQuote.toString(),
          };
        }));

        emit('fees', 'sell_base_input', state, base, attempt(() => {
          const sell = swap.sellBaseInput({ ...state.args, base, feeBucketsTotal: bn(0) });
          return {
            internalQuote: sell.internalQuoteAmountOut.toString(),
            uiQuote: sell.uiQuote.toString(),
          };
        }));
      }

      for (const size of ['1000000', '1000000000', '10000000000']) {
        const quote = bn(size);

        emit('fees', 'buy_quote_input', state, quote, attempt(() => {
          const buy = swap.buyQuoteInput({ ...state.args, quote });
          return {
            base: buy.base.toString(),
            internalQuote: buy.internalQuoteWithoutFees.toString(),
          };
        }));

        emit('fees', 'sell_quote_input', state, quote, attempt(() => {
          const sell = swap.sellQuoteInput({ ...state.args, quote, feeBucketsTotal: bn(0) });
          return {
            base: sell.base.toString(),
            internalQuote: sell.internalRawQuote.toString(),
          };
        }));
      }
    }
  }
}

function tierCases() {
  // The fee program tiers by market cap only; the on-chain `get_fees`
  // argument named for trade size is ignored. So the schedule depends on pool
  // state, not on the order.
  const tiers = {
    feeTiers: [
      { marketCapLamportsThreshold: bn(0), fees: fees(95, 5, 5) },
      { marketCapLamportsThreshold: bn('100000000000'), fees: fees(50, 5, 5) },
      { marketCapLamportsThreshold: bn('1000000000000'), fees: fees(20, 5, 5) },
      { marketCapLamportsThreshold: bn('10000000000000'), fees: fees(10, 2, 5) },
    ],
  };

  const reserves = [
    ['85000000000', 'tier_low'],
    ['850000000000', 'tier_mid'],
    ['8500000000000', 'tier_high'],
  ];

  for (const [quoteReserve, label] of reserves) {
    const state = stateOf({
      schedule: fees(95, 5, 5),
      hasCoinCreator: true,
      quoteReserve,
      feeConfigOverrides: tiers,
    });

    const base = bn('1000000000');
    const priced = attempt(() => {
      const buy = swap.buyBaseInput({ ...state.args, base });
      return {
        internalQuote: buy.internalQuoteAmount.toString(),
        uiQuote: buy.uiQuote.toString(),
      };
    });

    const resolved = swap.computeFeesBps({
      globalConfig: state.args.globalConfig,
      feeConfig: state.args.feeConfig,
      creator: PublicKey.default,
      baseMintSupply: state.baseSupply,
      baseMint: PublicKey.default,
      baseReserve: state.baseReserve,
      quoteReserve: state.quoteReserve,
      quoteMint: WSOL,
    });

    emit('tier', 'buy_base_input_' + label, state, base, {
      ...priced,
      resolvedLpFeeBps: resolved.lpFeeBps.toString(),
      resolvedProtocolFeeBps: resolved.protocolFeeBps.toString(),
      resolvedCreatorFeeBps: resolved.creatorFeeBps.toString(),
    });
  }
}

function boundaryCases() {
  const state = stateOf({ schedule: fees(20, 5, 5), hasCoinCreator: true });

  // One base unit, where every ceiling shows, and an order that takes most of
  // the pool, where the curve is steepest.
  for (const size of ['1', '2', '999', '103450000000000']) {
    const base = bn(size);
    emit('boundary', 'buy_base_input', state, base, attempt(() => {
      const buy = swap.buyBaseInput({ ...state.args, base });
      return {
        internalQuote: buy.internalQuoteAmount.toString(),
        uiQuote: buy.uiQuote.toString(),
      };
    }));

    emit('boundary', 'sell_base_input', state, base, attempt(() => {
      const sell = swap.sellBaseInput({ ...state.args, base, feeBucketsTotal: bn(0) });
      return {
        internalQuote: sell.internalQuoteAmountOut.toString(),
        uiQuote: sell.uiQuote.toString(),
      };
    }));
  }

  // A budget of one or two quote units cannot cover a fee, and the SDK
  // refuses it rather than returning zero base. That refusal is the vector.
  for (const size of ['1', '2', '999', '100000']) {
    const quote = bn(size);
    emit('boundary', 'buy_quote_input', state, quote, attempt(() => {
      const buy = swap.buyQuoteInput({ ...state.args, quote });
      return {
        base: buy.base.toString(),
        internalQuote: buy.internalQuoteWithoutFees.toString(),
      };
    }));
  }
}

// Observations, not parity targets: what the SDK does in cases this library
// refuses to price. Recorded so the refusal is backed by data, as with the
// negative virtual_quote_reserves cases for the curve.

function observationCases() {
  const base = bn('1000000000');

  const observe = (name, state, extra) => {
    emit('observation', name, state, base, attempt(() => {
      const buy = swap.buyBaseInput({ ...state.args, base });
      return {
        internalQuote: buy.internalQuoteAmount.toString(),
        uiQuote: buy.uiQuote.toString(),
      };
    }), extra);
  };

  observe('mayhem_pool', stateOf({
    schedule: fees(20, 5, 5),
    hasCoinCreator: true,
    isMayhemMode: true,
    globalOverrides: { mayhemModeEnabled: true },
  }), { note: 'mayhem pools take a fixed total supply as the market-cap basis' });

  observe('boost_pool_virtual_quote', stateOf({
    schedule: fees(20, 5, 5),
    hasCoinCreator: true,
    virtualQuoteReserves: '5000000000',
    globalOverrides: { boostEnabled: true },
  }), { note: 'non-zero virtual quote reserves: a boost pool prices on reserve + virtual' });

  observe('stable_quote_mint', stateOf({
    schedule: fees(20, 5, 5),
    hasCoinCreator: true,
    quoteMint: USDC,
    quoteSchedule: 'stable',
  }), { note: 'USDC selects stableFeeTiers, empty here, so the reference falls back' });

  observe('exotic_quote_mint', stateOf({
    schedule: fees(20, 5, 5),
    hasCoinCreator: true,
    quoteMint: EXOTIC,
    quoteSchedule: 'exotic',
  }), { note: 'neither SOL-like nor listed stable: exoticFlatFees, unset here' });
}

// Fee schedule resolution: which rates a pool pays, before any trade is
// priced. Resolving it needs the FeeConfig account and the SDK's market-cap
// formula.
//
// The grid sits one unit either side of every tier threshold, because
// whether a threshold is inclusive only shows up on pools exactly at the
// edge.

function feeScheduleCases() {
  const tiers = [
    { marketCapLamportsThreshold: bn(0), fees: fees(95, 5, 5) },
    { marketCapLamportsThreshold: bn('100000000000'), fees: fees(50, 10, 5) },
    { marketCapLamportsThreshold: bn('1000000000000'), fees: fees(20, 15, 5) },
    { marketCapLamportsThreshold: bn('10000000000000'), fees: fees(10, 20, 5) },
  ];

  const stableTiers = [
    { marketCapLamportsThreshold: bn(0), fees: fees(30, 3, 3) },
    { marketCapLamportsThreshold: bn('500000000000'), fees: fees(15, 6, 3) },
  ];

  // Reserve pairs chosen so the market cap lands just under, on and just over
  // each threshold under the SDK's formula. The formula is what these vectors
  // measure, so the inputs are recorded as given and nothing here computes a
  // cap.
  const states = [];
  for (const quoteReserve of [
    '1000000000', '9999999999', '10000000000', '10000000001',
    '85000000000', '99999999999', '100000000000', '100000000001',
    '999999999999', '1000000000000', '1000000000001', '9000000000000',
  ]) {
    states.push({ baseReserve: BASE_RESERVE, quoteReserve, baseSupply: BASE_SUPPLY });
  }

  // A different supply at the same reserves, so a cap that ignored supply
  // would be caught.
  states.push({ baseReserve: BASE_RESERVE, quoteReserve: '85000000000', baseSupply: '500000000000000' });
  states.push({ baseReserve: '103450000000000', quoteReserve: '85000000000', baseSupply: BASE_SUPPLY });

  const schedules = [
    { label: 'sol', quoteMint: WSOL, quoteSchedule: 'sol' },
    { label: 'stable', quoteMint: USDC, quoteSchedule: 'stable' },
    { label: 'exotic', quoteMint: EXOTIC, quoteSchedule: 'exotic' },
  ];

  // The canonical pool authority is derived from the base mint, so the mint
  // must be a real key, not the default.
  const BASE_MINT = new PublicKey(new Uint8Array(32).fill(7));
  const CANONICAL_CREATOR = pump.pumpPoolAuthorityPda(BASE_MINT);

  for (const schedule of schedules) {
    for (const state of states) {
      for (const canonical of [true, false]) {
      for (const mayhem of [false, true]) {
        for (const creatorOverride of [null, 250]) {
          const globalOverrides = {
            mayhemModeEnabled: mayhem,
            creatorFeeConfigurable: creatorOverride !== null,
          };

          const config = feeConfig(fees(777, 7, 7), {
            feeTiers: tiers,
            stableFeeTiers: stableTiers,
            exoticFlatFees: schedule.label === 'exotic' ? fees(40, 4, 4) : fees(0, 0, 0),
          });

          const args = {
            globalConfig: globalConfig(globalOverrides),
            feeConfig: config,
            creator: canonical ? CANONICAL_CREATOR : PublicKey.default,
            baseMintSupply: bn(state.baseSupply),
            baseMint: BASE_MINT,
            baseReserve: bn(state.baseReserve),
            quoteReserve: bn(state.quoteReserve),
            quoteMint: schedule.quoteMint,
            isMayhemMode: mayhem,
            ...(creatorOverride === null ? {} : { creatorFeeBps: bn(creatorOverride) }),
          };

          const resolved = attempt(() => {
            const r = swap.computeFeesBps(args);
            return {
              resolvedLpFeeBps: r.lpFeeBps.toString(),
              resolvedProtocolFeeBps: r.protocolFeeBps.toString(),
              resolvedCreatorFeeBps: r.creatorFeeBps.toString(),
            };
          });

          vectors.push({
            kind: 'feeSchedule',
            name: 'compute_fees_bps_' + schedule.label,
            baseReserve: state.baseReserve,
            quoteReserve: state.quoteReserve,
            baseSupply: state.baseSupply,
            quoteSchedule: schedule.label,
            isCanonicalPumpPool: canonical,
            isMayhemMode: mayhem,
            creatorFeeConfigurable: creatorOverride !== null,
            poolCreatorFeeBps: creatorOverride === null ? '0' : String(creatorOverride),
            flatLpFeeBps: '777',
            flatProtocolFeeBps: '7',
            flatCreatorFeeBps: '7',
            exoticSet: schedule.label === 'exotic',
            tiers: tiers.map((t) => ({
              thresholdLamports: t.marketCapLamportsThreshold.toString(),
              lpFeeBps: t.fees.lpFeeBps.toString(),
              protocolFeeBps: t.fees.protocolFeeBps.toString(),
              creatorFeeBps: t.fees.creatorFeeBps.toString(),
            })),
            stableTiers: stableTiers.map((t) => ({
              thresholdLamports: t.marketCapLamportsThreshold.toString(),
              lpFeeBps: t.fees.lpFeeBps.toString(),
              protocolFeeBps: t.fees.protocolFeeBps.toString(),
              creatorFeeBps: t.fees.creatorFeeBps.toString(),
            })),
            input: '0',
            ...resolved,
          });
        }
      }
      }
    }
  }
}

selfCheck();
curveCases();
feeScheduleCases();
feeCases();
tierCases();
boundaryCases();
observationCases();

const document = {
  generator: 'generate-swap-vectors.js',
  source: '@pump-fun/pump-swap-sdk',
  sourceVersion: require(installDir + '/node_modules/@pump-fun/pump-swap-sdk/package.json').version,
  pumpSdkVersion: require(installDir + '/node_modules/@pump-fun/pump-sdk/package.json').version,
  note:
    'Generated by executing the official SDK. Fields named internal* are the '
    + "reference's pre-fee swap amounts; uiQuote and base are what a trader "
    + 'pays or receives. `observation` vectors are not parity targets: they '
    + 'record what the reference does in cases this project refuses to price.',
  vectorCount: vectors.length,
  vectors,
};

fs.writeFileSync(outputFile, JSON.stringify(document, null, 1) + '\n');
console.log('wrote ' + vectors.length + ' vectors to ' + outputFile);
