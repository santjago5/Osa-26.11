using Newtonsoft.Json;
using OsEngine.Logging;
using OsEngine.Market.Servers.Alor.Json;
using OsEngine.Market.Servers.Bitfinex.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexMarketDepth///BookEntry //  //Total amount available at that price level (if AMOUNT > 0 then bid else ask)
    {

        public string Price { get; set; }
        public string Count { get; set; }
        public string Amount { get; set; }
  
   }
    public class BitfinexMarketDepthLevel
    {
        //public List<string> asks;
        //public List<string> bids;

        //public List<List<string>> ask { get; set; }
        //public List<List<string>> bid { get; set; }

        //public List<string>ask { get; set; }
        //public List<string> bid { get; set; }

        public List<BitfinexMarketDepth> bids;

        public List<BitfinexMarketDepth> asks;

    }
  
}