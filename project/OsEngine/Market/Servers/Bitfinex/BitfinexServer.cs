
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Bitfinex.Json;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Servers.Transaq.TransaqEntity;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using WebSocket4Net;
using Candle = OsEngine.Entity.Candle;
using ErrorEventArgs = SuperSocket.ClientEngine.ErrorEventArgs;
using MarketDepth = OsEngine.Entity.MarketDepth;
using Method = RestSharp.Method;
using Order = OsEngine.Entity.Order;
using Security = OsEngine.Entity.Security;
using Side = OsEngine.Entity.Side;
using Trade = OsEngine.Entity.Trade;
using WebSocket = WebSocket4Net.WebSocket;
using WebSocketState = WebSocket4Net.WebSocketState;
using Timer = System.Timers.Timer;
using WebSocketSharp;
using OsEngine.Charts.CandleChart.Indicators;
using System.Windows.Controls;
using System.Runtime.Remoting;
using static System.Net.WebRequestMethods;



namespace OsEngine.Market.Servers.Bitfinex
{
    public class BitfinexServer : AServer
    {
        public BitfinexServer()
        {
            BitfinexServerRealization realization = new BitfinexServerRealization();
            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
        }
    }
    public class BitfinexServerRealization : IServerRealization
    {
        #region 1 Constructor, Status, Connection
        public BitfinexServerRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;

            Thread threadForPublicMessages = new Thread(PublicMessageReader);
            threadForPublicMessages.IsBackground = true;
            threadForPublicMessages.Name = "PublicMessageReaderBitfinex";
            threadForPublicMessages.Start();

            Thread threadForPrivateMessages = new Thread(PrivateMessageReader);
            threadForPrivateMessages.IsBackground = true;
            threadForPrivateMessages.Name = "PrivateMessageReaderBitfinex";
            threadForPrivateMessages.Start();
        }
        public DateTime ServerTime { get; set; }
        public void Connect()
        {
            try
            {
                SendLogMessage("Start Bitfinex Connection", LogMessageType.System);

                _publicKey = ((ServerParameterString)ServerParameters[0]).Value;
                _secretKey = ((ServerParameterPassword)ServerParameters[1]).Value;

                if (string.IsNullOrEmpty(_publicKey) || string.IsNullOrEmpty(_secretKey))
                {
                    SendLogMessage("Connection failed. Authorization exception", LogMessageType.Error);
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                    return;
                }
                string _apiPath = "v2/platform/status";
                RestClient client = new RestClient(_baseUrl);
                RestRequest request = new RestRequest(_apiPath);
                request.AddHeader("accept", "application/json");

                IRestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;

                    if (responseBody.Contains("1"))
                    {
                        CreateWebSocketConnection();
                    }
                }
            }
            catch (Exception exception)
            {
                SendLogMessage("Connection cannot be open Bitfinex. exception:" + exception.ToString(), LogMessageType.Error);
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }
        public void Dispose()
        {
            try
            {
                _securities.Clear();
                _portfolios.Clear();
                //marketDepth.Bids.Clear();
                //marketDepth.Asks.Clear();
                //tradeDictionary.Clear();///////////
                //depthDictionary.Clear();//////////

                DeleteWebSocketConnection();
            }
            catch (Exception exception)
            {
                SendLogMessage("Connection closed by Bitfinex. WebSocket Data Closed Event" + exception.ToString(), LogMessageType.System);
            }

            if (ServerStatus != ServerConnectStatus.Disconnect)
            {
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }
        public ServerType ServerType => ServerType.Bitfinex;
        public event Action ConnectEvent;
        public event Action DisconnectEvent;
        #endregion

        #region 2 Properties 
        public List<IServerParameter> ServerParameters { get; set; }
        public ServerConnectStatus ServerStatus { get; set; }

        private string _publicKey = "";

        private string _secretKey = "";

        private string _baseUrl = "https://api.bitfinex.com";

        #endregion

        #region 3 Securities

        private RateGate _rateGateSecurity = new RateGate(30, TimeSpan.FromMinutes(1));
        private RateGate _rateGatePositions = new RateGate(90, TimeSpan.FromMinutes(1));
        private RateGate _rateGateTrades = new RateGate(15, TimeSpan.FromMinutes(1));
        private List<Security> _securities = new List<Security>();
        public event Action<List<Security>> SecurityEvent;

        public void GetSecurities()
        {
            _rateGateSecurity.WaitToProceed();

            RequestMinSizes();

            try
            {
                string _apiPath = "v2/tickers?symbols=ALL";
                RestClient client = new RestClient(_baseUrl);
                RestRequest request = new RestRequest(_apiPath);
                request.AddHeader("accept", "application/json");

                IRestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string jsonResponse = response.Content;

                    List<List<object>> securityList = JsonConvert.DeserializeObject<List<List<object>>>(jsonResponse);

                    if (securityList == null)
                    {
                        SendLogMessage("Deserialization resulted in null", LogMessageType.Error);
                        return;
                    }

                    if (securityList.Count > 0)

                    {
                        SendLogMessage("Securities loaded. Count: " + securityList.Count, LogMessageType.System);
                        SecurityEvent?.Invoke(_securities);
                    }

                    List<Security> securities = new List<Security>();

                    for (int i = 0; i < securityList.Count; i++)
                    {
                        List<object> item = securityList[i];
                        string symbol = item[0]?.ToString();
                        string price = item[1]?.ToString()?.Replace('.', ',');
                        string volume = item[8]?.ToString()?.Replace('.', ',');
                        SecurityType securityType = GetSecurityType(symbol);

                        if (securityType == SecurityType.None)
                        {
                            continue;
                        }

                        Security newSecurity = new Security();

                        newSecurity.Exchange = ServerType.Bitfinex.ToString();
                        newSecurity.Name = symbol;
                        newSecurity.NameFull = symbol;
                        newSecurity.NameClass = symbol.StartsWith("f") ? "Futures" : "CurrencyPair";
                        newSecurity.NameId = symbol;
                        newSecurity.SecurityType = securityType;
                        newSecurity.Lot = 1;
                        newSecurity.State = SecurityStateType.Activ;
                        newSecurity.Decimals = DigitsAfterComma(price);
                        newSecurity.PriceStep = CalculatePriceStep(price).ToString().ToDecimal();//1;                                                     // (CalculatePriceStep(price)).ToString().ToDecimal();/*newSecurity.Decimals.GetValueByDecimals();*/
                        newSecurity.PriceStepCost = newSecurity.PriceStep;                             //(newSecurity.PriceStep) * (price).ToDecimal();
                        newSecurity.DecimalsVolume = DigitsAfterComma(volume);                       //кол-во знаков после запятой  объем инструмента
                        newSecurity.MinTradeAmount = GetMinSize(symbol);

                        securities.Add(newSecurity);
                    }

                    if (SecurityEvent != null)
                    {
                        SecurityEvent(securities);
                    }
                }
                else
                {
                    SendLogMessage("Securities request exception. Status: " + response.Content, LogMessageType.Error);

                }
            }
            catch (Exception exception)
            {
                SendLogMessage("Securities request exception" + exception.ToString(), LogMessageType.Error);
            }
        }
        public double CalculatePriceStep(string price)
        {
            if (string.IsNullOrWhiteSpace(price))
            {
                SendLogMessage("Price cannot be null or empty.", LogMessageType.Error);
                return 0;
            }

            int decimalPlaces = 0;

            if (price.Contains("."))
            {
                decimalPlaces = price.Split('.')[1].Length;
            }

            else if (price.Contains(","))
            {
                decimalPlaces = price.Split(',')[1].Length;
            }

            return Math.Pow(10, -decimalPlaces);
        }
        private int DigitsAfterComma(string valueNumber)
        {
            int commaPosition = valueNumber.IndexOf(',');
            int digitsAfterComma = valueNumber.Length - commaPosition - 1;
            return digitsAfterComma;
        }

        public Dictionary<string, decimal> minSizes = new Dictionary<string, decimal>();
        public void RequestMinSizes()
        {
            try
            {
                string _apiPatch = "/v2/conf/pub:info:pair"; //для спота
                var client = new RestClient(_baseUrl);
                var request = new RestRequest(_apiPatch, Method.GET);

                var response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Content))
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    for (int i = 0; i < data.Count; i++)
                    {
                        var subArray = data[i];

                        for (int j = 0; j < subArray.Count; j++)
                        {
                            if (subArray[j] is JArray pairData && pairData.Count > 1)
                            {
                                string pair = "t" + pairData[0]?.ToString();

                                if (pairData[1] is JArray limits && limits.Count > 3 && limits[3] != null)
                                {
                                    string minSizeString = limits[3].ToString();

                                    if (decimal.TryParse(minSizeString, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out decimal minSize))
                                    {
                                        minSizes.Add(pair, minSize);
                                    }
                                }
                                else
                                {
                                    SendLogMessage($"Error  json is null", LogMessageType.Error);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SendLogMessage($"Error : {ex.Message}", LogMessageType.Error);
            }
        }

        public decimal GetMinSize(string symbol)
        {
            if (minSizes.TryGetValue(symbol, out decimal minSize))
            {
                return minSize;
            }

            return 1;
        }

        private SecurityType GetSecurityType(string type)
        {
            SecurityType _securityType = type.StartsWith("t") ? SecurityType.CurrencyPair : SecurityType.Futures;

            return _securityType;
        }
        #endregion

        #region 4 Portfolios

        private List<Portfolio> _portfolios = new List<Portfolio>();

        public event Action<List<Portfolio>> PortfolioEvent;

        private RateGate _rateGatePortfolio = new RateGate(90, TimeSpan.FromMinutes(1));

        public void GetPortfolios()
        {
            CreateQueryPortfolio();

            if (_portfolios.Count != 0)
            {
                PortfolioEvent?.Invoke(_portfolios);
            }
        }
        private void CreateQueryPortfolio()
        {
            _rateGatePortfolio.WaitToProceed();

            try
            {
                string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
                string _apiPath = "v2/auth/r/wallets";
                string signature = $"/api/{_apiPath}{nonce}";
                string sig = ComputeHmacSha384(_secretKey, signature);

                RestClient client = new RestClient(_baseUrl);
                var request = new RestRequest(_apiPath, Method.POST);

                request.AddHeader("accept", "application/json");
                request.AddHeader("bfx-nonce", nonce);
                request.AddHeader("bfx-apikey", _publicKey);
                request.AddHeader("bfx-signature", sig);

                IRestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Portfolio portfolio = new Portfolio();
                    portfolio.Number = "BitfinexPortfolio";
                    portfolio.ValueBegin = 1;
                    portfolio.ValueCurrent = 1;

                    List<List<object>> wallets = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    for (int i = 0; i < wallets.Count; i++)
                    {
                        List<object> wallet = wallets[i];

                        if (wallet[0].ToString() == "exchange")
                        {
                            PositionOnBoard position = new PositionOnBoard();

                            position.PortfolioName = "BitfinexPortfolio";
                            position.SecurityNameCode = wallet[1].ToString();
                            position.ValueBegin = wallet[2].ToString().ToDecimal();
                            position.ValueCurrent = wallet[4].ToString().ToDecimal();
                            if (wallet[4] != null)
                            {
                                position.ValueBlocked = wallet[2].ToString().ToDecimal() - wallet[4].ToString().ToDecimal();
                            }
                            else
                            {
                                position.ValueBlocked = 0;
                            }

                            portfolio.SetNewPosition(position);
                        }
                    }

                    _portfolios.Add(portfolio);

                    if (_portfolios.Count != 0)
                    {
                        PortfolioEvent?.Invoke(_portfolios);
                    }
                }

                else
                {
                    SendLogMessage($"Error Query Portfolio: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void UpdatePortfolio(string json)
        {
            try
            {
                List<object> walletArray = JsonConvert.DeserializeObject<List<object>>(json);
                List<object> wallet = JsonConvert.DeserializeObject<List<object>>(walletArray[2].ToString());

                if (walletArray == null)
                {
                    return;
                }

                if (wallet == null)
                {
                    return;
                }

                Portfolio portfolio = new Portfolio();
                portfolio.Number = "BitfinexPortfolio";
                portfolio.ValueBegin = 1;
                portfolio.ValueCurrent = 1;
                portfolio.ServerType = ServerType.Bitfinex;

                if (wallet[0].ToString() == "exchange")
                {
                    PositionOnBoard position = new PositionOnBoard();
                    position.PortfolioName = "BitfinexPortfolio";
                    position.SecurityNameCode = wallet[1].ToString();
                    position.ValueCurrent = wallet[2].ToString().ToDecimal();
                    position.ValueBegin = wallet[2].ToString().ToDecimal();

                    if (wallet[4] != null)
                    {
                        position.ValueBlocked = wallet[2].ToString().ToDecimal() - wallet[4].ToString().ToDecimal();
                    }
                    else
                    {
                        position.ValueBlocked = 0;
                    }
                    portfolio.SetNewPosition(position);
                }

                _portfolios.Add(portfolio);

                if (_portfolios.Count > 0)
                {
                    PortfolioEvent?.Invoke(_portfolios);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        #endregion

        #region 5 Data Candles

        private RateGate _rateGateCandleHistory = new RateGate(30, TimeSpan.FromMinutes(1));
        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            List<Trade> trades = new List<Trade>();
            int limit = 10000;

            List<Trade> lastTrades = BitfinexGetTrades(security.Name, limit, startTime, endTime);

            if (lastTrades == null || lastTrades.Count == 0)
            {
                SendLogMessage("No trades found.", LogMessageType.Error);
                return trades;
            }
            trades.AddRange(lastTrades);

            if (trades.Count == 0)
            {
                SendLogMessage("No trades found after adding last trades.", LogMessageType.Error);
                return trades;
            }

            DateTime curEnd = trades[0].Time;

            DateTime cu = trades[trades.Count - 1].Time;

            while (curEnd > startTime)
            {
                List<Trade> newTrades = BitfinexGetTrades(security.Name, limit, startTime, endTime);

                if (newTrades == null || newTrades.Count == 0)
                {
                    return trades;
                }

                if (trades.Count > 0 && newTrades.Count > 0)
                {
                    if (newTrades[newTrades.Count - 1].Time >= trades[0].Time)
                    {
                        newTrades.RemoveAt(newTrades.Count - 1);
                    }
                }

                trades.InsertRange(0, newTrades);

                curEnd = trades[0].Time;
            }

            return trades;
        }
        private List<Trade> BitfinexGetTrades(string security, int count, DateTime startTime, DateTime endTime)
        {
            try
            {
                Thread.Sleep(3000);

                _rateGateTrades.WaitToProceed();

                long startDate = (long)(startTime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
                long endDate = (long)(endTime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;

                string _apiPath = $"/v2/trades/{security}/hist?limit={count}&start={startDate}&end={endDate}";
                RestClient client = new RestClient(_baseUrl);
                var request = new RestRequest(_apiPath, Method.GET);
                request.AddHeader("accept", "application/json");

                IRestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    List<List<object>> tradeList = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    if (tradeList == null || tradeList.Count == 0)
                    {
                        return null;
                    }

                    List<Trade> trades = new List<Trade>();

                    for (int i = 0; i < tradeList.Count; i++)
                    {
                        Trade newTrade = new Trade();
                        newTrade.Id = tradeList[i][0].ToString();
                        newTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(tradeList[i][1]));
                        newTrade.SecurityNameCode = security;
                        decimal amount = tradeList[i][2].ToString().ToDecimal();
                        newTrade.Volume = Math.Abs(amount);
                        newTrade.Price = (tradeList[i][3]).ToString().ToDecimal();
                        newTrade.Side = amount > 0 ? Side.Buy : Side.Sell;

                        trades.Insert(0, newTrade);
                    }

                    return trades;
                }
                else
                {
                    SendLogMessage($"API вернул ошибку: {response.StatusCode} - {response.Content}", LogMessageType.Error);
                    return null;
                }
            }
            catch (Exception e)
            {
                SendLogMessage(e.ToString(), LogMessageType.Error);
                return null;
            }
        }
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf, bool isOsData, int countToLoad, DateTime timeEnd)
        {

            int limit = 10000;

            List<Candle> allCandles = new List<Candle>();

            DateTime startTime = timeEnd - TimeSpan.FromMinutes(tf.TotalMinutes * countToLoad);

            HashSet<DateTime> uniqueTimes = new HashSet<DateTime>();//////////////////////////////////////////////////

            int totalMinutes = (int)(timeEnd - startTime).TotalMinutes;

            DateTime currentStartDate = startTime;

            int candlesLoaded = 0;

            while (candlesLoaded < countToLoad)
            {
                int candlesToLoad = Math.Min(limit, countToLoad - candlesLoaded);

                DateTime periodStart = currentStartDate;
                DateTime periodEnd = periodStart.AddMinutes(tf.TotalMinutes * candlesToLoad);

                if (periodEnd > DateTime.UtcNow)
                {
                    periodEnd = DateTime.UtcNow;
                }
                string timeFrame = GetInterval(tf);

                List<Candle> rangeCandles = CreateQueryCandles(nameSec, timeFrame, periodStart, periodEnd, candlesToLoad);

                if (rangeCandles == null || rangeCandles.Count == 0)
                {
                    break;
                }

                for (int i = 0; i < rangeCandles.Count; i++)
                {
                    if (uniqueTimes.Add(rangeCandles[i].TimeStart))
                    {
                        allCandles.Add(rangeCandles[i]);
                    }
                }

                //// Добавляем загруженные свечи в общий список
                //allCandles.AddRange(rangeCandles);

                // Обновляем количество загруженных свечей
                int actualCandlesLoaded = rangeCandles.Count;

                candlesLoaded += actualCandlesLoaded;

                currentStartDate = rangeCandles[rangeCandles.Count - 1].TimeStart.AddMinutes(tf.TotalMinutes);

                if (candlesLoaded >= countToLoad || currentStartDate >= timeEnd)
                {
                    break;
                }
            }

            for (int i = allCandles.Count - 1; i > 0; i--)
            {
                if (allCandles[i].TimeStart == allCandles[i - 1].TimeStart)
                {
                    allCandles.RemoveAt(i);
                }
            }

            return allCandles;
        }

        //public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf, bool IsOsData, int CountToLoad, DateTime timeEnd)  // енд 19/11, а дальше 26,10 
        //{
        //    int limit = 10000;
        //    int needToLoadCandles = CountToLoad;

        //    List<Candle> allCandles = new List<Candle>();

        //    HashSet<DateTime> uniqueTimes = new HashSet<DateTime>();


        //    DateTime startTime = timeEnd - TimeSpan.FromMinutes(tf.TotalMinutes * CountToLoad);
        //    // DateTime timeEnd = startTime + TimeSpan.FromMinutes(tf.TotalMinutes * CountToLoad);

        //    // DateTime currentStart = timeEnd - TimeSpan.FromMinutes(tf.TotalMinutes * CountToLoad);

        //    int totalMinutes = (int)(timeEnd - startTime).TotalMinutes;
        //    DateTime currentStartDate = startTime;
        //    int candlesLoaded = 0;
        //    while (candlesLoaded < totalMinutes) // (needToLoadCandles > 0)// (startTime < timeEnd)/*()*/ //

        //    {
        //        int candlesToLoad = Math.Min(limit, totalMinutes - candlesLoaded);
        //        DateTime periodStart = startTime.AddMinutes(candlesLoaded);

        //        DateTime periodEnd = periodStart.AddMinutes(candlesToLoad);

        //        if(periodEnd > DateTime.UtcNow)
        //        {
        //            periodEnd =DateTime.UtcNow;
        //        }
        //        //int batchSize = Math.Min(needToLoadCandles, limit);
        //        //DateTime currentEnd = startTime.AddMinutes(tf.TotalMinutes * batchSize);
        //        //if (currentEnd > timeEnd)
        //        //{
        //        //    currentEnd = DateTime.UtcNow;
        //        //}
        //        List<Candle> rangeCandles = new List<Candle>();
        //        //rangeCandles = CreateQueryCandles(nameSec, GetInterval(tf), startTime, currentEnd, batchSize);
        //        rangeCandles = CreateQueryCandles(nameSec, GetInterval(tf), periodStart, periodEnd, candlesToLoad);

        //        if (rangeCandles == null || rangeCandles.Count == 0)
        //        {
        //            break;
        //        }

        //        int actualCandlesLoaded = rangeCandles.Count; // 

        //        DateTime lastCandleTime = rangeCandles[rangeCandles.Count - 1].TimeStart;

        //        if (actualCandlesLoaded < candlesToLoad)
        //        {
        //            lastCandleTime = currentStartDate.AddMinutes(rangeCandles.Count);
        //            //lastCandleTime = rangeCandles[rangeCandles.Count - 1].TimeStart;
        //            //periodEnd = lastCandleTime;
        //        }
        //        candlesLoaded += actualCandlesLoaded;
        //        currentStartDate = lastCandleTime.AddMinutes(tf.TotalMinutes); 

        //       // Обновляем startDate для следующего запроса (после последней загруженной свечи)
        //       //currentStartDate = periodEnd;

        //       // DateTime currentEnd = curTime < timeEnd ? curTime : timeEnd;

        //       // DateTime endDate = currentStart.AddMinutes(tf.TotalMinutes * batchSize);

        //       //allCandles.InsertRange(0, rangeCandles);

        //       ////if (allCandles.Count != 0)
        //       ////{
        //       ////    currentEnd = allCandles[0].TimeStart;
        //       ////}

        //       allCandles.AddRange(rangeCandles);

        //        //var rangeCandlesFirst = rangeCandles.First().TimeStart;
        //        //var rangeCandlesLast = rangeCandles.Last().TimeStart;

        //        //startTime = allCandles[allCandles.Count - 1].TimeStart;
        //        //startTime = rangeCandles.First().TimeStart;


        //        //needToLoadCandles -= rangeCandles.Count;
        //        if (candlesLoaded >= totalMinutes)
        //        {

        //            break;
        //        }

        //        if (startTime >= timeEnd)
        //        {
        //            break;
        //        }

        //       // startTime = currentEnd;
        //    }

        //    return allCandles;
        //}

        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            int tfTotalMinutes = (int)timeFrameBuilder.TimeFrameTimeSpan.TotalMinutes;

            if (!CheckTf(tfTotalMinutes))
            {
                return null;
            }

            DateTime timeStart = DateTime.UtcNow - TimeSpan.FromMinutes(timeFrameBuilder.TimeFrameTimeSpan.Minutes * candleCount);
            // DateTime timeStart = DateTime.UtcNow - TimeSpan.FromMinutes(tfTotalMinutes * candleCount);
            DateTime timeEnd = DateTime.UtcNow;

            return GetCandleDataToSecurity(security, timeFrameBuilder, timeStart, timeEnd, timeStart);
        }
        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            if (startTime != actualTime)
            {
                startTime = actualTime;
            }

            if (!CheckTime(startTime, endTime, actualTime))
            {
                return null;
            }

            if (endTime > DateTime.UtcNow)
            {
                endTime = DateTime.UtcNow;
            }

            int countNeedToLoad = GetCountCandlesFromPeriod(startTime, endTime, timeFrameBuilder.TimeFrameTimeSpan);

            return GetCandleHistory(security.NameFull, timeFrameBuilder.TimeFrameTimeSpan, true, countNeedToLoad, endTime);
        }
        private bool CheckTime(DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            if (startTime >= endTime ||
                startTime >= DateTime.Now ||
                actualTime > endTime ||
                actualTime > DateTime.Now ||
                endTime < DateTime.UtcNow.AddYears(-20))
            {
                return false;
            }

            return true;
        }
        private bool CheckTf(int timeFrameMinutes)
        {
            if (timeFrameMinutes == 1 ||
                timeFrameMinutes == 5 ||
                timeFrameMinutes == 15 ||
                timeFrameMinutes == 30 ||
                timeFrameMinutes == 60 ||
                timeFrameMinutes == 1440)
            {
                return true;
            }
            return false;
        }
        private string GetInterval(TimeSpan tf)
        {
            if (tf.Days > 0)
            {
                return $"{tf.Days}D";
            }
            else if (tf.Hours > 0)
            {
                return $"{tf.Hours}h";
            }
            else if (tf.Minutes > 0)
            {
                return $"{tf.Minutes}m";
            }
            else
            {
                return null;
            }
        }
        private int GetCountCandlesFromPeriod(DateTime startTime, DateTime endTime, TimeSpan tf)
        {
            TimeSpan timePeriod = endTime - startTime;

            if (tf.Days > 0)
            {
                return Convert.ToInt32(timePeriod.TotalDays / tf.TotalDays);
            }
            else if (tf.Hours > 0)
            {
                return Convert.ToInt32(timePeriod.TotalHours / tf.TotalHours);
            }
            else if (tf.Minutes > 0)
            {
                return Convert.ToInt32(timePeriod.TotalMinutes / tf.TotalMinutes);
            }
            else
            {
                SendLogMessage(" Timeframe must be defined in days, hours, or minutes.", LogMessageType.Error);
            }
            return 0;
        }
        private List<Candle> CreateQueryCandles(string nameSec, string tf, DateTime startTime, DateTime endTime, int limit)
        {
            _rateGateCandleHistory.WaitToProceed();

            long startDate = (long)(startTime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
            long endDate = (long)(endTime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;

            string _apiPath = $"/v2/candles/trade:{tf}:{nameSec}/hist?sort=1&start={startDate}&end={endDate}&limit={limit}";
            RestClient client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.GET);
            request.AddHeader("accept", "application/json");

            IRestResponse response = client.Execute(request);

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string jsonResponse = response.Content;

                    List<List<object>> candles = JsonConvert.DeserializeObject<List<List<object>>>(jsonResponse);

                    if (candles == null || candles.Count == 0)
                    {
                        return null;
                    }

                    List<BitfinexCandle> candleList = new List<BitfinexCandle>();

                    for (int i = 0; i < candles.Count; i++)
                    {
                        List<object> candleData = candles[i];

                        BitfinexCandle newCandle = new BitfinexCandle();
                        newCandle.Mts = candleData[0].ToString();
                        newCandle.Open = candleData[1].ToString();
                        newCandle.Close = candleData[2].ToString();
                        newCandle.High = candleData[3].ToString();
                        newCandle.Low = candleData[4].ToString();
                        newCandle.Volume = candleData[5].ToString();

                        candleList.Add(newCandle);
                    }

                    return ConvertToCandles(candleList);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage($"Request error{exception.Message}", LogMessageType.Error);
            }

            return null;
        }
        private List<Candle> ConvertToCandles(List<BitfinexCandle> candleList)
        {
            List<Candle> candles = new List<Candle>();

            try
            {
                for (int i = 0; i < candleList.Count; i++)
                {
                    BitfinexCandle candle = candleList[i];

                    try
                    {
                        if (string.IsNullOrEmpty(candle.Mts) || string.IsNullOrEmpty(candle.Open) ||
                            string.IsNullOrEmpty(candle.Close) || string.IsNullOrEmpty(candle.High) ||
                            string.IsNullOrEmpty(candle.Low) || string.IsNullOrEmpty(candle.Volume))
                        {
                            SendLogMessage("Candle data contains null or empty values", LogMessageType.Error);
                            continue;
                        }

                        if ((candle.Open).ToDecimal() == 0 || (candle.Close).ToDecimal() == 0 ||
                            (candle.High).ToDecimal() == 0 || (candle.Low).ToDecimal() == 0 ||
                            (candle.Volume).ToDecimal() == 0)
                        {
                            SendLogMessage("Candle data contains zero values", LogMessageType.Error);
                            continue;
                        }

                        Candle newCandle = new Candle();
                        newCandle.State = CandleState.Finished;
                        newCandle.TimeStart = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(candle.Mts));
                        newCandle.Open = candle.Open.ToDecimal();
                        newCandle.Close = candle.Close.ToDecimal();
                        newCandle.High = candle.High.ToDecimal();
                        newCandle.Low = candle.Low.ToDecimal();
                        newCandle.Volume = candle.Volume.ToDecimal();

                        candles.Add(newCandle);
                    }
                    catch (Exception exception)
                    {
                        SendLogMessage($"Format exception: {exception.Message}", LogMessageType.Error);
                    }
                }
                //  candles.Reverse();

                return candles;
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
                return null;
            }
        }

        #endregion

        #region  6 WebSocket creation

        private WebSocket _webSocketPublic;
        private WebSocket _webSocketPrivate;

        private ConcurrentQueue<string> _webSocketPublicMessage = new ConcurrentQueue<string>();
        private ConcurrentQueue<string> _webSocketPrivateMessage = new ConcurrentQueue<string>();

        private string _webSocketPublicUrl = "wss://api-pub.bitfinex.com/ws/2";
        private string _webSocketPrivateUrl = "wss://api.bitfinex.com/ws/2";

        private Timer _pingTimer;
        private void CreateWebSocketConnection()
        {
            try
            {
                if (_webSocketPublic != null)
                {
                    return;
                }

                _webSocketPublic = new WebSocket(_webSocketPublicUrl)
                {
                    EnableAutoSendPing = true,
                    AutoSendPingInterval = 15
                };

                _webSocketPublic.Opened += WebSocketPublic_Opened;
                _webSocketPublic.Closed += WebSocketPublic_Closed;
                _webSocketPublic.MessageReceived += WebSocketPublic_MessageReceived;
                _webSocketPublic.Error += WebSocketPublic_Error;

                _webSocketPublic.Open();

                _webSocketPrivate = new WebSocket(_webSocketPrivateUrl)
                {
                    EnableAutoSendPing = true,
                    AutoSendPingInterval = 15
                };

                _webSocketPrivate.Opened += WebSocketPrivate_Opened;
                _webSocketPrivate.Closed += WebSocketPrivate_Closed;
                _webSocketPrivate.MessageReceived += WebSocketPrivate_MessageReceived;
                _webSocketPrivate.Error += WebSocketPrivate_Error;

                _webSocketPrivate.Open();
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void DeleteWebSocketConnection()
        {
            try
            {
                if (_webSocketPublic != null)
                {
                    try
                    {
                        _webSocketPublic.Close();
                    }
                    catch (Exception exception)
                    {
                        SendLogMessage(exception.ToString(), LogMessageType.System);
                    }

                    _webSocketPublic.Opened -= WebSocketPublic_Opened;
                    _webSocketPublic.Closed -= WebSocketPublic_Closed;
                    _webSocketPublic.MessageReceived -= WebSocketPublic_MessageReceived;
                    _webSocketPublic.Error -= WebSocketPublic_Error;
                    _webSocketPublic = null;
                }

                if (_webSocketPrivate != null)
                {
                    try
                    {
                        _webSocketPrivate.Close();
                    }
                    catch (Exception exception)
                    {
                        SendLogMessage(exception.ToString(), LogMessageType.System);
                    }

                    _webSocketPrivate.Opened -= WebSocketPrivate_Opened;
                    _webSocketPrivate.Closed -= WebSocketPrivate_Closed;
                    _webSocketPrivate.MessageReceived -= WebSocketPrivate_MessageReceived;
                    _webSocketPrivate.Error -= WebSocketPrivate_Error;
                    _webSocketPrivate = null;
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.System);
            }
        }
        #endregion

        #region  7 WebSocket events

        private bool _socketPublicIsActive;
        private bool _socketPrivateIsActive;
        private void SendPing(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (_webSocketPublic != null && _webSocketPublic.State == WebSocketState.Open)
                {
                    string pingMessage = "{\"event\":\"ping\", \"cid\":1234}";
                    _webSocketPublic.Send(pingMessage);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPublic_Opened(object sender, EventArgs e)
        {
            Thread.Sleep(2000);

            try
            {
                _socketPublicIsActive = true;//отвечает за соединение

                CheckActivationSockets();

                SendLogMessage("Websocket public Bitfinex Opened", LogMessageType.System);
                // Настраиваем таймер для отправки пинга каждые 30 секунд
                _pingTimer = new Timer(30000); // 30 секунд
                _pingTimer.Elapsed += SendPing;
                _pingTimer.AutoReset = true;
                _pingTimer.Start();
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPublic_Closed(object sender, EventArgs e)
        {
            try
            {
                if (ServerStatus != ServerConnectStatus.Disconnect)
                {
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                }
                // Останавливаем таймер, если он был запущен
                if (_pingTimer != null)
                {
                    _pingTimer.Stop();  // Останавливаем таймер
                    _pingTimer.Elapsed -= SendPing;  // Отписываемся от события Elapsed
                    _pingTimer = null;  // Очищаем таймер
                }
                SendLogMessage("WebSocket Public сlosed by Bitfinex.", LogMessageType.Error);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPublic_Error(object sender, ErrorEventArgs e)
        {
            try
            {
                if (e.Exception != null)
                {
                    SendLogMessage(e.Exception.ToString(), LogMessageType.Error);
                }

                if (e == null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(e.ToString()))
                {
                    return;
                }

                if (_webSocketPublicMessage == null)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                SendLogMessage("Data socket exception" + exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPublic_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (e == null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(e.Message))
                {
                    return;
                }

                if (_webSocketPublicMessage == null)
                {
                    return;
                }

                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }

                _webSocketPublicMessage.Enqueue(e.Message);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        public event Action<MarketDepth> MarketDepthEvent;
        private string GetSymbolByKeyInDepth(int channelId)
        {
            string symbol = "";

            if (_depthDictionary.TryGetValue(channelId, out symbol))
            {
                return symbol;
            }
            return null;
        }

        private DateTime _lastTimeMd = DateTime.MinValue;

        private List<MarketDepth> _marketDepths = new List<MarketDepth>();
        private void SnapshotDepth(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    SendLogMessage("Received empty message", LogMessageType.Error);
                    return;
                }
                List<object> root = JsonConvert.DeserializeObject<List<object>>(message);

                if (root == null || root.Count < 2)
                {
                    SendLogMessage("Invalid root structure", LogMessageType.Error);
                    return;
                }

                int channelId = Convert.ToInt32(root[0]);
                string securityName = GetSymbolByKeyInDepth(channelId);

                List<List<object>> snapshot = JsonConvert.DeserializeObject<List<List<object>>>(root[1].ToString());

                if (snapshot == null || snapshot.Count == 0)
                {
                    SendLogMessage("Snapshot data is empty", LogMessageType.Error);
                    return;
                }
                if (_marketDepths == null)
                {
                    _marketDepths = new List<MarketDepth>();
                }
                var needDepth = _marketDepths.Find(depth =>
                    depth.SecurityNameCode == securityName);

                if (needDepth == null)
                {
                    needDepth = new MarketDepth();
                    needDepth.SecurityNameCode = securityName;
                    _marketDepths.Add(needDepth);
                }

                List<MarketDepthLevel> asks = new List<MarketDepthLevel>();
                List<MarketDepthLevel> bids = new List<MarketDepthLevel>();

                for (int i = 0; i < snapshot.Count; i++)
                {
                    List<object> value = snapshot[i];

                    if (Convert.ToDecimal(value[2]) > 0)
                    {
                        bids.Add(new MarketDepthLevel()
                        {
                            Bid = Convert.ToDecimal(value[2]),
                            Price = Convert.ToDecimal(value[0]),
                        });
                    }
                    else
                    {
                        asks.Add(new MarketDepthLevel()
                        {
                            Ask = Convert.ToDecimal(Math.Abs(Convert.ToDecimal(value[2]))),
                            Price = Convert.ToDecimal(value[0]),
                        });
                    }
                }

                needDepth.Asks = asks;
                needDepth.Bids = bids;

                needDepth.Time = ServerTime;

                if (needDepth.Time < _lastTimeMd)
                {
                    needDepth.Time = _lastTimeMd;
                }
                else if (needDepth.Time == _lastTimeMd)
                {
                    _lastTimeMd = DateTime.FromBinary(_lastTimeMd.Ticks + 1);
                    needDepth.Time = _lastTimeMd;
                }
                _lastTimeMd = needDepth.Time;

                if (MarketDepthEvent != null)
                {
                    MarketDepthEvent(needDepth.GetCopy());
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }
        private void UpdateDepth(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    SendLogMessage("Received empty message", LogMessageType.Error);
                    return;
                }
                List<object> root = JsonConvert.DeserializeObject<List<object>>(message);

                if (root == null || root.Count < 2)
                {
                    SendLogMessage("Invalid root structure", LogMessageType.Error);
                    return;
                }

                List<object> update = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());

                if (update == null || update.Count < 3)
                {
                    SendLogMessage("Invalid update data", LogMessageType.Error);
                    return;
                }

                int channelId = Convert.ToInt32(root[0]);
                string securityName = GetSymbolByKeyInDepth(channelId);

                if (_marketDepths == null)
                {
                    return;
                }
                var needDepth = _marketDepths.Find(depth =>
                    depth.SecurityNameCode == securityName);

                if (needDepth == null)
                {
                    return;
                }

                var price = (update[0]).ToString().ToDecimal();
                var count = (update[1]).ToString().ToDecimal();
                var amount = (update[2]).ToString().ToDecimal();

                needDepth.Time = ServerTime;

                if (needDepth.Time < _lastTimeMd)
                {
                    needDepth.Time = _lastTimeMd;
                }
                else if (needDepth.Time == _lastTimeMd)
                {
                    _lastTimeMd = DateTime.FromBinary(_lastTimeMd.Ticks + 1);
                    needDepth.Time = _lastTimeMd;
                }

                _lastTimeMd = needDepth.Time;

                if (count == 0)
                {
                    if (amount < 0)
                    {
                        needDepth.Asks.Remove(needDepth.Asks.Find(level => level.Price == price));
                    }
                    if (amount > 0)
                    {
                        needDepth.Bids.Remove(needDepth.Bids.Find(level => level.Price == price));
                    }
                    return;
                }

                else if (amount > 0)
                {
                    var needLevel = needDepth.Bids.Find(bid => bid.Price == price);

                    if (needLevel == null)
                    {
                        needDepth.Bids.Add(new MarketDepthLevel()
                        {
                            Bid = amount,
                            Price = price
                        });

                        needDepth.Bids.Sort((level, depthLevel) => level.Price > depthLevel.Price ? -1 : level.Price < depthLevel.Price ? 1 : 0);
                    }
                    else
                    {
                        needLevel.Bid = amount;
                    }
                }
                else if (amount < 0)
                {
                    var needLevel = needDepth.Asks.Find(ask => ask.Price == price);

                    if (needLevel == null)
                    {
                        needDepth.Asks.Add(new MarketDepthLevel()
                        {
                            Ask = Math.Abs(amount),
                            Price = price
                        });

                        needDepth.Asks.Sort((level, depthLevel) => level.Price > depthLevel.Price ? 1 : level.Price < depthLevel.Price ? -1 : 0);
                    }
                    else
                    {
                        needLevel.Ask = Math.Abs(amount);
                    }
                }
                if (needDepth.Asks.Count < 2 ||
                    needDepth.Bids.Count < 2)
                {
                    return;
                }
                if (needDepth.Asks[0].Price > needDepth.Asks[1].Price)
                {
                    needDepth.Asks.RemoveAt(0);
                }
                if (needDepth.Bids[0].Price < needDepth.Bids[1].Price)
                {
                    needDepth.Asks.RemoveAt(0);
                }
                if (needDepth.Asks[0].Price < needDepth.Bids[0].Price)
                {
                    if (needDepth.Asks[0].Price < needDepth.Bids[1].Price)
                    {
                        needDepth.Asks.Remove(needDepth.Asks[0]);
                    }
                    else if (needDepth.Bids[0].Price > needDepth.Asks[1].Price)
                    {
                        needDepth.Bids.Remove(needDepth.Bids[0]);
                    }
                }
                if (MarketDepthEvent != null)
                {
                    MarketDepthEvent(needDepth.GetCopy());
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPrivate_Opened(object sender, EventArgs e)
        {
            GenerateAuthenticate();
            _socketPrivateIsActive = true;//отвечает за соединение
            CheckActivationSockets();
            SendLogMessage("Connection to private data is Open", LogMessageType.System);

            // Настраиваем таймер для отправки пинга каждые 30 секунд
            _pingTimer = new Timer(30000); // 30 секунд
            _pingTimer.Elapsed += SendPing;
            _pingTimer.AutoReset = true;
            _pingTimer.Start();
        }
        private void GenerateAuthenticate()
        {
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string authPayload = "AUTH" + nonce;
            string authSig;
            using (var hmac = new HMACSHA384(Encoding.UTF8.GetBytes(_secretKey)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(authPayload));
                authSig = BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
            var payload = new
            {
                @event = "auth",
                apiKey = _publicKey,
                authSig = authSig,
                authNonce = nonce,
                authPayload = authPayload
            };
            string authJson = JsonConvert.SerializeObject(payload);

            _webSocketPrivate.Send(authJson);

        }
        private void CheckActivationSockets()
        {
            if (_socketPublicIsActive == false)
            {
                return;
            }
            if (_socketPrivateIsActive == false)
            {
                return;
            }
            try
            {
                if (ServerStatus != ServerConnectStatus.Connect &&
                    _webSocketPublic != null && _webSocketPrivate != null &&
                    _webSocketPublic.State == WebSocketState.Open &&
                    _webSocketPrivate.State == WebSocketState.Open)
                {
                    ServerStatus = ServerConnectStatus.Connect;
                    ConnectEvent();
                }

                SendLogMessage("All sockets activated.", LogMessageType.System);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPrivate_Closed(object sender, EventArgs e)
        {
            try
            {
                // _socketPrivateIsActive = false;///добавила


                if (ServerStatus != ServerConnectStatus.Disconnect)
                {
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                }
                if (_pingTimer != null)
                {
                    _pingTimer.Stop();  // Останавливаем таймер
                    _pingTimer.Elapsed -= SendPing;  // Отписываемся от события Elapsed
                    _pingTimer = null;  // Очищаем таймер
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            SendLogMessage("Connection Closed by Bitfinex. WebSocket Private сlosed ", LogMessageType.Error);
        }
        private void WebSocketPrivate_Error(object sender, ErrorEventArgs e)
        {
            try
            {
                if (e.Exception != null)
                {
                    SendLogMessage(e.Exception.ToString(), LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void WebSocketPrivate_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }
                if (e == null || string.IsNullOrEmpty(e.Message))
                {
                    return;
                }
                if (_webSocketPrivateMessage == null)
                {
                    return;
                }

                _webSocketPrivateMessage.Enqueue(e.Message);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        #endregion

        #region  8 Security subscrible 

        private List<Security> _subscribledSecurities = new List<Security>();
        public void Subscrible(Security security)
        {
            try
            {
                CreateSubscribleMessageWebSocket(security);

                Thread.Sleep(200);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void CreateSubscribleMessageWebSocket(Security security)
        {
            try
            {
                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }

                for (int i = 0; i < _subscribledSecurities.Count; i++)
                {
                    if (_subscribledSecurities[i].Name == security.Name &&
                        _subscribledSecurities[i].NameClass == security.NameClass)
                    {
                        return;
                    }
                }

                _subscribledSecurities.Add(security);

                _webSocketPublic.Send($"{{\"event\":\"subscribe\",\"channel\":\"book\",\"symbol\":\"{security.Name}\",\"prec\":\"P0\",\"freq\":\"F0\",\"len\":\"25\"}}");//стакан
                _webSocketPublic.Send($"{{\"event\":\"subscribe\",\"channel\":\"trades\",\"symbol\":\"{security.Name}\"}}"); //трейды                                                                                              
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        #endregion

        #region  9 WebSocket parsing the messages

        public event Action<Trade> NewTradesEvent;
        public event Action<MyTrade> MyTradeEvent;
        public event Action<Order> MyOrderEvent;

        private Dictionary<int, string> _tradeDictionary = new Dictionary<int, string>();
        private Dictionary<int, string> _depthDictionary = new Dictionary<int, string>();

        int currentChannelIdDepth;
        int channelIdTrade;
        private void PublicMessageReader()
        {
            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                    if (_webSocketPublicMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    _webSocketPublicMessage.TryDequeue(out string message);

                    if (message == null)
                    {
                        continue;
                    }

                    else if (message.Contains("event") || message.Contains("hb") || message.Contains("auth"))
                    {
                        //continue;
                    }

                    if (message.Contains("trades"))
                    {
                        BitfinexSubscriptionResponse responseTrade = JsonConvert.DeserializeObject<BitfinexSubscriptionResponse>(message);
                        _tradeDictionary.Add(Convert.ToInt32(responseTrade.ChanId), responseTrade.Symbol);
                        channelIdTrade = Convert.ToInt32(responseTrade.ChanId);
                    }

                    else if (message.Contains("book"))
                    {
                        BitfinexSubscriptionResponse responseDepth = JsonConvert.DeserializeObject<BitfinexSubscriptionResponse>(message);
                        _depthDictionary.Add(Convert.ToInt32(responseDepth.ChanId), responseDepth.Symbol);
                        currentChannelIdDepth = Convert.ToInt32(responseDepth.ChanId);
                    }

                    if (message.Contains("[["))
                    {
                        var root = JsonConvert.DeserializeObject<List<object>>(message);
                        int channelId = Convert.ToInt32(root[0]);
                        if (root == null || root.Count < 2)
                        {
                            SendLogMessage("Некорректный формат сообщения: недостаточно элементов.", LogMessageType.Error);
                            return;
                        }
                        if (channelId == currentChannelIdDepth)
                        {
                            SnapshotDepth(message); // Вызов метода обработки снапшота стакана
                        }
                    }

                    if (!message.Contains("[[") && !message.Contains("te") && !message.Contains("tu") && !message.Contains("ws") && !message.Contains("event") && !message.Contains("hb"))
                    {
                        UpdateDepth(message);
                    }

                    if ((message.Contains("te") || message.Contains("tu")) && channelIdTrade != 0)//\"te\"
                    {
                        UpdateTrade(message);
                    }
                }
                catch (Exception exception)
                {
                    Thread.Sleep(5000);
                    SendLogMessage(exception.ToString(), LogMessageType.Error);
                }
            }
        }

        private string GetSymbolByKeyInTrades(int channelId)
        {
            string symbol = "";

            if (_tradeDictionary.TryGetValue(channelId, out symbol))
            {
                return symbol;
            }
            return null;
        }
        private void UpdateTrade(string message)
        {
            if (message.Contains("tu"))
            {
                return;
            }
            try
            {
                var root = JsonConvert.DeserializeObject<List<object>>(message);

                if (root == null && root.Count < 2)
                {
                    return;
                }
                var tradeData = JsonConvert.DeserializeObject<List<object>>(root[2].ToString());
                int channelId = Convert.ToInt32(root[0]);

                if (tradeData == null && tradeData.Count < 4)
                {
                    return;
                }

                Trade newTrade = new Trade();
                newTrade.SecurityNameCode = GetSymbolByKeyInTrades(channelId);
                newTrade.Id = tradeData[0].ToString();
                newTrade.Price = tradeData[3].ToString().ToDecimal();
                decimal volume = tradeData[2].ToString().ToDecimal();

                if (volume < 0)
                {
                    volume = Math.Abs(volume);
                }
                newTrade.Volume = volume;
                newTrade.Side = volume > 0 ? Side.Buy : Side.Sell;
                newTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(tradeData[1]));

                NewTradesEvent?.Invoke(newTrade);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private void PrivateMessageReader()
        {
            while (true)
            {
                try
                {
                    if (ServerStatus == ServerConnectStatus.Disconnect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (_webSocketPrivateMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    _webSocketPrivateMessage.TryDequeue(out string message);

                    if (message == null)
                    {
                        continue;
                    }

                    if (message.Contains("\"event\":\"info\""))
                    {
                        SendLogMessage("WebSocket opened", LogMessageType.System);
                    }

                    if (message.Contains("\"event\":\"auth\""))
                    {
                        var authResponse = JsonConvert.DeserializeObject<BitfinexAuthResponseWebSocket>(message);

                        if (authResponse.Status == "OK")
                        {
                            SendLogMessage("WebSocket authentication successful", LogMessageType.System);
                        }
                        else
                        {
                            SendLogMessage($"WebSocket authentication error: {authResponse.Msg}", LogMessageType.Error);
                        }
                    }

                    else if (message.StartsWith("[0,\"tu\",["))
                    {
                        UpdateMyTrade(message);
                    }

                    else if
                       (message.StartsWith("[0,\"oc\",[") || message.StartsWith("[0,\"ou\",["))
                    {
                        UpdateOrder(message);
                    }

                    else if (message.StartsWith("[0,\"wu\",["))
                    {
                        UpdatePortfolio(message);
                    }
                }
                catch (Exception exception)
                {
                    Thread.Sleep(5000);
                    SendLogMessage(exception.ToString(), LogMessageType.Error);
                }
            }
        }
        private void UpdateMyTrade(string message)
        {
            try
            {
                List<object> tradyList = JsonConvert.DeserializeObject<List<object>>(message);

                if (tradyList == null || tradyList.Count < 3)
                {
                    return;
                }


                var tradeDataJson = tradyList[2]?.ToString();///посмотреть
                if (string.IsNullOrEmpty(tradeDataJson))
                {
                    return;
                }
                List<object> tradeData = JsonConvert.DeserializeObject<List<object>>(tradeDataJson);

                if (tradeData == null)
                {
                    return;
                }

                MyTrade myTrade = new MyTrade();

                myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(tradeData[2]));
                myTrade.SecurityNameCode = Convert.ToString(tradeData[1]);
                myTrade.NumberOrderParent = (tradeData[3]).ToString();
                myTrade.Price = Convert.ToDecimal(tradeData[7]);
                myTrade.NumberTrade = (tradeData[0]).ToString();
                decimal volume = (tradeData[4]).ToString().ToDecimal();
                myTrade.Side = volume > 0 ? Side.Buy : Side.Sell;

                if (volume < 0)
                {
                    volume = Math.Abs(volume);
                }
                // Расчет объема с учетом комиссии
                decimal preVolume = volume + Math.Abs((tradeData[9]).ToString().ToDecimal());

                //decimal preVolume = myTrade.Side == Side.Sell
                //    ? Convert.ToDecimal(tradeData[4])
                //    : Convert.ToDecimal(tradeData[4]) + Math.Abs(Convert.ToDecimal(tradeData[9]));

                myTrade.Volume = GetVolumeForMyTrade(myTrade.SecurityNameCode, preVolume);

                MyTradeEvent?.Invoke(myTrade);
                SendLogMessage(myTrade.ToString(), LogMessageType.Trade);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        private Dictionary<string, int> _decimalVolume = new Dictionary<string, int>();
        private decimal GetVolumeForMyTrade(string symbol, decimal preVolume)
        {
            int forTruncate = 1;

            Dictionary<string, int>.Enumerator enumerator = _decimalVolume.GetEnumerator();

            while (enumerator.MoveNext())
            {
                string key = enumerator.Current.Key;
                int value = enumerator.Current.Value;

                if (key.Equals(symbol))
                {
                    if (value != 0)
                    {
                        for (int i = 0; i < value; i++)
                        {
                            forTruncate *= 10;
                        }
                    }
                    return Math.Truncate(preVolume * forTruncate) / forTruncate;
                }
            }
            return preVolume;
        }
        private void UpdateOrder(string message)
        {
            try
            {
                List<object> rootArray = JsonConvert.DeserializeObject<List<object>>(message);

                if (rootArray == null)
                {
                    return;
                }

                var orderDataList = JsonConvert.DeserializeObject<List<object>>(rootArray[2].ToString());

                if (orderDataList == null)
                {
                    return;
                }

                Order updateOrder = new Order();
                updateOrder.SecurityNameCode = (orderDataList[3]).ToString(); // SYMBOL
                updateOrder.SecurityClassCode = orderDataList[3].ToString().StartsWith("f") ? "Futures" : "CurrencyPair";
                updateOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderDataList[4])); // MTS_CREATE
                updateOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderDataList[5])); // MTS_UPDATE                        
                updateOrder.NumberUser = Convert.ToInt32(orderDataList[2]); // CID
                updateOrder.NumberMarket = (orderDataList[0]).ToString(); // ID
                updateOrder.Side = (orderDataList[7]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell; // SIDE  должен быть buy
                updateOrder.State = GetOrderState((orderDataList[13]).ToString()); // STATUS//Done
                string typeOrder = (orderDataList[8]).ToString();

                if (typeOrder == "EXCHANGE LIMIT")
                {
                    updateOrder.TypeOrder = OrderPriceType.Limit;
                }
                else
                {
                    updateOrder.TypeOrder = OrderPriceType.Market;
                }

                updateOrder.Price = (orderDataList[16]).ToString().ToDecimal(); // PRICE
                updateOrder.ServerType = ServerType.Bitfinex;
                decimal volume = (orderDataList[7]).ToString().ToDecimal();
                if (volume < 0)
                {
                    volume = Math.Abs(volume);
                }
                updateOrder.VolumeExecute = volume;// AMOUNT
                updateOrder.Volume = volume;
                updateOrder.PortfolioNumber = "BitfinexPortfolio";

                SendLogMessage($"Order updated: {updateOrder.NumberMarket}, Status: {updateOrder.State}", LogMessageType.Trade);

                SendLogMessage($"UpdateOrder message: {message} volume {updateOrder.Volume}, volumeExecute {updateOrder.VolumeExecute}", LogMessageType.Trade);
                SendLogMessage($"Parsed order state: {updateOrder.State}", LogMessageType.Trade);

                if (updateOrder.State == OrderStateType.Done || updateOrder.State == OrderStateType.Partial)
                {
                    updateOrder.TimeDone = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderDataList[5])); // MTS_UPDATE  
                    updateOrder.State = OrderStateType.Done;

                    List<MyTrade> myTrades = GetMyTradesBySecurity(updateOrder.SecurityNameCode, updateOrder.NumberMarket);
                }

                if (updateOrder.State == OrderStateType.Active)
                {
                    Order orderFromActive = GetActiveOrder(updateOrder.NumberMarket);

                    if (orderFromActive != null)
                    {
                        updateOrder = orderFromActive;
                        SendLogMessage($"Order updated from history: {orderFromActive.NumberMarket}, Status: {orderFromActive.State}", LogMessageType.Trade);
                    }
                }

                if (updateOrder.State == OrderStateType.Cancel)
                {
                    updateOrder.TimeCancel = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderDataList[5]));
                    updateOrder.State = OrderStateType.Cancel;

                    SendLogMessage($"Order canceled Successfully. Order ID:{updateOrder.NumberMarket}", LogMessageType.Trade);
                    SendLogMessage($"Parsed order state: {updateOrder.State}", LogMessageType.Trade);
                }

                MyOrderEvent?.Invoke(updateOrder);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private OrderStateType GetOrderState(string orderStateResponse)
        {
            if (orderStateResponse.StartsWith("ACTIVE"))
            {
                return OrderStateType.Active;
            }
            else if (orderStateResponse.StartsWith("EXECUTED"))
            {
                return OrderStateType.Done;
            }
            else if (orderStateResponse.StartsWith("PARTIALLY FILLED"))
            {
                return OrderStateType.Partial;
            }
            else if (orderStateResponse.StartsWith("CANCELED"))
            {
                return OrderStateType.Cancel;
            }

            return OrderStateType.None;
        }

        #endregion

        #region  10 Trade

        private RateGate _rateGateSendOrder = new RateGate(90, TimeSpan.FromMinutes(1));
        private RateGate _rateGateCancelOrder = new RateGate(90, TimeSpan.FromMinutes(1));
        private RateGate _rateGateCancelAllOrder = new RateGate(90, TimeSpan.FromMinutes(1));
        private RateGate _rateGateChangePriceOrder = new RateGate(90, TimeSpan.FromMinutes(1));
        public void SendOrder(Order order)
        {
            _rateGateSendOrder.WaitToProceed();

            string nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string _apiPath = "v2/auth/w/order/submit";

            BitfinexOrderData newOrder = new BitfinexOrderData();
            newOrder.Cid = order.NumberUser.ToString();
            newOrder.Symbol = order.SecurityNameCode;
            order.PortfolioNumber = "BitfinexPortfolio";
            if (order.TypeOrder == OrderPriceType.Limit)
            {
                newOrder.OrderType = "EXCHANGE LIMIT";
            }
            else
            {
                newOrder.OrderType = "EXCHANGE MARKET";
            }
            // newOrder.OrderType = order.TypeOrder.ToString() == "Limit" ? "EXCHANGE LIMIT" : "EXCHANGE MARKET";
            newOrder.Price = order.TypeOrder == OrderPriceType.Market ? null : order.Price.ToString().Replace(",", ".");
            if (order.Side.ToString() == "Sell")
            {
                //newOrder.Amount = (-order.Volume).ToString(CultureInfo.InvariantCulture);
                newOrder.Amount = "-" + (order.Volume).ToString().Replace(",", ".");
            }
            else
            {
                //newOrder.Amount = order.Volume.ToString(CultureInfo.InvariantCulture);
                newOrder.Amount = (order.Volume).ToString().Replace(",", ".");
            }
            string body = $"{{\"type\":\"{newOrder.OrderType}\",\"symbol\":\"{newOrder.Symbol}\",\"amount\":\"{newOrder.Amount}\",\"price\":\"{newOrder.Price}\",\"cid\":{newOrder.Cid}}}";
            //var body = $"{{\"type\":\"{newOrder.OrderType}\"," +
            //      $"\"symbol\":\"{newOrder.Symbol}\"," +
            //      $"\"amount\":\"{newOrder.Amount}\"," +
            //      $"\"price\":\"{newOrder.Price}\"," +
            //      $"\"cid\":{newOrder.Cid}}}";

            string signature = $"/api/{_apiPath}{nonce}{body}";

            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var response = client.Execute(request);

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;

                    List<object> responseArray = JsonConvert.DeserializeObject<List<object>>(responseBody);

                    if (responseArray == null)
                    {
                        return;
                    }

                    string dataJson = responseArray[4].ToString();
                    List<List<object>> ordersArray = JsonConvert.DeserializeObject<List<List<object>>>(dataJson);

                    if (ordersArray == null)
                    {
                        return;
                    }

                    //  _osOrders.Add(newCreatedOrder.id.ToString(), order.NumberUser);

                    List<object> orders = ordersArray[0]; // Получаем первый заказ из массива
                    string status = orders[13].ToString();
                    string numUser = (orders[2]).ToString();

                    if (orders[0] != null)// if(numberUser==cid)
                    {
                        Order newOsOrder = new Order();

                        newOsOrder.SecurityNameCode = (orders[3]).ToString();
                        newOsOrder.NumberMarket = orders[0].ToString();//190240109337
                        newOsOrder.NumberUser = Convert.ToInt32(orders[2]);
                        newOsOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orders[4]));
                        //newOsOrder.State = GetOrderState(orders[13].ToString());
                        newOsOrder.State = OrderStateType.Active;
                        newOsOrder.SecurityClassCode = orders[3].ToString().StartsWith("f") ? "Futures" : "CurrencyPair";
                        newOsOrder.Side = (orders[6]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell;
                        newOsOrder.ServerType = ServerType.Bitfinex;
                        newOsOrder.Price = (orders[16]).ToString().ToDecimal();
                        newOsOrder.PortfolioNumber = "BitfinexPortfolio";
                        decimal volume = (orders[7]).ToString().ToDecimal();
                        //if (volume < 0)
                        //{
                        //    volume = Math.Abs(volume);
                        //}
                        newOsOrder.Volume = volume;

                        SendLogMessage($"Order number {newOsOrder.NumberMarket} on exchange.", LogMessageType.Trade);

                        string typeOrder = (orders[8]).ToString();
                        if (typeOrder == "EXCHANGE LIMIT")
                        {
                            newOsOrder.TypeOrder = OrderPriceType.Limit;
                        }
                        else
                        {
                            newOsOrder.TypeOrder = OrderPriceType.Market;
                        }
                        if (MyOrderEvent != null)
                        {
                            MyOrderEvent(newOsOrder);
                        }

                        GetPortfolios();
                    }
                }
                else
                {
                    CreateOrderFail(order);
                    SendLogMessage($"Error Order exception {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                CreateOrderFail(order);
                SendLogMessage("Order send exception " + exception.ToString(), LogMessageType.Error);
            }
        }
        private void CreateOrderFail(Order order)
        {
            order.State = OrderStateType.Fail;
            MyOrderEvent?.Invoke(order);
        }
        public void CancelAllOrders()
        {
            _rateGateCancelAllOrder.WaitToProceed();

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/w/order/cancel/multi";

            string body = $"{{\"all\":1}}";

            string signature = $"/api/{_apiPath}{nonce}{body}";
            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            IRestResponse response = client.Execute(request);

            if (response == null)
            {
                return;
            }
            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    List<object> responseJson = JsonConvert.DeserializeObject<List<object>>(response.Content);

                    if (responseJson.Contains("oc_multi-req"))
                    {
                        SendLogMessage($"All active orders canceled: {response.Content}", LogMessageType.Trade);

                        GetPortfolios();
                    }

                    else
                    {
                        SendLogMessage($" {response.Content}", LogMessageType.Error);
                    }
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        public void CancelOrder(Order order)
        {
            _rateGateCancelOrder.WaitToProceed();

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/w/order/cancel";

            if (order.State == OrderStateType.Cancel)
            {
                return;
            }

            string body = $"{{\"id\":{order.NumberMarket}}}";

            string signature = $"/api/{_apiPath}{nonce}{body}";
            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            IRestResponse response = client.Execute(request);

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;

                    List<object> responseJson = JsonConvert.DeserializeObject<List<object>>(responseBody);

                    SendLogMessage($"Order canceled Successfully. Order ID:{order.NumberMarket}", LogMessageType.Trade);

                    order.State = OrderStateType.Cancel;

                    MyOrderEvent(order);

                    GetPortfolios();
                }
                else
                {
                    CreateOrderFail(order);
                    SendLogMessage($" Error Order cancellation:  {response.Content}, {response.ErrorMessage}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                CreateOrderFail(order);
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private string ComputeHmacSha384(string apiSecret, string signature)
        {
            using HMACSHA384 hmac = new HMACSHA384(Encoding.UTF8.GetBytes(apiSecret));
            byte[] output = hmac.ComputeHash(Encoding.UTF8.GetBytes(signature));
            return BitConverter.ToString(output).Replace("-", "").ToLower();
        }
        public void ChangeOrderPrice(Order order, decimal newPrice)
        {
            _rateGateChangePriceOrder.WaitToProceed();

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string price = newPrice.ToString().Replace(',', '.');

            try
            {
                if (order.TypeOrder == OrderPriceType.Market)
                {
                    SendLogMessage("Can't change price for  Order Market", LogMessageType.Error);
                    return;
                }

                string _apiPath = "v2/auth/w/order/update";
                string body = $"{{\"id\":{order.NumberMarket},\"price\":\"{price}\"}}";

                string signature = $"/api/{_apiPath}{nonce}{body}";
                var client = new RestClient(_baseUrl);
                var request = new RestRequest(_apiPath, Method.POST);
                string sig = ComputeHmacSha384(_secretKey, signature);

                request.AddHeader("accept", "application/json");
                request.AddHeader("bfx-nonce", nonce);
                request.AddHeader("bfx-apikey", _publicKey);
                request.AddHeader("bfx-signature", sig);
                request.AddParameter("application/json", body, ParameterType.RequestBody);

                IRestResponse response = client.Execute(request);

                // decimal qty = (order.Volume - order.VolumeExecute);

                //if (qty <= 0 || order.State != OrderStateType.Active)
                //{
                //    SendLogMessage("Can't change price for the order. It is not in Active state", LogMessageType.Error);
                //    return;
                //}
                if (order.State == OrderStateType.Cancel)//если ордер активный можно снять
                {
                    return;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;
                    var responseArray = JsonConvert.DeserializeObject<List<object>>(responseBody);
                    var orderDataArray = JsonConvert.DeserializeObject<List<object>>(responseArray[4].ToString());
                    order.Price = orderDataArray[16].ToString().ToDecimal();
                    order.State = GetOrderState(orderDataArray[13].ToString()); // раскоментировать

                    SendLogMessage("Order change price. New price: " + order.Price
                      + "  " + order.SecurityNameCode, LogMessageType.Trade);//LogMessageType.System
                }
                else
                {
                    SendLogMessage("Change price order Fail. Status: "
                    + response.Content + "  " + order.SecurityNameCode, LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        #endregion

        #region  11 Queries

        public void CancelAllOrdersToSecurity(Security security)
        {
            throw new NotImplementedException();
        }
        public List<Order> GetAllActiveOrders()
        {
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

            List<Order> orders = new List<Order>();
            string _apiPath = "v2/auth/r/orders";
            string signature = $"/api/{_apiPath}{nonce}";

            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);

            IRestResponse response = client.Execute(request);

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;// пустой массив

                    if (string.IsNullOrEmpty(responseBody) || responseBody == "[]")
                    {
                        SendLogMessage("No active orders found.", LogMessageType.Trade);
                        return null;
                    }

                    List<List<object>> listOrders = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    for (int i = 0; i < listOrders.Count; i++)
                    {
                        var orderData = listOrders[i];

                        Order activOrder = new Order();

                        activOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        activOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                        activOrder.ServerType = ServerType.Bitfinex;
                        activOrder.SecurityNameCode = orderData[3].ToString();
                        activOrder.SecurityClassCode = orderData[3].ToString().StartsWith("f") ? "Futures" : "CurrencyPair";
                        activOrder.NumberUser = Convert.ToInt32(orderData[2]);//
                        activOrder.NumberMarket = orderData[0].ToString();
                        activOrder.Side = (orderData[7]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell; // SIDE     orderData[6].Equals("-") ? Side.Sell : Side.Buy;
                        activOrder.State = GetOrderState(orderData[13].ToString());
                        decimal volume = orderData[7].ToString().ToDecimal();
                        if (volume < 0)
                        {
                            volume = Math.Abs(volume);
                        }
                        activOrder.Volume = volume;
                        activOrder.Price = orderData[16].ToString().ToDecimal();
                        activOrder.PortfolioNumber = "BitfinexPortfolio";

                        orders.Add(activOrder);

                        MyOrderEvent?.Invoke(activOrder);
                    }
                }

                else
                {
                    SendLogMessage($" Can't get all orders. State Code: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return orders;
        }
        public void GetOrderStatus(Order order)
        {
            if (order == null || order.NumberUser == 0)
            {
                SendLogMessage("Order or CID is null/zero.", LogMessageType.Error);
                return;
            }

            //List<Order> ordersactive = GetAllActiveOrders();
            // Переменная для хранения ордера с рынка


            //// Проверяем активные ордера
            //if (activeOrdersByCid.TryGetValue(order.NumberUser, out var activeOrder))
            //{
            //    SendLogMessage($"Order found in active orders: {activeOrder.NumberMarket}{order.NumberUser}", LogMessageType.Trade);
            //    orderOnMarket = activeOrder;
            //}

            // Получаем историю ордеров
            //List<Order> ordersHistory = GetHistoryOrders();

            //var  ordersHistory = GetHistoryOrders();
            ////int o2 = (order.NumberUser);

            //List<object> findOrder1 = ordersHistory.FirstOrDefault(o => Convert.ToInt32(ordersHistory[2]) == order.NumberUser);
            //List<Order> orders = new List<Order>();
            // order.NumberUser = findOrder1.ordersHistory[2];
            // List<Order> findOrder= findOrder1.


            try
            {

                Order orderOnMarket = null;
                List<Order> ordersHistory = GetHistoryOrders();

                List<List<object>> orders = GetHistoryOrders1();

                if (orderOnMarket == null)
                {
                    SendLogMessage($"Failed to find order with numberUser.", LogMessageType.Error);
                    return;
                }

                // orderOnMarket = ordersHistory;

                if (orders == null)
                {
                    Console.WriteLine("Failed to retrieve order history.");
                    return;
                }

                // Поиск ордера с указанным CID
                var orderData = orders.FirstOrDefault(o => Convert.ToInt64(o[2]) == order.NumberUser);

                if (orderData != null)
                {
                    // Десериализация данных в объект Order
                    Order myOrder = new Order();

                    myOrder.NumberUser = Convert.ToInt32(orderData[2]);
                    myOrder.NumberMarket = orderData[0]?.ToString();
                    myOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                    myOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                    myOrder.ServerType = ServerType.Bitfinex;
                    myOrder.SecurityNameCode = orderData[3]?.ToString();
                    myOrder.SecurityClassCode = myOrder.SecurityNameCode.StartsWith("f") ? "Futures" : "CurrencyPair";
                    myOrder.Side = orderData[7]?.ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell;
                    myOrder.State = GetOrderState(orderData[13]?.ToString());
                    string typeOrder = orderData[8].ToString();
                    if (typeOrder == "EXCHANGE LIMIT")
                    {
                        myOrder.TypeOrder = OrderPriceType.Limit;
                    }
                    else
                    {
                        myOrder.TypeOrder = OrderPriceType.Market;
                    }

                    decimal volume = orderData[7]?.ToString().ToDecimal() ?? 0;

                    if (volume < 0)
                    {
                        volume = Math.Abs(volume);
                    }

                    myOrder.Price = orderData[16]?.ToString().ToDecimal() ?? 0;
                    myOrder.PortfolioNumber = "BitfinexPortfolio";
                    myOrder.VolumeExecute = volume;
                    myOrder.Volume = volume;

                    orderOnMarket = myOrder;
                }
                else
                {
                    Console.WriteLine("The order with NumberUser was not found.");
                }

                if (orderOnMarket.State == OrderStateType.Active)
                {
                    orderOnMarket.State = OrderStateType.Active;
                }
                if (orderOnMarket.State == OrderStateType.Cancel)
                {
                    orderOnMarket.State = OrderStateType.Cancel;
                }

                if (orderOnMarket.State == OrderStateType.Done || orderOnMarket.State == OrderStateType.Partial)
                {
                    List<MyTrade> tradesBySecurity = GetMyTradesBySecurity(orderOnMarket.SecurityNameCode, orderOnMarket.NumberMarket);//trades =""

                    if (tradesBySecurity != null)
                    {
                        List<MyTrade> tradesByMyOrder = new List<MyTrade>();

                        for (int i = 0; i < tradesBySecurity.Count; i++)
                        {
                            if (tradesBySecurity[i].NumberOrderParent == orderOnMarket.NumberMarket)
                            {
                                tradesByMyOrder.Add(tradesBySecurity[i]);
                            }
                        }

                        for (int i = 0; i < tradesByMyOrder.Count; i++)
                        {
                            MyTradeEvent?.Invoke(tradesByMyOrder[i]);
                        }
                    }
                }

                MyOrderEvent?.Invoke(orderOnMarket);
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }
        private Order GetActiveOrder(string id)
        {
            if (id == "")
            {
                return null;
            }

            long orderId = Convert.ToInt64(id);

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

            List<Order> orders = new List<Order>();
            string body = $"{{\"id\":[{orderId}]}}";

            string _apiPath = "v2/auth/r/orders";
            string signature = $"/api/{_apiPath}{nonce}{body}";

            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            IRestResponse response = client.Execute(request);

            Order activOrder = new Order();
            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;// пустой массив
                    if (responseBody.Contains("[]"))
                    {
                        SendLogMessage("Don't have active orders", LogMessageType.Trade);
                        return null;
                    }

                    List<List<object>> listOrders = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    // List<BitfinexOrderData> activeOrders = new List<BitfinexOrderData>();

                    //  if (orders != null && orders.Count > 0)
                    if (listOrders == null)
                    {
                        return null;
                    }

                    for (int i = 0; i < listOrders.Count; i++)
                    {
                        var orderData = listOrders[i];

                        activOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        activOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                        activOrder.ServerType = ServerType.Bitfinex;
                        activOrder.SecurityNameCode = orderData[3].ToString();
                        activOrder.SecurityClassCode = orderData[3].ToString().StartsWith("f") ? "Futures" : "CurrencyPair";
                        activOrder.NumberUser = Convert.ToInt32(orderData[2]);
                        activOrder.NumberMarket = orderData[0].ToString();
                        activOrder.Side = (orderData[7]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell; // SIDE activeOrders[i].Amount//orderData[6].Equals("-") ? Side.Sell : Side.Buy;
                        activOrder.State = GetOrderState(orderData[13].ToString());
                        activOrder.Volume = orderData[7].ToString().ToDecimal();/////
                        activOrder.Price = orderData[16].ToString().ToDecimal();
                        activOrder.PortfolioNumber = "BitfinexPortfolio";

                        orders.Add(activOrder);

                        MyOrderEvent?.Invoke(orders[i]);
                    }
                }
                else
                {
                    SendLogMessage($" Can't get all orders. State Code: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return activOrder;
        }
        public void GetAllActivOrders()
        {
            List<Order> orders = GetAllActiveOrders();

            if (orders == null || orders.Count == 0)
            {
                SendLogMessage("No active orders found.", LogMessageType.Trade);
                return;
            }
            for (int i = 0; i < orders.Count; i++)
            {
                orders[i].TimeCallBack = orders[i].TimeCallBack;
                orders[i].State = (orders[i].State);

                MyOrderEvent?.Invoke(orders[i]);
            }
        }
        private Order GetOrderHistoryById(string orderId)//нет ордер id
        {
            // https://api.bitfinex.com/v2/auth/r/orders/hist
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/r/orders/hist";

            // long orderId = 190717081154;
            //if (orderId == "")
            //{
            //    return null;
            //}

            string body = $"{{\"id\":[{orderId}]}}";


            string signature = $"/api/{_apiPath}{nonce}{body}";

            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            IRestResponse response = client.Execute(request);

            Order historyOrder = new Order();

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    //[[184206213491,null,1732536416875,"tTRXUSD",1732536416876,1732536416876,22,22,"EXCHANGE LIMIT",null,null,null,0,"ACTIVE",null,null,0.0958,0,0,0,null,null,null,0,0,null,null,null,"API>BFX",null,null,{}]]

                    if (data != null && data.Count > 0 && data[0].Count > 0)
                    {
                        List<object> orderData = data[0]; // Берём первый массив данных из массива

                        string numberMarketId = orderData[0]?.ToString();

                        //if (orderId != numberMarketId)
                        //{
                        //    SendLogMessage($"Order ID {orderId} does not match Number Market ID {numberMarketId}. Exiting.", LogMessageType.Error);
                        //    return null;
                        //}
                        historyOrder.NumberUser = Convert.ToInt32(orderData[2]);
                        historyOrder.NumberMarket = orderData[0].ToString();
                        historyOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                        historyOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        historyOrder.ServerType = ServerType.Bitfinex;
                        historyOrder.SecurityClassCode = orderData[3].ToString().StartsWith("f") ? "Futures" : "CurrencyPair";
                        historyOrder.SecurityNameCode = orderData[3].ToString();
                        historyOrder.Side = (orderData[7]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell;
                        historyOrder.State = GetOrderState(orderData[13].ToString());
                        decimal volume = orderData[7].ToString().ToDecimal();

                        if (volume < 0)
                        {
                            volume = Math.Abs(volume);
                        }
                        historyOrder.Volume = volume;
                        historyOrder.NumberUser = Convert.ToInt32(orderData[2]);
                        historyOrder.Price = orderData[16].ToString().ToDecimal();
                        historyOrder.PortfolioNumber = "BitfinexPortfolio";
                        historyOrder.VolumeExecute = volume;

                        //5555555555555555
                        //if (newOrder.State == OrderStateType.Done ||
                        //    newOrder.State == OrderStateType.Partial)
                        //{
                        //    List<MyTrade> tradesBySecurity = GetMyTradesBySecurity(newOrder.SecurityNameCode, newOrder.NumberMarket);
                        //    newOrder.TimeDone = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        //}

                        //if (newOrder.State == OrderStateType.Cancel)
                        //{
                        //    newOrder.TimeCancel = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        //}
                    }
                }
                else
                {
                    SendLogMessage($"GetOrderState. Http State Code: {response.Content}", LogMessageType.Error);
                }
            }

            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
            return historyOrder;

        }
        public List<List<object>> GetHistoryOrders1()
        {
            // https://api.bitfinex.com/v2/auth/r/orders/hist
            string nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string _apiPath = "v2/auth/r/orders/hist";

            string signature = $"/api/{_apiPath}{nonce}";
            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);

            IRestResponse response = client.Execute(request);
            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
            return null;
        }
        public List<Order> GetHistoryOrders()
        {
            string nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string _apiPath = "v2/auth/r/orders/hist";

            string signature = $"/api/{_apiPath}{nonce}";
            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPath, Method.POST);
            string sig = ComputeHmacSha384(_secretKey, signature);

            request.AddHeader("accept", "application/json");
            request.AddHeader("bfx-nonce", nonce);
            request.AddHeader("bfx-apikey", _publicKey);
            request.AddHeader("bfx-signature", sig);

            IRestResponse response = client.Execute(request);

            List<Order> ordersHistory = new List<Order>();

            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    if (data != null && data.Count > 0)
                    {
                        for (int i = 0; i < data.Count; i++)
                        {
                            var orderData = data[i];

                            if (orderData != null && orderData.Count > 0)
                            {
                                Order myOrder = new Order();

                                if (int.TryParse(orderData[2]?.ToString(), out int number)) // Преобразуем значение в строку перед TryParse
                                {
                                    myOrder.NumberUser = Convert.ToInt32(number);
                                }
                                //myOrder.NumberUser=Convert.ToInt32(orderData[2]);
                                myOrder.NumberMarket = orderData[0]?.ToString();
                                myOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                                myOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                                myOrder.ServerType = ServerType.Bitfinex;
                                myOrder.SecurityNameCode = orderData[3]?.ToString();
                                myOrder.SecurityClassCode = myOrder.SecurityNameCode.StartsWith("f") ? "Futures" : "CurrencyPair";
                                myOrder.Side = orderData[7]?.ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell;
                                myOrder.State = GetOrderState(orderData[13]?.ToString());
                                string typeOrder = orderData[8].ToString();
                                if (typeOrder == "EXCHANGE LIMIT")
                                {
                                    myOrder.TypeOrder = OrderPriceType.Limit;
                                }
                                else
                                {
                                    myOrder.TypeOrder = OrderPriceType.Market;
                                }

                                decimal volume = orderData[7]?.ToString().ToDecimal() ?? 0;

                                if (volume < 0)
                                {
                                    volume = Math.Abs(volume);
                                }

                                myOrder.Price = orderData[16]?.ToString().ToDecimal() ?? 0;
                                myOrder.PortfolioNumber = "BitfinexPortfolio";
                                myOrder.VolumeExecute = volume;
                                myOrder.Volume = volume;

                                ordersHistory.Add(myOrder);

                                //MyOrderEvent(myOrder);


                                if (myOrder.State == OrderStateType.Done ||
                                    myOrder.State == OrderStateType.Partial)
                                {
                                    GetMyTradesBySecurity(myOrder.SecurityNameCode, myOrder.NumberMarket);
                                }

                            }
                        }
                    }
                }
                else
                {
                    SendLogMessage($"GetOrderState. Http State Code: {response.StatusCode}, Content: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return ordersHistory;
        }
        private List<MyTrade> GetMyTradesBySecurity(string symbol, string orderId)
        {
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

            List<MyTrade> trades = new List<MyTrade>();
            try
            {
                string _apiPath = $"v2/auth/r/order/{symbol}:{orderId}/trades";
                string signature = $"/api/{_apiPath}{nonce}";

                var client = new RestClient(_baseUrl);
                var request = new RestRequest(_apiPath, Method.POST);
                string sig = ComputeHmacSha384(_secretKey, signature);

                request.AddHeader("accept", "application/json");
                request.AddHeader("bfx-nonce", nonce);
                request.AddHeader("bfx-apikey", _publicKey);
                request.AddHeader("bfx-signature", sig);

                IRestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseBody = response.Content;

                    List<List<object>> tradesData = JsonConvert.DeserializeObject<List<List<object>>>(responseBody);

                    for (int i = 0; i < tradesData.Count; i++)
                    {
                        var tradeData = tradesData[i];
                        MyTrade myTrade = new MyTrade();
                        myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(tradeData[2]));
                        myTrade.SecurityNameCode = Convert.ToString(tradeData[1]);
                        myTrade.NumberTrade = (tradeData[0]).ToString();
                        myTrade.NumberOrderParent = (tradeData[3]).ToString();
                        myTrade.Price = Convert.ToDecimal(tradeData[5]);
                        myTrade.Side = (tradeData[4]).ToString().ToDecimal() > 0 ? Side.Buy : Side.Sell;
                        myTrade.NumberPosition = (tradeData[11]).ToString();
                        decimal volume = (tradeData[4]).ToString().ToDecimal();

                        if (volume < 0)
                        {
                            volume = Math.Abs(volume);
                        }
                        /*555 */
                        decimal preVolume = volume + Math.Abs(Convert.ToDecimal(tradeData[9]));// посмотреть как считается комиссия

                        myTrade.Volume = GetVolumeForMyTrade(myTrade.SecurityNameCode, preVolume);

                        trades.Add(myTrade);

                        //for (int j = 0; j < trades.Count; j++)
                        //{
                        //    MyTradeEvent?.Invoke(trades[j]); // Вызываем событие для каждой сделки
                        //    SendLogMessage(trades[j].ToString(), LogMessageType.Trade); // Логируем информацию о сделке
                        //}

                        MyTradeEvent?.Invoke(myTrade);

                        //SendLogMessage(myTrade.ToString(), LogMessageType.Trade);
                    }
                }
                else
                {
                    SendLogMessage($"Failed to retrieve trades. Status: {response.StatusCode}, Response: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return trades;
        }

        #endregion

        #region 12 Log

        public event Action<string, LogMessageType> LogMessageEvent;
        private void SendLogMessage(string message, LogMessageType messageType)
        {
            LogMessageEvent(message, messageType);
        }

        #endregion
    }
}





