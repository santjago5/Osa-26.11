using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tinkoff.InvestApi.V1;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexPortfolioSocket
    {
        public string ChanId { get;set;}
        public string  Type {get;set;}
        public string WalletType{ get; set; }
        public string Currency { get; set; }
        public string Balance { get; set; }
        public string Unsettled_interest{ get; set; }
        public string BalanceAvailable{get; set; }
        public string Description { get; set; }
        public string Meta{ get; set; }



        //[0,"wu",["exchange","BTC",1.61169184,0,null,"Exchange 0.01 BTC for USD @ 7804.6",{"reason":"TRADE","order_id":34988418651,"order_id_oppo":34990541044,"trade_price":"7804.6","trade_amount":"0.01"}]]
    }
}
