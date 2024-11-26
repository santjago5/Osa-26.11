using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OsEngine.Market.Servers.BingX.BingXSpot.Entity;
using OsEngine.Market.Servers.Bitfinex.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Market.Servers.Bitfinex.Json
{


    public class BitfinexSecurity
    {
        // GET https://api-pub.bitfinex.com/v2/tickers?symbols=ALL

       
        public string Symbol { get; set; }                    //tBTCUSD //SYMBOL

        public string Bid { get; set; }                       //10645 //BID

        public string BidSize { get; set; }                  //73.93854271 //BID_SIZE

        public string Ask { get; set; }                       //10647 //ASK

        public string AskSize { get; set; }                  //75.22266119 //ASK_SIZE

        public string DailyChange { get; set; }              //731.60645389 //DAILY_CHANGE

        public string DailyChangeRelative { get; set; }     //0.0738 //DAILY_CHANGE_RELATIVE

        public string LastPrice { get; set; }                //10644.00645389 //LAST_PRICE

        public string Volume { get; set; }                    //14480.89849423 //VOLUME

        public string High { get; set; }                      //10766 //HIGH

        public string Low { get; set; }                       //9889.1449809 //LOW
    }

    public class TradingSnapshot
    {
        public string ChannelId { get; set; }          // Идентификационный номер канала
        public string Bid { get; set; }            // Цена последней наивысшей заявки на покупку
        public string BidSize { get; set; }        // Сумма 25 наивысших размеров заявок на покупку
        public string Ask { get; set; }            // Цена последней наименьшей заявки на продажу
        public string AskSize { get; set; }        // Сумма 25 наименьших размеров заявок на продажу
        public string DailyChange { get; set; }    // Сумма изменения последней цены с предыдущего дня
        public string DailyChangeRelative { get; set; } // Относительное изменение цены с предыдущего дня
        public string LastPrice { get; set; }      // Цена последней сделки
        public string Volume { get; set; }         // Ежедневный объем торгов
        public string High { get; set; }           // Ежедневный максимум
        public string Low { get; set; }            // Ежедневный минимум
    }

}

