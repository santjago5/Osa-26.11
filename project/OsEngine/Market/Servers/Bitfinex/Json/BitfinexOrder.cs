using Newtonsoft.Json;
using OsEngine.Charts.CandleChart.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
   
    public class BitfinexResponseOrder
    {
        public List<BitfinexOrderData> OrderData { get; set; } // Это основной массив данных ордера
     
        public string Mts { get; set; } // Временная метка (MTS)//1678988263842, //MTS
        public string Type { get; set; } // Тип события (TYPE)/ "ou-req"order cancel request, //TYPE
        public string MessageId { get; set; } // ID сообщения (MESSAGE_ID)
        public string Data { get; set; } // DATA массив (десериализуется отдельно)
        public string Code { get; set; } // Код ответа (CODE)
        public string Status { get; set; } // Статус (STATUS) (SUCCESS, ERROR, FAILURE, ..., //STATUS
        public string Text { get; set; } // Текст ответа (TEXT)text Submitting exchange limit buy order for 0.1 BTC."
    }

    public class BitfinexOrderData
    {
        public string Id { get; set; }//1747566428, //ID// Идентификатор заказа (ID)
        public string Gid { get; set; }//GID Group Order ID
        public string Cid { get; set; }//1678987199446, //CID Client Order ID
        public string Symbol { get; set; }//"tBTCUSD", //SYMBOL
        public string MtsCreate { get; set; }//1678988263843, //MTS_CREATE
        public string MtsUpdate { get; set; } //1678988263843, //MTS_UPDATE
        public string Amount { get; set; }// 0.25, //AMOUNT
        public string AmountOrig { get; set; }//    0.1, //AMOUNT_ORIG
        public string OrderType { get; set; }  //"EXCHANGE LIMIT", //ORDER_TYPE
        public string TypePrev { get; set; } //"EXCHANGE LIMIT", //TYPE_PREV
        public string MtsTif { get; set; } //MTS_TIF
        public string Flags { get; set; } // 0, //FLAGS
        public string Status { get; set; }   // "ACTIVE", //STATUS
        public string Price { get; set; }// 25000, //PRICE
        public string PriceAvg { get; set; }  // 0,153 //PRICE_AVG
        public string PriceTrailing { get; set; }// 0, //PRICE_TRAILING
        public string PriceAuxLimit { get; set; } // 0, //PRICE_AUX_LIMIT
        public string Notify { get; set; } // 0, //NOTIFY
        public string Hidden { get; set; }  //0, //HIDDEN
        public string PlacedId { get; set; }// null, //PLACED_ID
        public string Routing { get; set; }//  "API>BFX", //ROUTING
        //public JsonElement Meta { get; set; } // Meta information as JSON element //null //META
    }


    //public class BitfinexOrder
    //{
    //    // Уникальный идентификатор ордера
    //    public long Id { get; set; }

    //    // Торговая пара (например, tBTCUSD)
    //    public string Symbol { get; set; }

    //    // Момент создания ордера в формате Unix timestamp
    //    public long MtsCreate { get; set; }

    //    // Момент обновления ордера в формате Unix timestamp
    //    public long MtsUpdate { get; set; }

    //    // ID клиента
    //    public long ClientOrderId { get; set; }

    //    // Цена ордера
    //    public decimal Price { get; set; }

    //    // Средняя цена исполнения ордера
    //    public decimal AvgExecutionPrice { get; set; }

    //    // Объем ордера (например, 0.1 BTC)
    //    public decimal Amount { get; set; }

    //    // Исполненный объем ордера
    //    public decimal AmountExecuted { get; set; }

    //    // Сторона ордера (1 = покупка, -1 = продажа)
    //    public int OrderType { get; set; }

    //    // Тип ордера (например, LIMIT, MARKET)
    //    public string Type { get; set; }

    //    // Флаг статуса ордера (например, ACTIVE, EXECUTED, CANCELED)
    //    public string Status { get; set; }

    //    // Дополнительное поле с информацией о прайсе трейлинга (например, для трейлинг-стопа)
    //    public decimal PriceTrailing { get; set; }

    //    // Дополнительное поле с информацией о прайсе скрытого ордера
    //    public decimal PriceAuxLimit { get; set; }

    //    // Флаг - скрытый ли ордер
    //    public bool IsHidden { get; set; }

    //    // Флаг - Post-Only ордер
    //    public bool IsPostOnly { get; set; }

    //    // Дополнительные комментарии/информация
    //    public string Meta { get; set; }
    //}









    //"Submitting update to exchange limit buy order for 0.1 BTC." //TEXT


    //Available order types are: LIMIT, EXCHANGE LIMIT, MARKET, 
    //EXCHANGE MARKET, STOP, EXCHANGE STOP, STOP LIMIT, EXCHANGE STOP LIMIT,
    //TRAILING STOP, EXCHANGE TRAILING STOP, FOK, EXCHANGE FOK, IOC, 
    //EXCHANGE IOC.

}

//public enum BitfinexOrderType
//{
//    LIMIT,              // Лимитный ордер
//    EXCHANGE_LIMIT,     // Лимитный ордер на бирже
//    MARKET,             // Рыночный ордер
//    EXCHANGE_MARKET,    // Рыночный ордер на бирже
//    STOP,               // Стоп-ордер
//    EXCHANGE_STOP,      // Стоп-ордер на бирже
//    STOP_LIMIT,         // Стоп-лимитный ордер
//    EXCHANGE_STOP_LIMIT,// Стоп-лимитный ордер на бирже
//    TRAILING_STOP,      // Трейлинг-стоп ордер
//    EXCHANGE_TRAILING_STOP, // Трейлинг-стоп ордер на бирже
//    FOK,                // Fill or Kill ордер
//    EXCHANGE_FOK,       // Fill or Kill ордер на бирже
//    IOC,                // Immediate or Cancel ордер
//    EXCHANGE_IOC        // Immediate or Cancel ордер на бирже
//}

//enum orderType
//{
//    limit,
//    exchangeLimit,
//    market,
//    exchangeMarket,
//    stop,
//    exchangeStop,
//    stopLimit,
//    exchangeStopLimit,
//    trailingStop,
//    exchangeTrailingStop,
//    fok,
//    exchangeFok,
//    ioc,
//    exchangeIoc
//}





//public class BitfinexResponseOrderRest
//{
//    [JsonProperty("MTS")]
//    public string mts; //1678988263842, //MTS

//    [JsonProperty("TYPE")]
//    public string type;// "ou-req"order cancel request, //TYPE

//    [JsonProperty("MESSAGE_ID")]
//    public string messageId;//MESSAGE_ID

//    [JsonProperty("DATA ")]
//    public string data;   //DATA 

//    [JsonProperty("CODE")]
//    public string code;    //null, //CODE

//    [JsonProperty("STATUS")]
//    public string status; // (SUCCESS, ERROR, FAILURE, ..., //STATUS

//    [JsonProperty("TEXT")]
//    public string text; //text Submitting exchange limit buy order for 0.1 BTC."
//}



//public class BitfinexOrderResponse
//{
//    public long MTS { get; set; }
//    public string TYPE { get; set; }
//    public int MESSAGE_ID { get; set; }
//    public object[] AdditionalFields { get; set; }
//    //public OrderData DATA { get; set; }
//    public int CODE { get; set; }
//    public string STATUS { get; set; }
//    public string TEXT { get; set; }
//}

//public class BitfinexOrderData
//{
//    internal object order;

//    public int ID { get; set; }
//    public int GID { get; set; }
//    public int CID { get; set; }
//    public string SYMBOL { get; set; }
//    public long MTS_CREATE { get; set; }
//    public long MTS_UPDATE { get; set; }
//    public float AMOUNT { get; set; }
//    public float AMOUNT_ORIG { get; set; }
//    public string ORDER_TYPE { get; set; }
//    public string TYPE_PREV { get; set; }
//    public long MTS_TIF { get; set; }
//    public int FLAGS { get; set; }
//    public string STATUS { get; set; }
//    public float PRICE { get; set; }
//    public float PRICE_AVG { get; set; }
//    public float PRICE_TRAILING { get; set; }
//    public float PRICE_AUX_LIMIT { get; set; }
//    public int NOTIFY { get; set; }
//    public int HIDDEN { get; set; }
//    public int PLACED_ID { get; set; }
//    public string ROUTING { get; set; }
//    public dynamic META { get; set; }
//}




//public class OrderSubmitResponse
//{
//    public long MTS { get; set; }
//    public string TYPE { get; set; }
//    public object MESSAGE_ID { get; set; }
//    public object PLACEHOLDER1 { get; set; }
//    public OrderData[] DATA { get; set; }
//    public object CODE { get; set; }
//    public string STATUS { get; set; }
//    public string TEXT { get; set; }
//}

//public class OrderData
//{
//    public int ID { get; set; }
//    public object GID { get; set; }
//    public long CID { get; set; }
//    public string SYMBOL { get; set; }
//    public long MTS_CREATE { get; set; }
//    public long MTS_UPDATE { get; set; }
//    public float AMOUNT { get; set; }
//    public float AMOUNT_ORIG { get; set; }
//    public string ORDER_TYPE { get; set; }
//    public object TYPE_PREV { get; set; }
//    public object MTS_TIF { get; set; }
//    public object PLACEHOLDER2 { get; set; }
//    public int FLAGS { get; set; }
//    public string STATUS { get; set; }
//    public object PLACEHOLDER3 { get; set; }
//    public object PLACEHOLDER4 { get; set; }
//    public float PRICE { get; set; }
//    public float PRICE_AVG { get; set; }
//    public float PRICE_TRAILING { get; set; }
//    public float PRICE_AUX_LIMIT { get; set; }
//    public object PLACEHOLDER5 { get; set; }
//    public object PLACEHOLDER6 { get; set; }
//    public object PLACEHOLDER7 { get; set; }
//    public int NOTIFY { get; set; }
//    public int HIDDEN { get; set; }
//    public object PLACED_ID { get; set; }
//    public object PLACEHOLDER8 { get; set; }
//    public object PLACEHOLDER9 { get; set; }
//    public string ROUTING { get; set; }
//    public object PLACEHOLDER10 { get; set; }
//    public object PLACEHOLDER11 { get; set; }
//    public object PLACEHOLDER12 { get; set; }
//}
