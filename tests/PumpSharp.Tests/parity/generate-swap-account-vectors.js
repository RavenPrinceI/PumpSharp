// Runs the pinned official SDK against saved account bytes. No RPC calls.
// Usage: node generate-swap-account-vectors.js INSTALL_DIR CAPTURE_JSON OUTPUT_JSON
const fs = require('fs');
const crypto = require('crypto');
const installDir = process.argv[2];
const capture = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const sdk = require(installDir + '/node_modules/@pump-fun/pump-swap-sdk');
const BN = require(installDir + '/node_modules/bn.js');
const spl = require(installDir + '/node_modules/@solana/spl-token');
if (require(installDir + '/node_modules/@pump-fun/pump-swap-sdk/package.json').version !== '2.2.0')
  throw new Error('This generator requires official SDK 2.2.0.');
const data = Buffer.from(capture.Accounts[2].DataBase64, 'base64');
const rates = f => ({ lp: f.lpFeeBps.toString(), protocol: f.protocolFeeBps.toString(), creator: f.creatorFeeBps.toString() });
const tiers = rows => rows.map(t => ({ threshold: t.marketCapLamportsThreshold.toString(), ...rates(t.fees) }));
const vectors = [2512, 4073, 4097].map(length => {
  const raw = data.subarray(0, length);
  const decoded = sdk.PUMP_AMM_SDK.decodeFeeConfig({ data: raw });
  return { allocation: length, inputBase64: raw.toString('base64'), bump: decoded.bump,
    admin: decoded.admin.toBase58(), flat: rates(decoded.flatFees),
    tiers: tiers(decoded.feeTiers), stableTiers: tiers(decoded.stableFeeTiers), exotic: rates(decoded.exoticFlatFees) };
});
const pool = sdk.PUMP_AMM_SDK.decodePool({ data: Buffer.from(capture.Accounts[0].DataBase64, 'base64') });
const globalConfig = sdk.PUMP_AMM_SDK.decodeGlobalConfig({ data: Buffer.from(capture.Accounts[1].DataBase64, 'base64') });
const feeConfig = sdk.PUMP_AMM_SDK.decodeFeeConfig({ data });
const baseMintAccount = spl.MintLayout.decode(Buffer.from(capture.Accounts[3].DataBase64, 'base64'));
const baseReserve = new BN(spl.AccountLayout.decode(Buffer.from(capture.Accounts[5].DataBase64, 'base64')).amount.toString());
const quoteReserve = new BN(spl.AccountLayout.decode(Buffer.from(capture.Accounts[6].DataBase64, 'base64')).amount.toString());
const args = { baseReserve, quoteReserve, virtualQuoteReserves: pool.virtualQuoteReserves, globalConfig, feeConfig,
  baseMintAccount, baseMint: pool.baseMint, quoteMint: pool.quoteMint, creator: pool.creator, coinCreator: pool.coinCreator,
  creatorFeeBps: pool.creatorFeeBps, isMayhemMode: pool.isMayhemMode, slippage: 0 };
const base = new BN(1000000);
const buy = sdk.buyBaseInput({ ...args, base });
const sell = sdk.sellBaseInput({ ...args, base, feeBucketsTotal: pool.protocolFees.add(pool.creatorFees) });
const poolQuote = { inputBase: base.toString(), baseReserve: baseReserve.toString(), rawQuoteReserve: quoteReserve.toString(),
  virtualQuoteReserve: pool.virtualQuoteReserves.toString(), effectiveQuoteReserve: quoteReserve.add(pool.virtualQuoteReserves).toString(),
  protocolFees: pool.protocolFees.toString(), creatorFees: pool.creatorFees.toString(),
  buyNetQuote: buy.internalQuoteAmount.toString(), buyGrossQuote: buy.uiQuote.toString(),
  sellGrossQuote: sell.internalQuoteAmountOut.toString(), sellNetQuote: sell.uiQuote.toString() };
const output = { sdk: '@pump-fun/pump-swap-sdk', version: '2.2.0',
  sourceFixtureDigest: capture.Digest,
  sourceSlot: capture.ContextSlot,
  sourceAccount: capture.Accounts[2].Address,
  rawAccountSha256: crypto.createHash('sha256').update(data).digest('hex'), vectors, poolQuote };
fs.writeFileSync(process.argv[4], JSON.stringify(output, null, 2) + '\n', { flag: 'wx' });
console.log('Generated ' + vectors.length + ' official account-decode vectors from recorded bytes.');
