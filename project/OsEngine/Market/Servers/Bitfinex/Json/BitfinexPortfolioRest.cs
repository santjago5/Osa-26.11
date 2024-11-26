using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    //POST https://api.bitfinex.com/v2/auth/r/wallets
    public class BitfinexPortfolioRest
    {
        public string Type { get; set; }    //EXCHANGE, MARGIN, FUNDING //TYPE
        public string Currency { get; set; } //"UST", //CURRENCY
        public decimal Balance { get; set; } //19788.6529257, //BALANCE
        public decimal UnsettledInterest { get; set; } //	0, //UNSETTLED_INTEREST
        public decimal AvailableBalance { get; set; }    //19788.6529257, //AVAILABLE_BALANCE
        public string LastChange { get; set; }//"Exchange 2.0 UST for USD @ 11.696", 

    }

    public class BitfinexWallet
    {
        public BitfinexPortfolioRest[] balances { get; set; }
    }


    public enum BitfinexExchangeType
    {
        SpotExchange,
        FuturesExchange,
        MarginExchange
    }



}


//[
//  [
//  	"exchange", //TYPE
//  	"UST", //CURRENCY
//  	19788.6529257, //BALANCE
//  	0, //UNSETTLED_INTEREST
//  	19788.6529257, //AVAILABLE_BALANCE
//  	"Exchange 2.0 UST for USD @ 11.696", //LAST_CHANGE
//  	{
//  		reason: "TRADE",
//          order_id: 1189740779,
//          order_id_oppo: 1189785673,
//          trade_price: "11.696",
//          trade_amount: "-2.0",
//          order_cid: 1598516362757,
//          order_gid: 1598516362629
//  	} //TRADE_DETAILS
//  ], //WALLET
//  [...]
//]


//"Exchange 2.0 UST for USD @ 11.696", //LAST_CHANGE
//{
//	reason: "TRADE",
//	order_id: 1189740779,
//	order_id_oppo: 1189785673,
//	trade_price: "11.696",
//	trade_amount: "-2.0",
//	order_cid: 1598516362757,
//	order_gid: 1598516362629
//} //TRADE_DETAILS
//  //WALLET



