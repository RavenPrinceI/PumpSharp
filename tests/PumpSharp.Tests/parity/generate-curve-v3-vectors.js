// Runs the pinned SDK's v3 quote functions, including the synthetic
// migration.
// Usage: node generate-curve-v3-vectors.js SDK_INSTALL NEW_OUTPUT
const fs=require('fs'),path=require('path'),root=path.resolve(process.argv[2]);
const from=name=>require(path.join(root,'node_modules',name));
if(from('@pump-fun/pump-sdk/package.json').version!=='4.3.1')throw Error('Reference version differs');
const pump=from('@pump-fun/pump-sdk'),BN=from('bn.js'),{PublicKey}=from('@solana/web3.js');
const bn=n=>new BN(String(n)),key=n=>new PublicKey(new Uint8Array(32).fill(n));
const max=(1n<<64n)-1n,cases=[];
for(const native of [false,true])for(const mayhem of [false,true])for(const creator of [false,true])
for(const buyback of [0,5000,10000])for(const bps of [0,500,10000])for(const cap of [0n,1000n,max]){
 const global={feeBasisPoints:bn(20),creatorFeeBasisPoints:bn(30),creatorFeeConfigurable:false,buybackBasisPoints:bn(buyback),poolMigrationFee:bn(500)};
 const curve={virtualTokenReserves:bn(1000000),virtualQuoteReserves:bn(30000),realTokenReserves:bn(10000),
   realQuoteReserves:bn(1000),complete:false,isMayhemMode:mayhem,quoteMint:native?PublicKey.default:key(3),
   creator:creator?key(2):PublicKey.default,creatorFeeBps:bn(0)};
 const args={global,feeConfig:null,mintSupply:bn('1000000000000000'),bondingCurve:curve,curveBaseTokenBalance:bn(30000),
   quoteTransferFee:{basisPoints:bps,maximumFee:bn(cap)}};
 for(const [kind,amounts,fn] of [['base',['0','1','9999','10000','10001','29999','30000'],pump.getBuyV3QuoteAmountFromTokenAmount],
   ['budget',['0','1','2','1000','1000000','1000000000',String(max)],pump.getBuyV3TokenAmountFromQuoteAmount]])
  for(const amount of amounts){
   let output=null,error=false;
   try{output=fn({...args,amount:bn(amount)}).toString();}catch{error=true;}
   cases.push({native,mayhem,creator,buyback,bps,cap:String(cap),kind,amount,output,error,
     fits:!error&&BigInt(output)>0n&&BigInt(output)<=max});
  }
}
fs.writeFileSync(process.argv[3],JSON.stringify({sdk:'@pump-fun/pump-sdk',version:'4.3.1',
  virtualBase:'1000000',virtualQuote:'30000',remaining:'10000',realQuote:'1000',baseVault:'30000',migrationFee:'500',cases},null,2)+'\n',{flag:'wx'});
console.log(`${cases.length} executed curve-v3 exact-base/budget references.`);
