using Jayrock.Json;
using OsEngine.Entity;
using OsEngine.Market.Servers.BitMax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static OsEngine.Market.Servers.Deribit.Entity.ResponseChannelUserChanges;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexSoketMessageBase
    {

        public string guid;

        public JsonObject data;


    }


}
    //[
// CHANNEL_ID // = 0
// <TYPE> //'on', 'ou', 'oc', etc. 
//  [
//   ... //Content goes here
//  ]
//]

//Orders: 'os' = order snapshot; 'on' = order new; 'ou' = order update; 'oc' = order cancel//Snapshot and updates on the trade orders in your account.
//Positions: 'ps' = position snapshot; 'pn' = position new; 'pu' = position update; 'pc' = position close//Snapshot and updates on the positions in your account
//Trades: 	'te' = trade executed; 'tu' = trade execution update//Provides a feed of your trades
//Funding Offers:'
//Funding Credits: 
//Funding Loans: 
//Wallets: ws' = wallet snapshot; 'wu' = wallet update //Provides a snapshot and updates on wallet balance, unsettled interest, and available balance.   
//Balance Info:'bu' = balance update// Provides updates on the total and net assets in your account. 
//Margin Info: 
//Funding Info: 
//Funding Trades: 
//Notifications: 



//List of WS Inputs
// New Order: "on" (order new) ,"os" order snapshot ,"on-req" new order request
//Update Order:  "ou" (order update) Update('on', 'ou', 'oc') "os" snapshot
//Order Multi-OP: 
//Cancel Order:"oc" order cancel / fully executed(no longer active), "oc-req" order cancel request
//Cancel Order Multi: 
//New Offer: 
//Cancel Offer: 
//Calc: 
//}
