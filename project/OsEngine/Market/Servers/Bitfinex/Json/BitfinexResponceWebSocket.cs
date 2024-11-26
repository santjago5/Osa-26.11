using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace OsEngine.Market.Servers.Bitfinex.Json
{

    public class BitfinexResponceWebSocketMyTrades//BitfinexTrade
    {
        public string Id { get; set; }
        public string Symbol { get; set; }
        public string Timestamp { get; set; }
        public string OrderId { get; set; }
        public string Amount { get; set; }
        public string Price { get; set; }
        public string OrderType { get; set; }
        public string OrderPrice { get; set; }
        public string Maker { get; set; }
        public string Fee { get; set; }  // Nullable for 'te' events
        public string FeeCurrency { get; set; }  // Nullable for 'te' events
        public string ClientOrderId { get; set; }
    }

    public class BitfinexWebSocketMyTradeMessage
    {
        public string ChannelId { get; set; }
        public string MsgType { get; set; }
        public List<object> TradeArray { get; set; }
    }



    public class BitfinexTradeMessage
    {
        public string ChannelId { get; set; }
        public string MsgType { get; set; }
        public List<object> TradeArray { get; set; }
    }

    public class BitfinexTradeUpdate
    {
        public string Id { get; set; }
        public string Timestamp { get; set; }
        public string Amount { get; set; }
        public string Price { get; set; }
    }



    public class BitfinexResponseDepth
    {
        public string Event { get; set; }
        public string Channel { get; set; }
        public string ChanId { get; set; }
        public string Symbol { get; set; }
        public string Pair { get; set; }
    }


    // Определение класса для структуры данных записи стакана
    public class BitfinexBookEntry
    {
        public string Price { get; set; }
        public string Count { get; set; }
        public string Amount { get; set; }
    }

    // Определение класса для структуры данных снимка стакана
    public class BitfinexBookSnapshot
    {
        public string ChannelId { get; set; }
        public List<BitfinexBookEntry> BookEntries { get; set; }
    }

    public class BitfinexBookUpdate
    {
        public string ChannelId { get; set; }
        public BitfinexBookEntry BookEntry { get; set; }
    }



    public class BitfinexResponceWebSocketDepth
    {
       
        public string Event { get; set; }
   
        public string Channel { get; set; }
        
        public string ChanId { get; set; }

        public string Symbol { get; set; }
        
        public string prec { get; set; }
     
        public string Freq { get; set; }
  
        public string Len { get; set; }
        
        public string SubId { get; set; }     
        public string Pair { get; set; }

        // "event":"subscribe","channel":"book","symbol":"tBTCUSD","prec":"P0","freq":"F0","len":"25","subId": 123
    }

    
    


    }




    




    //public class BitfinexResponceWebSocketDepth
    //{
    //    [JsonProperty("event")]
    //    public string Event { get; set; }
    //    [JsonProperty("channel")]
    //    public string Channel { get; set; }
    //    [JsonProperty("chanId")]
    //    public int ChannelId { get; set; }
    //    [JsonProperty("symbol")]
    //    public string Symbol { get; set; }
    //    [JsonProperty("prec")]
    //    public string Precision { get; set; }
    //    [JsonProperty("freq")]
    //    public string Frequency { get; set; }
    //    [JsonProperty("len")]
    //    public string Length { get; set; }
    //    [JsonProperty("subId")]
    //    public int SubscriptionId { get; set; }
    //    [JsonProperty("pair")]
    //    public string Pair { get; set; }
    //     "event":"subscribe","channel":"book","symbol":"tBTCUSD","prec":"P0","freq":"F0","len":"25","subId": 123
    //}


    public class BitfinexResponceWebSocketCandles
    {
        public string @event { get; set; }
        public string channel { get; set; }
        public string chanId { get; set; }
        public string key { get; set; }

        //"event":"subscribed","channel":"candles","chanId":343351,"key":"trade:1m:tBTCUSD"
    }

   





//    public class BitfinexResponceWebSocketTicker
//    {
//        event: "subscribe", 
//        channel: "ticker", 
//        symbol: SYMBOL

//   // response - trading
//   event: "subscribed",
//   channel: "ticker",
//   chanId: CHANNEL_ID,
//   symbol: SYMBOL,
//   pair: PAIR

//          "event":"subscribed","channel":"ticker","chanId":224555,"symbol":"tBTCUSD","pair":"BTCUSD"
//    }
//        public class BitfinexResponceWebSocketTrades
//        {

//            // request

//            event: "subscribe", 
//  channel: "trades", 
//  symbol: SYMBOL


//// response Trading

//  event: "subscribed",
//  channel: "trades",
//  chanId: CHANNEL_ID,
//  symbol: "tBTCUSD"
//  pair: "BTCUSD"


//"event":"subscribed","channel":"trades","chanId":19111,"symbol":"tBTCUSD","pair":"BTCUSD"
//            }


//            public class BitfinexResponceWebSocketBooks
//            {
//                // request

//                event: 'subscribe',
//	channel: 'book',
//	symbol: SYMBOL,
//	prec: PRECISION,
//	freq: FREQUENCY,
//	len: LENGTH,
//	subId: SUBID
//                }

//"event":"subscribe","channel":"book","symbol":"tBTCUSD","prec":"P0","freq":"F0","len":"25","subId": 123

//// response

//	event: 'subscribed',
//	channel: 'book',
//	chanId: CHANNEL_ID,
//	symbol: SYMBOL,
//	prec: PRECISION,
//	freq: FREQUENCY,
//	len: LENGTH,
//	subId: SUBID,
//	pair: PAIR


//"event":"subscribed","channel":"book","chanId":10092,"symbol":"tETHUSD","prec":"P0","freq":"F0","len":"25","subId":123,"pair":"ETHUSD"
//            }

//                public class BitfinexResponceWebSocketCandles
//                {

//// request

//   event: "subscribe",
//   channel: "candles",
//   key: "trade:1m:tBTCUSD"


//// response

//  event: "subscribed",
//  channel: "candles",
//  chanId": CHANNEL_ID,
//  key: "trade:1m:tBTCUSD"


//"event":"subscribed","channel":"candles","chanId":343351,"key":"trade:1m:tBTCUSD"
//            }
//        }
