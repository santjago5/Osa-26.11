using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{


    public class BitfinexSubscriptionResponse
    {
        public string Event { get; set; }
        public string Channel { get; set; }
        public string ChanId { get; set; }
        public string Symbol { get; set; }
        public string Pair { get; set; }
    }
    //Класс для представления трейда:
    public class BitfinexTrades
    {
        public string Id { get; set; }
        public string Mts { get; set; }
        public string Amount { get; set; }
        public string Price { get; set; }
    }

    //Класс для представления снимка и обновлений:
    public class TradeSnapshot
    {
        public string ChannelId { get; set; }
        public List<BitfinexTrades> Trades { get; set; }
    }

    //public class BitfinexTradeUpdate
    //{
    //    public string ChannelId { get; set; }
    //    public string MsgType { get; set; }
    //    public BitfinexTrades Trade { get; set; }
    //}


    public class BitfinexMyTradeUpdate
    {
        public string Id { get; set; }//LONG
        public string Symbol { get; set; }
        public string MtsCreate { get; set; }//LONG
        public string OrderId { get; set; }//LONG
        public string ExecAmount { get; set; }//double
        public string ExecPrice { get; set; }//double
        public string OrderType { get; set; }
        public string OrderPrice { get; set; }//double
        public string Maker { get; set; }//int
        public string Fee { get; set; }//double
        public string FeeCurrency { get; set; }
        public string Cid { get; set; }//LONG
    }

    class BitfinexAuthResponseWebSocket
    {
        public string Event { get; set; }
        public string Status { get; set; }
        public string ChanId { get; set; }
        public string UserId { get; set; }
        public string AuthId { get; set; }
        public string Msg { get; set; }
    }

    public class WalletData
    {
        public string WalletType { get; set; } // Тип кошелька (например, "exchange", "margin")
        public string Currency { get; set; }   // Валюта (например, "USD", "BTC")
        public decimal Balance { get; set; }   // Общий баланс
        public decimal UnsettledInterest { get; set; } // Непогашенный интерес
        public decimal BalanceAvailable { get; set; }  // Доступный баланс
    }
}
