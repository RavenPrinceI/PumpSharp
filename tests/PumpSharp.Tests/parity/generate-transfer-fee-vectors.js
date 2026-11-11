// Usage: node generate-transfer-fee-vectors.js SDK_INSTALL NEW_OUTPUT
// Runs the SDKs' exported functions and SPL layouts; no local fee formula.
const fs = require('fs');
const path = require('path');
const root = path.resolve(process.argv[2]);
const from = name => require(path.join(root, 'node_modules', name));
const versions = { '@pump-fun/pump-sdk': '4.3.1', '@pump-fun/pump-swap-sdk': '2.2.0', '@solana/spl-token': '0.4.15',
  '@solana/spl-token-metadata': '0.1.6' };
for (const [name, version] of Object.entries(versions)) {
  if (JSON.parse(fs.readFileSync(path.join(root, 'node_modules', name, 'package.json'), 'utf8')).version !== version)
    throw new Error('Reference SDK version differs');
}
const pump = from('@pump-fun/pump-sdk'), swap = from('@pump-fun/pump-swap-sdk');
const spl = from('@solana/spl-token'), BN = from('bn.js');
const { PublicKey } = from('@solana/web3.js');
const bn = n => new BN(String(n));
const key = n => new PublicKey(new Uint8Array(32).fill(n));
const max = (1n << 64n) - 1n;
const cases = [];
for (const bps of [0, 1, 25, 100, 2500, 9999, 10000]) {
  for (const cap of [0n, 1n, 2n, 99n, 10000n, 1000000000n, max]) {
    for (const amount of [0n, 1n, 2n, 9n, 10n, 99n, 100n, 101n, 9999n, 10000n, 10001n,
      (1n << 53n) + 1n, (1n << 63n) - 1n, 1n << 63n, max - 1n, max]) {
      const fee = { basisPoints: bps, maximumFee: bn(cap) };
      const a = bn(amount);
      const withheld = pump.transferFeeWithheld(fee, a), net = pump.transferFeeNetOf(fee, a), gross = pump.transferFeeGrossFor(fee, a);
      if (!withheld.eq(swap.transferFeeOn(fee, a)) || !net.eq(swap.netOfTransferFee(fee, a)) || !gross.eq(swap.grossForTransferFee(fee, a)))
        throw new Error('Pump SDKs disagree');
      const splFee = spl.calculateFee({ epoch: 0n, maximumFee: cap, transferFeeBasisPoints: bps }, amount);
      if (withheld.toString() !== splFee.toString()) throw new Error('SPL disagrees');
      cases.push({ bps, cap: String(cap), amount: String(amount), withheld: withheld.toString(), net: net.toString(),
        gross: gross.toString(), grossFitsU64: BigInt(gross.toString()) <= max });
    }
  }
}
const tlv = (type, data) => {
  const header = Buffer.alloc(4); header.writeUInt16LE(type); header.writeUInt16LE(data.length, 2);
  return Buffer.concat([header, data]);
};
const mintBase = Buffer.alloc(spl.MINT_SIZE);
spl.MintLayout.encode({ mintAuthorityOption: 0, mintAuthority: PublicKey.default, supply: max, decimals: 6,
  isInitialized: true, freezeAuthorityOption: 0, freezeAuthority: PublicKey.default }, mintBase);
const mint = parts => Buffer.concat([mintBase, Buffer.alloc(83), Buffer.from([spl.AccountType.Mint]), ...parts]);
const accountBase = Buffer.alloc(spl.ACCOUNT_SIZE);
spl.AccountLayout.encode({ mint: key(1), owner: key(2), amount: max, delegateOption: 0, delegate: PublicKey.default,
  state: 1, isNativeOption: 0, isNative: 0n, delegatedAmount: 0n, closeAuthorityOption: 0, closeAuthority: PublicKey.default }, accountBase);
const accountFee = Buffer.alloc(spl.TRANSFER_FEE_AMOUNT_SIZE);
spl.TransferFeeAmountLayout.encode({ withheldAmount: 777n }, accountFee);
const tokenAccount = Buffer.concat([accountBase, Buffer.from([spl.AccountType.Account]),
  tlv(spl.ExtensionType.ImmutableOwner, Buffer.alloc(0)), tlv(spl.ExtensionType.TransferFeeAmount, accountFee)]);
const metadata = from('@solana/spl-token-metadata');
const metadataMints = ['', 'Research token', '研究 🪙'].map(name => mint([
  tlv(spl.ExtensionType.MintCloseAuthority, Buffer.alloc(32)),
  tlv(spl.ExtensionType.MetadataPointer, Buffer.alloc(64)),
  tlv(spl.ExtensionType.TokenMetadata, Buffer.from(metadata.pack({ mint: key(1), name, symbol: 'LAB',
    uri: 'https://example.invalid/metadata', additionalMetadata: [['purpose', 'parity'], ['units', 'raw']] })))
]).toString('base64'));
const schedules = [];
for (const transition of [0n, 2n, 1000n, (1n << 53n) + 1n, max]) {
  const config = { transferFeeConfigAuthority: key(3), withdrawWithheldAuthority: key(4), withheldAmount: 555n,
    olderTransferFee: { epoch: 0n, maximumFee: 100000n, transferFeeBasisPoints: 25 },
    newerTransferFee: { epoch: transition, maximumFee: max, transferFeeBasisPoints: 9999 } };
  const bytes = Buffer.alloc(spl.TRANSFER_FEE_CONFIG_SIZE); spl.TransferFeeConfigLayout.encode(config, bytes);
  const data = mint([tlv(spl.ExtensionType.TransferFeeConfig, bytes), tlv(spl.ExtensionType.MetadataPointer, Buffer.alloc(64))]);
  const info = { owner: spl.TOKEN_2022_PROGRAM_ID, data, lamports: 1, executable: false };
  for (const epoch of [...new Set([0n, transition > 0n ? transition - 1n : 0n, transition, transition < max ? transition + 1n : max, max])]) {
    const fee = pump.quoteTransferFeeOf(key(1), info, epoch);
    const configRead = swap.parseTransferFeeConfig(key(1), info), other = swap.transferFeeAt(configRead, epoch);
    if (fee.basisPoints !== other.basisPoints || !fee.maximumFee.eq(other.maximumFee)) throw new Error('Epoch selection disagrees');
    schedules.push({ mint: data.toString('base64'), epoch: String(epoch), transition: String(transition),
      bps: fee.basisPoints, cap: fee.maximumFee.toString(), supply: spl.unpackMint(key(1), info, spl.TOKEN_2022_PROGRAM_ID).supply.toString() });
  }
}
const unpacked = spl.unpackAccount(key(5), { owner: spl.TOKEN_2022_PROGRAM_ID, data: tokenAccount, lamports: 1, executable: false }, spl.TOKEN_2022_PROGRAM_ID);
const output = { versions, token2022Program: spl.TOKEN_2022_PROGRAM_ID.toString(), cases, schedules, metadataMints,
  account: { data: tokenAccount.toString('base64'), mint: unpacked.mint.toString(), authority: unpacked.owner.toString(),
    amount: unpacked.amount.toString(), withheld: spl.getTransferFeeAmount(unpacked).withheldAmount.toString() } };
fs.writeFileSync(process.argv[3], JSON.stringify(output, null, 2) + '\n', { flag: 'wx' });
console.log(`Generated ${cases.length} arithmetic cases and ${schedules.length} epoch/mint cases.`);
