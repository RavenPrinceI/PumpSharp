// Account encoding, decoding and address derivation from the official SDK,
// with no RPC.
// Usage: node generate-curve-account-vectors.js SDK_INSTALL NEW_OUTPUT
const fs=require('fs'),path=require('path'),root=path.resolve(process.argv[2]);
const from=name=>require(path.join(root,'node_modules',name));
for(const [name,version] of [['@pump-fun/pump-sdk','4.3.1'],['@solana/spl-token','0.4.15']])
  if(JSON.parse(fs.readFileSync(path.join(root,'node_modules',name,'package.json'),'utf8')).version!==version)throw Error('Reference version differs');
const pump=from('@pump-fun/pump-sdk'),sdk=pump.PUMP_SDK,spl=from('@solana/spl-token'),BN=from('bn.js');
const {PublicKey,SYSVAR_CLOCK_PUBKEY,SYSVAR_EPOCH_SCHEDULE_PUBKEY}=from('@solana/web3.js');
const bn=n=>new BN(String(n)),key=n=>new PublicKey(new Uint8Array(32).fill(n));
// Uses the SDK coder's layout with a buffer large enough for Global. The
// public Anchor encode wrapper has a 1,000-byte scratch buffer, and Global's
// body is currently 1,080 bytes. The layout and discriminator are still the
// SDK's.
const encode=(program,name,value)=>{
  const entry=program.coder.accounts.accountLayouts.get(name),bytes=Buffer.alloc(10240);
  const size=entry.layout.encode(value,bytes);
  return Buffer.concat([Buffer.from(entry.discriminator),bytes.subarray(0,size)]);
};
const defaults=(type,index=1)=>type==='bool'?index%2===0:type==='pubkey'?key(index):type==='u8'?index:
  typeof type==='string'?bn(1000+index):Array.from({length:type.array[1]},(_,i)=>defaults(type.array[0],index+i));
const globals=[];
for(let index=0;index<8;index++){
  const fields=sdk.offlinePumpProgram.idl.types.find(t=>t.name==='global').type.fields;
  const value=Object.fromEntries(fields.map((f,i)=>[f.name,defaults(f.type,i+1)]));
  Object.assign(value,{feeBasisPoints:bn(20+index),creatorFeeBasisPoints:bn(10+index),buybackBasisPoints:bn(5000+index),
    creatorFeeConfigurable:!!(index&1),mayhemModeEnabled:!!(index&2),maxConfigurableCreatorFeeBps:bn(500+index),poolMigrationFee:bn(1000000+index)});
  const data=encode(sdk.offlinePumpProgram,'global',value),decoded=sdk.decodeGlobal({data});
  globals.push({data:data.toString('base64'),protocol:decoded.feeBasisPoints.toString(),creator:decoded.creatorFeeBasisPoints.toString(),
    buyback:decoded.buybackBasisPoints.toString(),configurable:decoded.creatorFeeConfigurable,mayhem:decoded.mayhemModeEnabled,
    maxCreator:decoded.maxConfigurableCreatorFeeBps.toString(),migration:decoded.poolMigrationFee.toString()});
}
const mint=key(1),creator=key(2),program=sdk.offlinePumpProgram.programId,feeProgram=sdk.offlinePumpFeeProgram.programId;
const pda=(seeds,owner)=>PublicKey.findProgramAddressSync(seeds,owner);
const [curveAddress]=pda([Buffer.from('bonding-curve'),mint.toBuffer()],program);
const [globalAddress]=pda([Buffer.from('global')],program);
const [feeAddress,feeBump]=pda([Buffer.from('fee_config'),program.toBuffer()],feeProgram);
const fee=(protocol,creator)=>({lpFeeBps:bn(0),protocolFeeBps:bn(protocol),creatorFeeBps:bn(creator)});
const config={bump:feeBump,admin:key(3),flatFees:fee(11,12),feeTiers:[{marketCapLamportsThreshold:bn(0),fees:fee(20,10)}],
  stableFeeTiers:[{marketCapLamportsThreshold:bn(0),fees:fee(15,5)}],exoticFlatFees:fee(30,10)};
const feeData=Buffer.alloc(4097);encode(sdk.offlinePumpFeeProgram,'feeConfig',config).copy(feeData);
const decodedFees=sdk.decodeFeeConfig({data:feeData});
const mintData=()=>{const b=Buffer.alloc(82);spl.MintLayout.encode({mintAuthorityOption:0,mintAuthority:PublicKey.default,supply:1000000000000000n,
  decimals:6,isInitialized:true,freezeAuthorityOption:0,freezeAuthority:PublicKey.default},b);return b;};
const account=(token,amount)=>{const b=Buffer.alloc(165);spl.AccountLayout.encode({mint:token,owner:curveAddress,amount:BigInt(amount),delegateOption:0,
  delegate:PublicKey.default,state:1,isNativeOption:0,isNative:0n,delegatedAmount:0n,closeAuthorityOption:0,closeAuthority:PublicKey.default},b);return b;};
const clock=Buffer.alloc(40);clock.writeBigUInt64LE(200n);clock.writeBigUInt64LE(1n,24);clock.writeBigInt64LE(1793448000n,32);
const schedule=Buffer.alloc(33);schedule.writeBigUInt64LE(1000n);schedule.writeBigUInt64LE(1000n,8);
const row=(address,owner,data,lamports=10000000000)=>({Address:address.toBase58(),Owner:owner.toBase58(),DataBase64:data.toString('base64'),Lamports:lamports});
const scenarios=[];
for(const kind of ['native','classic','taxed']){
  const quote=kind==='native'?spl.NATIVE_MINT:kind==='classic'?new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v'):key(4);
  const quoteProgram=kind==='taxed'?spl.TOKEN_2022_PROGRAM_ID:spl.TOKEN_PROGRAM_ID;
  const curve={virtualTokenReserves:bn('1000000000000'),virtualQuoteReserves:bn('30000000000'),realTokenReserves:bn('800000000000'),
    realQuoteReserves:bn('3000000000'),tokenTotalSupply:bn('1000000000000000'),complete:false,creator,isMayhemMode:false,isCashbackCoin:false,
    quoteMint:kind==='native'?PublicKey.default:quote,creatorFeeBps:bn(25),canEditCreatorFee:false,isHolderReward:false,
    creatorFee:bn(1234),protocolFees:bn(4321),depth:0,initialVirtualQuoteReserves:bn('30000000000'),postCompleteBaseOut:bn(0),postCompleteQuoteIn:bn(0)};
  let quoteMint=mintData(),quoteAccount=account(quote,'3000005555');
  if(kind==='taxed'){
    const tf=Buffer.alloc(108);spl.TransferFeeConfigLayout.encode({transferFeeConfigAuthority:PublicKey.default,withdrawWithheldAuthority:PublicKey.default,
      withheldAmount:0n,olderTransferFee:{epoch:0n,maximumFee:1000000n,transferFeeBasisPoints:25},
      newerTransferFee:{epoch:1n,maximumFee:1000000n,transferFeeBasisPoints:500}},tf);
    const header=Buffer.alloc(4);header.writeUInt16LE(spl.ExtensionType.TransferFeeConfig);header.writeUInt16LE(108,2);
    quoteMint=Buffer.concat([quoteMint,Buffer.alloc(83),Buffer.from([spl.AccountType.Mint]),header,tf]);
    const withheld=Buffer.alloc(12);withheld.writeUInt16LE(spl.ExtensionType.TransferFeeAmount);withheld.writeUInt16LE(8,2);
    quoteAccount=Buffer.concat([quoteAccount,Buffer.from([spl.AccountType.Account]),withheld]);
  }
  const globalData=Buffer.from(globals[1].data,'base64');
  const rows=[row(curveAddress,program,encode(sdk.offlinePumpProgram,'bondingCurve',curve)),row(globalAddress,program,globalData),row(feeAddress,feeProgram,feeData),
    row(mint,spl.TOKEN_PROGRAM_ID,mintData()),row(quote,quoteProgram,quoteMint),
    row(spl.getAssociatedTokenAddressSync(mint,curveAddress,true),spl.TOKEN_PROGRAM_ID,account(mint,'900000000000')),
    row(SYSVAR_CLOCK_PUBKEY,new PublicKey('Sysvar1111111111111111111111111111111111111'),clock),
    row(SYSVAR_EPOCH_SCHEDULE_PUBKEY,new PublicKey('Sysvar1111111111111111111111111111111111111'),schedule)];
  if(kind!=='native')rows.push(row(spl.getAssociatedTokenAddressSync(quote,curveAddress,true,quoteProgram),quoteProgram,quoteAccount));
  const global=sdk.decodeGlobal({data:globalData}),decodedCurve=sdk.decodeBondingCurve({data:Buffer.from(rows[0].DataBase64,'base64')});
  scenarios.push({kind,mint:mint.toBase58(),ContextSlot:200,Accounts:rows,fee: pump.getFee({global,feeConfig:decodedFees,mintSupply:bn('1000000000000000'),
    bondingCurve:decodedCurve,amount:bn(1000000),isNewBondingCurve:false}).toString()});
}
fs.writeFileSync(process.argv[3],JSON.stringify({sdk:'@pump-fun/pump-sdk',version:'4.3.1',globals,scenarios},null,2)+'\n',{flag:'wx'});
console.log(`${globals.length} official Global encodings and ${scenarios.length} coherent synthetic curve account sets.`);
