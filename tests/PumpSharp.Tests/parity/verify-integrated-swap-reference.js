// Compares the standalone PumpSwap SDK with the swap module inside the Pump
// SDK. No formulas, RPC, wallet or package installation.
// Usage: node verify-integrated-swap-reference.js SDK_INSTALL NEW_OUTPUT
const fs = require('fs'), path = require('path'), root = path.resolve(process.argv[2]);
const from = name => require(require.resolve(name, { paths: [root] }));
const swap = from('@pump-fun/pump-swap-sdk'), integrated = from('@pump-fun/pump-sdk/swap');
const BN = from('bn.js'), { PublicKey } = from('@solana/web3.js');
const bn = value => new BN(String(value)), key = value => new PublicKey(new Uint8Array(32).fill(value));
const versionOf = name => JSON.parse(fs.readFileSync(path.join(root, 'node_modules', name, 'package.json'), 'utf8')).version;
const versions = { pump: versionOf('@pump-fun/pump-sdk'), standalone: versionOf('@pump-fun/pump-swap-sdk') };
if (versions.pump !== '4.3.1' || versions.standalone !== '2.2.0') throw Error('Reference version differs');
const cases = JSON.parse(fs.readFileSync(path.join(__dirname, 'pool-v2-debit-vectors.json'), 'utf8')).cases;
const source = JSON.parse(fs.readFileSync(path.join(__dirname, 'pump-swap-sdk-2.2.0-vectors.json'), 'utf8')).vectors;
let compared = 0;
const mismatches = [];
function compare(label, method, args) {
  const invoke = sdk => {
    try { return { value: sdk[method](args) }; }
    catch (error) { return { error: error.constructor.name + ': ' + error.message }; }
  };
  compared++;
  if (JSON.stringify(invoke(swap)) !== JSON.stringify(invoke(integrated))) mismatches.push(label);
}
for (const [index, row] of cases.entries()) {
  const net = bn(row.amount), transfer = { basisPoints: row.bps, maximumFee: bn(row.cap) };
  const globalConfig = { lpFeeBasisPoints: bn(30), protocolFeeBasisPoints: bn(20), coinCreatorFeeBasisPoints: bn(10),
    buybackBasisPoints: bn(row.buybackBps), creatorFeeConfigurable: false };
  const args = { globalConfig, feeConfig: null, baseMintAccount: { supply: 1000000000000000n }, baseMint: key(1),
    coinCreator: key(2), creator: key(4), quoteMint: key(3), isMayhemMode: row.mayhem, creatorFeeBps: bn(0),
    baseReserve: bn('1000000000000'), quoteReserve: bn('30000000000'), slippage: 0 };
  // Components come from SDK-generated outputs, not a new fee formula.
  const fees = swap.computeFeesBps(args);
  const component = rate => bn(from('@solana/spl-token').calculateFee(
    { epoch: 0n, maximumFee: (1n << 64n) - 1n, transferFeeBasisPoints: Number(rate.toString()) }, BigInt(row.amount)));
  compare(`v2 debit ${index}`, 'buyDebit', { quoteAmountIn: net, lpFee: component(fees.lpFeeBps),
    protocolFee: component(fees.protocolFeeBps), coinCreatorFee: component(fees.creatorFeeBps),
    quoteTransferFee: transfer, globalConfig, isMayhemMode: row.mayhem, tradeV2: true });
  compare(`v2 sell ${index}`, 'sellBaseInput', { ...args, base: net, quoteTransferFee: transfer });
}
const methods = { buy_base_input: ['buyBaseInput', 'base'], sell_base_input: ['sellBaseInput', 'base'],
  buy_quote_input: ['buyQuoteInput', 'quote'], sell_quote_input: ['sellQuoteInput', 'quote'] };
for (const [index, row] of source.entries()) {
  if (!methods[row.name] || row.baseReserve === undefined) continue;
  const [method, amountName] = methods[row.name];
  const args = { globalConfig: { lpFeeBasisPoints: bn(row.lpFeeBps), protocolFeeBasisPoints: bn(row.protocolFeeBps),
    coinCreatorFeeBasisPoints: bn(row.creatorFeeBps), creatorFeeConfigurable: false, buybackBasisPoints: bn(0) },
    feeConfig: null, baseMintAccount: { supply: 1000000000000000n }, baseMint: key(1), creator: key(4),
    coinCreator: row.hasCoinCreator ? key(2) : PublicKey.default, quoteMint: key(3), creatorFeeBps: bn(0),
    isMayhemMode: false, baseReserve: bn(row.baseReserve), quoteReserve: bn(row.quoteReserve), slippage: 0,
    [amountName]: bn(row.input) };
  compare(`core ${index} ${method}`, method, args);
}
fs.writeFileSync(process.argv[3], JSON.stringify({ versions, compared, mismatches }, null, 2) + '\n', { flag: 'wx' });
console.log(`${compared} standalone/integrated official outputs; ${mismatches.length} mismatches.`);
if (mismatches.length) process.exitCode = 2;
