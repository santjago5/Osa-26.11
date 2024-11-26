using Newtonsoft.Json;
using OsEngine.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{
    public class BitfinexBook
    {

       
        public string OrderId { get; set; }

        public string Price { get; set; }

        public string Amount { get; set; }
    }
}
