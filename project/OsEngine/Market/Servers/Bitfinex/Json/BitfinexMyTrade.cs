using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexMyTrade
    {
        public string Id;                   //402088407, //ID
        public string Symbol;               //tETHUST", //SYMBOL
        public string MtsCreate;                  //1574963975602, //MTS
        public string OrderId;             //34938060782, //ORDER_ID
        public string ExecAmount;          //-0.2, //EXEC_AMOUNT
        public string ExecPrice;           //153.57, //EXEC_PRICE
        public string OrderType;           //MARKET, //ORDER_TYPE
        public string OrderPrice;          //0, //ORDER_PRICE
        public string Maker;                //-1, //MAKER
        public string Fee;                  //-0.061668, //FEE
        public string FeeCurrency;         //USD, //FEE_CURRENCY
        public string Cid;                  //1234 //CID
    }
}



//Класс для десериализации ответа на подписку
public class BitfinexSubscriptionResponse
{
    public string Event { get; set; }
    public string Channel { get; set; }
    public string ChanId { get; set; }
    public string Symbol { get; set; }
    public string Pair { get; set; }
}

//Класс для десериализации трейдовых сообщений


//Класс для десериализации снимка трейдов (snapshot)




// Определяем модель для структуры данных
public class BitfinexUpdateTrades
{// [10098,\"tu\",[1657561837,1726071091967,-28.61178052,0.1531]]"

    public string ChannelId { get; set; }    // 10098 - идентификатор канала
    public string Type { get; set; }       // "tu" - тип сообщения
    public TradeData Data { get; set; }    // Объект с данными о торговой операции
}

// Класс для хранения данных о торговой операции
public class TradeData
{
    public string Id { get; set; }      // 1657561837
    public string Mts { get; set; }      // 1726071091967
    public string Amount { get; set; }    // -28.61178052
    public string Price { get; set; }     // 0.1531
}

//[
//  17470, //CHANNEL_ID
//  "te", //MSG_TYPE
//  [
//    401597395, //ID
//    1574694478808, //MTS
//    0.005, //AMOUNT
//    7245.3 //PRICE
//  ] //TRADE
