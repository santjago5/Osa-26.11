

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexOrderData
    {
        public string Id { get; set; }                    //1747566428, //ID// 
        public string Gid { get; set; }                   //GID Group Order ID
        public string Cid { get; set; }                   //1678987199446, //CID Client Order ID
        public string Symbol { get; set; }                //"tBTCUSD", //SYMBOL
        public string MtsCreate { get; set; }             //1678988263843, //MTS_CREATE
        public string MtsUpdate { get; set; }             //1678988263843, //MTS_UPDATE
        public string Amount { get; set; }                //0.25, //AMOUNT
        public string AmountOrig { get; set; }            //0.1, //AMOUNT_ORIG
        public string OrderType { get; set; }             //"EXCHANGE LIMIT", //ORDER_TYPE
        public string TypePrev { get; set; }              //"EXCHANGE LIMIT", //TYPE_PREV
        public string MtsTif { get; set; }                //MTS_TIF
        public string Flags { get; set; }                 // 0, //FLAGS
        public string Status { get; set; }               // "ACTIVE", //STATUS
        public string Price { get; set; }                // 25000, //PRICE
        public string PriceAvg { get; set; }             // 0,153 //PRICE_AVG
        public string PriceTrailing { get; set; }        // 0, //PRICE_TRAILING
        public string PriceAuxLimit { get; set; }        // 0, //PRICE_AUX_LIMIT
        public string Notify { get; set; }               // 0, //NOTIFY
        public string Hidden { get; set; }               // 0, //HIDDEN
        public string PlacedId { get; set; }              // null, //PLACED_ID
        public string Routing { get; set; }               //  "API>BFX", //ROUTING
      
    }
}
