// Writes the coverage audit: the list of supported routes, bound to the
// exact SDK and IDL bytes they were checked against. Running the C# tests
// is a separate step.
// Usage: node write-quote-coverage-audit.js SDK_INSTALL REGISTRY_JSON NEW_OUTPUT
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const root = path.resolve(process.argv[2]), repo = path.resolve(__dirname, '../../..');
const from = name => require(require.resolve(name, { paths: [root] }));
const registry = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const lock = JSON.parse(fs.readFileSync(path.join(root, 'node_modules/.package-lock.json'), 'utf8'));
for (const row of registry) {
  const installed = lock.packages['node_modules/' + row.Name];
  if (installed.version !== row.Version || installed.integrity !== row.Integrity) throw Error('Reference provenance mismatch');
}
const pump = from('@pump-fun/pump-sdk');
const programs = { pump: pump.PUMP_SDK.offlinePumpProgram, pump_amm: pump.PUMP_SDK.offlinePumpAmmProgram,
  pump_fees: pump.PUMP_SDK.offlinePumpFeeProgram };
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const idls = Object.entries(programs).map(([name, program]) => {
  const bytes = fs.readFileSync(path.join(repo, 'src/PumpSharp/protocol', name + '.json'));
  if (JSON.stringify(JSON.parse(bytes)) !== JSON.stringify(program.rawIdl)) throw Error('Official IDL mismatch: ' + name);
  return { name, sha256: sha(bytes), officialSdkRawIdlIdentical: true };
});
const artifacts = [
  ['pump-sdk-4.3.1-vectors.json', 'PumpParityTests'],
  ['pump-swap-sdk-2.2.0-vectors.json', 'PumpSwapParityTests'],
  ['pump-swap-sdk-2.2.0-account-vectors.json', 'PumpSwapFeeAccountDecoderTests'],
  ['pump-swap-sdk-2.2.0-boost-vectors.json', 'PumpSwapBoostParityTests'],
  ['mayhem-sdk-vectors.json', 'MayhemParityTests'],
  ['token-transfer-fee-vectors.json', 'TokenTransferFeeParityTests'],
  ['transfer-execution-vectors.json', 'TransferFeeExecutionTests'],
  ['epoch-web3-vectors.json', 'ChainEpochParityTests'],
  ['pool-v2-debit-vectors.json', 'PoolV2ExecutionTests'],
  ['curve-account-vectors.json', 'CurveAccountSetTests'],
  ['curve-v3-vectors.json', 'CurveV3Tests'],
].map(([name, tests]) => {
  const bytes = fs.readFileSync(path.join(__dirname, name));
  const data = JSON.parse(bytes);
  return { name, sha256: sha(bytes), bytes: bytes.length, tests,
    arrays: Object.fromEntries(Object.entries(data).filter(([, value]) => Array.isArray(value)).map(([key, value]) => [key, value.length])) };
});
const supportedExecution = [
  { id: 'CurveV2.NetQuoteBuy', inputs: 'Net curve quote input; independent ceiling fees; legacy vault transfer gross-up.',
    guard: 'No clamp/partial fill, complete/stale/future/signed-ambiguous state; real base supply retained.' },
  { id: 'CurveV2.BaseSell', inputs: 'Exact base input; real quote settlement check; ceiling fees and net quote transfer.',
    guard: 'Completed holdings refuse until observed pool routing.' },
  { id: 'CurveV3.ExactBaseBuy', inputs: 'Exact base output; separate curve/synthetic legs, native migration fee, buyback subset and combined transfer gross-up.',
    guard: 'Cashback, partial mayhem output, empty/drained/unrepresentable synthetic pool refuse.' },
  { id: 'PoolV2.NetQuoteBuy', inputs: 'Declared net plus independent fees; official one-unit pricing shave; v2 vault/buyback transfer debit.',
    guard: 'Validated effective/real reserves, flags, rates, mint/epoch context and freshness.' },
  { id: 'PoolV2.BaseSell', inputs: 'Exact base input; effective pricing reserves, real liquidity check before fee deductions and net transfer.',
    guard: 'Same context, settlement and overflow guards as pool buy.' },
];
const report = { version: 1, reviewedOn: '2026-10-31', accounting: 4, pricing: 10,
  references: registry, idls, artifacts, supportedExecution,
  conventions: [
    'Curve core net input is compared to the zero-rate SDK buy wrapper at net+1; it is not advertised as a wallet budget.',
    'Generic unshaved exact-quote primitives and exact-payout fitting use their documented core/fitting contracts; wrapper shaves are explicit relationships.',
    'Budget quote arithmetic has reference coverage; budget execution orders and multi-hop are not implemented or claimed.',
    'Base transfer fees, unknown extensions/layouts, unsupported cashback/partial execution and signed-ambiguous curve state refuse.',
    'Native network cost, delay and adverse reserve shift are separate uncalibrated assumptions; parity does not certify execution accuracy.',
  ] };
fs.writeFileSync(process.argv[4], JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
console.log(`${artifacts.length} exact artifacts, ${idls.length} SDK-identical IDLs, ${supportedExecution.length} supported execution paths recorded.`);
