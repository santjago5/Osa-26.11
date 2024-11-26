using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class Security

    {
        // GET https://api-pub.bitfinex.com/v2/trades/{symbol}/hist
        //[JsonProperty("SYMBOL")]
        //public string symbol;

        [JsonProperty("ID")]
        public string id;                   //402088407, //ID

       
        [JsonProperty("MTS")]
        public string time;                  //1574963975602, //MTS

        [JsonProperty("AMOUNT")]
        public string amount;          //-0.2, //AMOUNT

        
        [JsonProperty("PRICE")] 
        public string Price;           //153.57, //EXEC_PRICE

       
    }
}











