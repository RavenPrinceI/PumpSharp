// Runs the pinned official PumpSwap and SPL code. No network or wallet
// access.
// Usage: node generate-pool-v2-debit-vectors.js SDK_INSTALL NEW_OUTPUT
const fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]);
const from = name => require(path.join(root, 'node_modules', name));
const versions = { '@pump-fun/pump-swap-sdk':'2.2.0', '@solana/spl-token':'0.4.15' };
for (const [name, version] of Object.entries(versions))
  if (JSON.parse(fs.readFileSync(path.join(root,'node_modules',name,'package.json'),'utf8')).version !== version)
    throw new Error('Reference version differs');
const swap = from('@pump-fun/pump-swap-sdk'), spl = from('@solana/spl-token');
const BN = from('bn.js'), { PublicKey } = from('@solana/web3.js');
const bn = n => new BN(String(n)), key = n => new PublicKey(new Uint8Array(32).fill(n));
const max = (1n << 64n)-1n;
const component = (amount, bps) => bn(spl.calculateFee(
  { epoch:0n, maximumFee:max, transferFeeBasisPoints:bps },BigInt(amount.toString())));
const cases = [];
for (const mayhem of [false,true]) for (const buybackBps of [0,1,5000,10000])
  for (const bps of [0,1,500,10000]) for (const cap of [0n,1n,1000n,max])
    for (const amount of ['2','1000','1000000','1000000000']) {
      const net=bn(amount), transfer={ basisPoints:bps, maximumFee:bn(cap) };
      const globalConfig={ lpFeeBasisPoints:bn(30),protocolFeeBasisPoints:bn(20),coinCreatorFeeBasisPoints:bn(10),
        buybackBasisPoints:bn(buybackBps),creatorFeeConfigurable:false };
      const args={ globalConfig,feeConfig:null,baseMintAccount:{ supply:1000000000000000n },
        baseMint:key(1),coinCreator:key(2),creator:key(4),quoteMint:key(3),isMayhemMode:mayhem,
        creatorFeeBps:bn(0),baseReserve:bn('1000000000000'),quoteReserve:bn('30000000000'),slippage:0 };
      const lp=component(net,30),protocol=component(net,20),creator=component(net,10);
      const debitArgs={ quoteAmountIn:net,lpFee:lp,protocolFee:protocol,coinCreatorFee:creator,
        quoteTransferFee:transfer,globalConfig,isMayhemMode:mayhem };
      const debit=swap.buyDebit({ ...debitArgs,tradeV2:true });
      const legacy=swap.buyDebit({ ...debitArgs,tradeV2:false });
      const zero={ ...globalConfig,lpFeeBasisPoints:bn(0),protocolFeeBasisPoints:bn(0),coinCreatorFeeBasisPoints:bn(0) };
      const base=swap.buyQuoteInput({ ...args,globalConfig:zero,quote:net }).base;
      const sell=swap.sellBaseInput({ ...args,base:net,quoteTransferFee:transfer });
      const raw=swap.sellBaseInput({ ...args,base:net });
      const buyback=mayhem ? bn(0) : protocol.muln(buybackBps).divn(10000);
      const sellBuyback=mayhem ? bn(0) : component(sell.internalQuoteAmountOut,20).muln(buybackBps).divn(10000);
      cases.push({ mayhem,buybackBps,bps,cap:String(cap),amount,buyCash:debit.toString(),legacyCash:legacy.toString(),
        buyBase:base.toString(),buyFee:lp.add(protocol).add(creator).toString(),buyback:buyback.toString(),
        buyFits:BigInt(debit.toString())<=max && !base.isZero(),sellCash:sell.uiQuote.toString(),
        sellGross:sell.internalQuoteAmountOut.toString(),sellFee:sell.internalQuoteAmountOut.sub(raw.uiQuote).toString(),
        sellBuyback:sellBuyback.toString(),sellFits:sell.uiQuote.gtn(0) });
    }
fs.writeFileSync(process.argv[3],JSON.stringify({ versions,cases },null,2)+'\n',{ flag:'wx' });
console.log(`${cases.length} official pool-v2 debit/sell cases, including legacy debit comparison.`);
