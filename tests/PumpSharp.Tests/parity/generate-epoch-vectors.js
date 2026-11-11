// Usage: node generate-epoch-vectors.js SDK_INSTALL NEW_OUTPUT
// Epoch and index values come from running @solana/web3.js, not from a
// local formula.
const fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]);
const pkg = path.join(root, 'node_modules/@solana/web3.js');
const version = JSON.parse(fs.readFileSync(path.join(pkg, 'package.json'), 'utf8')).version;
if (version !== '1.99.0') throw new Error('Reference version differs');
const sdk = require(pkg);
const schedules = [];
for (const [slots, warmup, firstEpoch, firstSlot] of [
  [32, false, 0, 0], [33, false, 0, 0], [1000, false, 0, 0], [432000, false, 0, 0],
  [32, true, 0, 0], [33, true, 1, 32], [1000, true, 5, 992], [432000, true, 14, 524256]
]) {
  const schedule = new sdk.EpochSchedule(slots, slots, warmup, firstEpoch, firstSlot);
  const bytes = Buffer.alloc(33);
  bytes.writeBigUInt64LE(BigInt(slots)); bytes.writeBigUInt64LE(BigInt(slots), 8);
  bytes[16] = warmup ? 1 : 0; bytes.writeBigUInt64LE(BigInt(firstEpoch), 17); bytes.writeBigUInt64LE(BigInt(firstSlot), 25);
  const inputs = new Set([0, 1, 31, 32, 33, 63, 64, 95, 96, 200, firstSlot, firstSlot + 1,
    firstSlot + slots - 1, firstSlot + slots, 463621173, 2 ** 40, 2 ** 52 - 1]);
  if (firstSlot > 0) inputs.add(firstSlot - 1);
  if (warmup) for (let epoch = 0; epoch <= firstEpoch; epoch++) {
    const first = schedule.getFirstSlotInEpoch(epoch);
    if (first > 0) inputs.add(first - 1);
    inputs.add(first); inputs.add(first + 1);
  }
  const cases = [...inputs].sort((a,b) => a-b).map(slot => {
    const [epoch, index] = schedule.getEpochAndSlotIndex(slot);
    if (![slot, epoch, index].every(Number.isSafeInteger)) throw new Error('Unsafe JS oracle input or output');
    return { slot: String(slot), epoch: String(epoch), index: String(index) };
  });
  schedules.push({ slots: String(slots), warmup, firstEpoch: String(firstEpoch), firstSlot: String(firstSlot),
    bytes: bytes.toString('base64'), cases });
}
fs.writeFileSync(process.argv[3], JSON.stringify({ version, clockAddress: sdk.SYSVAR_CLOCK_PUBKEY.toString(),
  epochScheduleAddress: sdk.SYSVAR_EPOCH_SCHEDULE_PUBKEY.toString(), schedules }, null, 2) + '\n', { flag: 'wx' });
console.log(`${schedules.length} schedules, ${schedules.reduce((n,s) => n+s.cases.length, 0)} official epoch/index cases.`);
