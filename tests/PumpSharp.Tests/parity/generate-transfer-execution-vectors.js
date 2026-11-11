// Runs the official SDKs for net-input orders with transfer fees.
// Usage: node generate-transfer-execution-vectors.js SDK_INSTALL NEW_OUTPUT
const fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]);
const from = name => require(path.join(root, 'node_modules', name));
const versions = { '@pump-fun/pump-sdk': '4.3.1', '@pump-fun/pump-swap-sdk': '2.2.0', '@solana/spl-token': '0.4.15' };
for (const [name, version] of Object.entries(versions))
  if (JSON.parse(fs.readFileSync(path.join(root, 'node_modules', name, 'package.json'), 'utf8')).version !== version)
    throw new Error('Reference SDK version differs');
const pump = from('@pump-fun/pump-sdk'), swap = from('@pump-fun/pump-swap-sdk');
const spl = from('@solana/spl-token'), BN = from('bn.js'), { PublicKey } = from('@solana/web3.js');
const bn = n => new BN(String(n)), key = n => new PublicKey(new Uint8Array(32).fill(n));
const max = (1n << 64n) - 1n;
const base = '1000000000000', quote = '30000000000', supply = '1000000000000000';
const globals = { feeBasisPoints: bn(20), creatorFeeBasisPoints: bn(10), creatorFeeConfigurable: false,
  tokenTotalSupply: bn(supply), lpFeeBasisPoints: bn(30), protocolFeeBasisPoints: bn(20), coinCreatorFeeBasisPoints: bn(10),
  buybackBasisPoints: bn(0), initialVirtualTokenReserves: bn(base), initialVirtualSolReserves: bn(quote),
  initialRealTokenReserves: bn(base), initialVirtualQuoteReserves: bn(quote), whitelistedQuoteMints: [], maxCurveDepth: 5 };
const zero = { ...globals, feeBasisPoints: bn(0), creatorFeeBasisPoints: bn(0), lpFeeBasisPoints: bn(0),
  protocolFeeBasisPoints: bn(0), coinCreatorFeeBasisPoints: bn(0) };
const curve = { virtualTokenReserves: bn(base), virtualQuoteReserves: bn(quote), realTokenReserves: bn(base),
  realQuoteReserves: bn(quote), tokenTotalSupply: bn(supply), complete: false, creator: key(2),
  quoteMint: key(3), creatorFeeBps: bn(0), isMayhemMode: false };
const curveArgs = { global: globals, feeConfig: null, mintSupply: bn(supply), bondingCurve: curve };
const poolArgs = { globalConfig: globals, feeConfig: null, baseMintAccount: { supply: BigInt(supply) },
  baseMint: key(1), coinCreator: key(2), creator: key(4), quoteMint: key(3), isMayhemMode: false,
  creatorFeeBps: bn(0), baseReserve: bn(base), quoteReserve: bn(quote), slippage: 0 };
const component = (amount, bps) => bn(spl.calculateFee({ epoch: 0n, maximumFee: max, transferFeeBasisPoints: bps }, BigInt(amount.toString())));
const cases = [];
for (const venue of ['curve','pool']) for (const bps of [0,1,250,10000]) for (const cap of [0n,1n,1000n,max])
  for (const amount of ['2','1000','1000000','1000000000']) {
    const fee = { basisPoints: bps, maximumFee: bn(cap) }, net = bn(amount);
    let buyBase, buyCash, buyFee, sellCash, sellGross, sellRaw;
    if (venue === 'curve') {
      buyBase = pump.getBuyTokenAmountFromSolAmount({ ...curveArgs, global: zero, amount: net.addn(1) });
      buyFee = pump.getFee({ ...curveArgs, amount: net, isNewBondingCurve: false });
      buyCash = pump.transferFeeGrossFor(fee, net).add(buyFee);
      sellGross = pump.getSellSolAmountFromTokenAmount({ ...curveArgs, global: zero, amount: net });
      sellRaw = pump.getSellSolAmountFromTokenAmount({ ...curveArgs, amount: net });
      sellCash = pump.getSellSolAmountFromTokenAmount({ ...curveArgs, amount: net, quoteTransferFee: fee });
    } else {
      // A zero fee isolates the SDK's core quantity and its one-unit input
      // shave. Orders give the net amount, not a gross budget.
      buyBase = swap.buyQuoteInput({ ...poolArgs, globalConfig: zero, quote: net }).base;
      const lpFee = component(net,30), protocolFee = component(net,20), coinCreatorFee = component(net,10);
      buyFee = lpFee.add(protocolFee).add(coinCreatorFee);
      buyCash = swap.buyDebit({ quoteAmountIn: net, lpFee, protocolFee, coinCreatorFee,
        quoteTransferFee: fee, globalConfig: globals, tradeV2: false });
      const raw = swap.sellBaseInput({ ...poolArgs, base: net });
      sellGross = raw.internalQuoteAmountOut; sellRaw = raw.uiQuote;
      sellCash = swap.sellBaseInput({ ...poolArgs, base: net, quoteTransferFee: fee }).uiQuote;
    }
    cases.push({ venue, bps, cap: String(cap), amount, buyBase: buyBase.toString(), buyCash: buyCash.toString(),
      buyFee: buyFee.toString(), buyTransfer: buyCash.sub(net).sub(buyFee).toString(),
      buyFits: BigInt(buyCash.toString()) <= max && !buyBase.isZero(), sellCash: sellCash.toString(),
      sellGross: sellGross.toString(), sellFee: sellGross.sub(sellRaw).toString(),
      sellTransfer: sellRaw.sub(sellCash).toString(), sellFits: sellCash.gtn(0) });
  }
fs.writeFileSync(process.argv[3], JSON.stringify({ versions, base, quote, cases }, null, 2)+'\n', { flag:'wx' });
console.log(`${cases.length} combined transfer-fee cases, both directions; legacy pool buy split and declared net sizing.`);
