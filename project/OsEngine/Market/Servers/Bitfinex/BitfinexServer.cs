


using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Bitfinex.Json;
using OsEngine.Market.Servers.Entity;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

        //public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        //{
        //    return ((BitfinexServer)ServerRealization).GetCandleHistory(nameSec, tf);
        //}
    }

    public class BitfinexServerRealization : IServerRealization
    {
        #region 1 Constructor, Status, Connection

        public BitfinexServerRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;

            Thread threadForPublicMessages = new Thread(PublicMessageReader)
            {
                IsBackground = true,
                Name = "PublicMessageReaderBitfinex"
            };

            threadForPublicMessages.Start();

            Thread threadForPrivateMessages = new Thread(PrivateMessageReader)
            {
                IsBackground = true,
                Name = "PrivateMessageReaderBitfinex"
            };

            threadForPrivateMessages.Start();
        }

        public DateTime ServerTime { get; set; }

        // string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

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
                RestClient client = new RestClient(_baseUrl);
                string _apiPath = "v2/platform/status";

                RestRequest request = new RestRequest(_apiPath); // Указываем относительный путь для запроса
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
                SendLogMessage("Connection cannot be open. Bitfinex. exception:" + exception.ToString(), LogMessageType.Error);
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


        /// <summary>
        /// настройки коннектора
        /// </summary>
        #region 2 Properties 
        public List<IServerParameter> ServerParameters { get; set; }
        public ServerConnectStatus ServerStatus { get; set; }

        private string _publicKey = "";

        private string _secretKey = "";


        private readonly string _baseUrl = "https://api.bitfinex.com";

        #endregion

        /// <summary>
        /// сделать рейд минутами в соответсвии с сайтом
        /// </summary>
        #region 3 Securities

        private readonly RateGate _rateGateGetsecurity = new RateGate(30, TimeSpan.FromMinutes(1));

        private readonly RateGate _rateGatePositions = new RateGate(90, TimeSpan.FromMinutes(1));

        private readonly List<Security> _securities = new List<Security>();

        public event Action<List<Security>> SecurityEvent;

        public void GetSecurities()
        {
            _rateGateGetsecurity.WaitToProceed();
            RequestMinSizes();
            try
            {
                RestClient client = new RestClient(_baseUrl);
                string _apiPath = "v2/tickers?symbols=ALL";
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
                    SecurityEvent(securities);
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
        // Метод для расчета шага цены на основе количества знаков после запятой или точки
        public double CalculatePriceStep(string price)
        {
            // Проверяем входную строку на пустоту или null
            if (string.IsNullOrWhiteSpace(price))
            {
                throw new ArgumentException("Price cannot be null or empty.");
            }

            int decimalPlaces = 0;

            // Если есть точка, вычисляем количество знаков после нее
            if (price.Contains("."))
            {
                // Разделяем строку на части до и после точки, и измеряем длину части после точки
                decimalPlaces = price.Split('.')[1].Length;
            }
            // Если есть запятая, аналогичная логика
            else if (price.Contains(","))
            {
                decimalPlaces = price.Split(',')[1].Length;
            }

            // Вычисляем шаг цены как 10 в степени -количество_знаков
            return Math.Pow(10, -decimalPlaces);
        }


        private int DigitsAfterComma(string valueNumber)
        {
            int commaPosition = valueNumber.IndexOf(',');
            int digitsAfterComma = valueNumber.Length - commaPosition - 1;
            return digitsAfterComma;
        }
        #region// Функция для конвертации строки в десятичное представление и конвертации строки с научной нотацией

        public static string ConvertScientificNotation(string value)
        {
            double result;

            // Заменяем запятую на точку, если есть
            value = value.Replace(",", ".");

            // Пытаемся преобразовать строку в число типа double
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result))
            {
                // Преобразуем в строку с десятичным представлением с 20 знаками после запятой
                return result.ToString("F20", System.Globalization.CultureInfo.InvariantCulture);
            }

            // Если строка не является числом, возвращаем "0.000000000000000"
            return "0.00000000"; // Или любое другое значение по умолчанию
        }
        #endregion

        #region минимальный размер торговли
        public Dictionary<string, decimal> minSizes = new Dictionary<string, decimal>();
        public void RequestMinSizes()
        {

            string _apiPatch = "/v2/conf/pub:info:pair"; //для спота
            //string _apiPatch = "/v2/conf/pub:info:pair:futures";//для фьючерсов
            var client = new RestClient(_baseUrl);
            var request = new RestRequest(_apiPatch, Method.GET);

            var response = client.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Content))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    //  Dictionary<string, decimal> minSizes = new Dictionary<string, decimal>();

                    // Внешний цикл для списка массивов
                    for (int i = 0; i < data.Count; i++)
                    {
                        var subArray = data[i]; // Получаем подмассив

                        // Внутренний цикл для каждого элемента подмассива
                        for (int j = 0; j < subArray.Count; j++)
                        {
                            if (subArray[j] is JArray pairData && pairData.Count > 1)
                            {
                                string pair = "t" + pairData[0]?.ToString();

                                if (pairData[1] is JArray limits && limits.Count > 3 && limits[3] != null)
                                {
                                    // Приведение limits[3] к строке
                                    string minSizeString = limits[3].ToString();

                                    // Попытка преобразовать строку в decimal
                                    if (decimal.TryParse(minSizeString, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out decimal minSize))
                                    {
                                        minSizes.Add(pair, minSize);
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
            else
            {
                SendLogMessage($"Error  json is null", LogMessageType.Error);
            }
            // return minSizes;
        }

        public decimal GetMinSize(string symbol)
        {
            // Проверяем наличие символа в словаре
            if (minSizes.TryGetValue(symbol, out decimal minSize))
            {
                return minSize; // Возвращаем минимальный размер
            }

            return 1; // Если символ не найден
        }

        #endregion

        #region // Метод для преобразования строки в decimal с учетом научной нотации
        //private string ConvertScientificNotation(string value)
        //{
        //    // Преобразование строки в decimal с учетом научной нотации
        //    return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result)
        //        ? result.ToString(CultureInfo.InvariantCulture)
        //        : value;

        //}
        #endregion


        private SecurityType GetSecurityType(string type)
        {
            SecurityType _securityType = type.StartsWith("t") ? SecurityType.CurrencyPair : SecurityType.Futures;

            return _securityType;
        }
        #endregion

        /// <summary>
        /// Запрос доступных портфелей у подключения. 
        /// </summary>
        #region 4 Portfolios

        private readonly List<Portfolio> _portfolios = new List<Portfolio>();

        public event Action<List<Portfolio>> PortfolioEvent;

        private readonly RateGate _rateGatePortfolio = new RateGate(90, TimeSpan.FromMinutes(1));


        public void GetPortfolios()
        {
            if (_portfolios.Count != 0)
            {
                PortfolioEvent?.Invoke(_portfolios);
            }

            CreateQueryPortfolio();
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
                    List<List<object>> wallets = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    Portfolio portfolio = new Portfolio();

                    portfolio.Number = "BitfinexPortfolio";
                    portfolio.ValueBegin = wallets[0][2].ToString().ToDecimal();
                    portfolio.ValueCurrent = wallets[0][4].ToString().ToDecimal();


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
                            position.ValueBlocked = wallet[2].ToString().ToDecimal() - wallet[4].ToString().ToDecimal();

                            portfolio.SetNewPosition(position);
                        }
                    }


                    PortfolioEvent(new List<Portfolio> { portfolio });

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

        private void CreateQueryPosition()
        {
            _rateGatePositions.WaitToProceed();

            try
            {
                string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
                string _apiPath = "v2/auth/r/orders";
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
                    string responseBody = response.Content;

                    UpdatePosition(responseBody); // Обновляем позиции  приходит пустой массив, если нет позиций
                }
                else
                {
                    SendLogMessage($"Create Query Position: {response.Content}", LogMessageType.Error);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

        }

        private void UpdatePosition(string json)
        {

            List<List<object>> response = JsonConvert.DeserializeObject<List<List<object>>>(json);

            Portfolio portfolio = new Portfolio();

            portfolio.Number = "BitfinexPortfolio";
            portfolio.ValueBegin = 1;
            portfolio.ValueCurrent = 1;


            for (int i = 0; i < response.Count; i++)
            {
                List<object> position = response[i];

                PositionOnBoard pos = new PositionOnBoard();

                pos.PortfolioName = "BitfinexPortfolio";
                pos.SecurityNameCode = position[3].ToString();
                pos.ValueBegin = position[7].ToString().ToDecimal();
                pos.ValueBlocked = position[7].ToString().ToDecimal() - position[6].ToString().ToDecimal();
                pos.ValueCurrent = position[6].ToString().ToDecimal();

                portfolio.SetNewPosition(pos);
            }

            PortfolioEvent(new List<Portfolio> { portfolio });
        }


        #endregion

        /// <summary>
        /// Запросы данных по свечкам и трейдам. 
        /// </summary>
        #region 5 Data Candles

        private readonly RateGate _rateGateCandleHistory = new RateGate(30, TimeSpan.FromMinutes(1));

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }
        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            DateTime startTime = DateTime.UtcNow - TimeSpan.FromMinutes(timeFrameBuilder.TimeFrameTimeSpan.Minutes * candleCount);
            DateTime endTime = DateTime.UtcNow;
            DateTime actualTime = startTime;

            return GetCandleDataToSecurity(security, timeFrameBuilder, startTime, endTime, actualTime);

        }

        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf, long CountToLoad, DateTime endTime)
        {


            long needToLoadCandles = CountToLoad;

            List<Candle> candles = new List<Candle>();
            DateTime fromTime = endTime - TimeSpan.FromMinutes(tf.TotalMinutes * CountToLoad);//перепрыгивает на сутки назад

            do
            {

                // ограничение Bitfinex: For each query, the system would return at most 1500 pieces of data. To obtain more data, please page the data by time.
                int maxCandleCountToLoad = 10000;
                long limit = Math.Min(needToLoadCandles, maxCandleCountToLoad);

                List<Candle> rangeCandles = new List<Candle>(); //////не нужен новый список 

                rangeCandles = CreateQueryCandles(nameSec, GetStringInterval(tf), fromTime, endTime);

                if (rangeCandles == null)
                {
                    return null;
                }

                rangeCandles.Reverse();

                candles.InsertRange(0, rangeCandles);

                if (candles.Count != 0)
                {
                    endTime = candles[0].TimeStart;////
                }

                needToLoadCandles -= limit;

            }

            while (needToLoadCandles > 0);

            return candles;
        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)/////string
        {
            //if (endTime > DateTime.Now ||
            //   startTime >= endTime ||
            //   startTime >= DateTime.Now ||
            //   actualTime > endTime ||
            //   actualTime > DateTime.Now)
            //{
            //    return null;
            //}
            //if (startTime != actualTime)
            //{
            //    startTime = actualTime;
            //}

            //if (!CheckTime(startTime, endTime, actualTime))
            //{
            //    return null;
            //}

            int tfTotalMinutes = (int)timeFrameBuilder.TimeFrameTimeSpan.TotalMinutes;// надо или нет

            if (!CheckTf(tfTotalMinutes))
            {
                return null;
            }
            //if (timeFrameBuilder.TimeFrame != TimeFrame.Min1
            //    || timeFrameBuilder.TimeFrame != TimeFrame.Min5 
            //    || timeFrameBuilder.TimeFrame != TimeFrame.Min15
            //     || timeFrameBuilder.TimeFrame != TimeFrame.Min30 
            //     || timeFrameBuilder.TimeFrame != TimeFrame.Hour1 
            //     || timeFrameBuilder.TimeFrame != TimeFrame.Day)
            //{
            //    return null;
            //}


            if (actualTime < startTime)
            {
                startTime = actualTime;
            }

            if (endTime > DateTime.UtcNow)
            {
                endTime = DateTime.UtcNow;
            }

            long countNeedToLoad = GetCountCandlesFromSliceTime(startTime, endTime, timeFrameBuilder.TimeFrameTimeSpan);

            return GetCandleHistory(security.NameFull, timeFrameBuilder.TimeFrameTimeSpan, countNeedToLoad, endTime);
        }

        private string GetStringInterval(TimeSpan tf)
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

        private List<Candle> CreateQueryCandles(string nameSec, string tf, DateTime timeFrom, DateTime timeTo)
        {
            _rateGateCandleHistory.WaitToProceed();


            DateTime yearBegin = new DateTime(1970, 1, 1);

            string startTime = Convert.ToInt64((timeFrom - yearBegin).TotalMilliseconds).ToString();
            string endTime = Convert.ToInt64((timeTo - yearBegin).TotalMilliseconds).ToString();


            string limit = "10000";

            string _apiPath = $"/v2/candles/trade:{tf}:{nameSec}/hist?start={startTime}&end={endTime}&limit={limit}";

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

                        if (candles[i] == null || candles.Count < 6)
                        {
                            SendLogMessage("Candle data is incomplete", LogMessageType.Error);
                            continue;
                        }

                        if (candleData == null)
                        //if (candleData[0] == null || candleData[1] == null ||
                        //    candleData[2] == null ||  candleData[3] == null ||
                        //    candleData[4] == null || candleData[5] == null)

                        {
                            SendLogMessage("Candle data contains null values", LogMessageType.Error);

                            continue;
                        }
                        //// .ToString().ToDecimal()
                        //if (Convert.ToDecimal(candleData[1]) == 0 ||
                        //    Convert.ToDecimal(candleData[2]) == 0 ||
                        //    Convert.ToDecimal(candleData[3]) == 0 ||
                        //    Convert.ToDecimal(candleData[4]) == 0 ||
                        //    Convert.ToDecimal(candleData[5]) == 0)
                        //{
                        //    SendLogMessage("Candle data contains zero values", LogMessageType.Error);

                        //    continue;
                        //}

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
                    catch (FormatException exception)
                    {
                        SendLogMessage($"Format exception: {exception.Message}", LogMessageType.Error);
                    }
                }
                candles.Reverse();
                return candles;

            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
                return null;
            }
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
        private long GetCountCandlesFromSliceTime(DateTime startTime, DateTime endTime, TimeSpan tf)
        {

            TimeSpan timeSlice = endTime - startTime;


            if (tf.Days > 0)
            {

                return Convert.ToInt64(timeSlice.TotalDays / tf.TotalDays);
            }
            else if (tf.Hours > 0)
            {

                return Convert.ToInt64(timeSlice.TotalHours / tf.TotalHours);
            }
            else if (tf.Minutes > 0)
            {

                return Convert.ToInt64(timeSlice.TotalMinutes / tf.TotalMinutes);
            }
            else
            {

                SendLogMessage(" Timeframe must be defined in days, hours, or minutes.", LogMessageType.Error);
            }
            return 0;
        }

        #endregion

        /// <summary>
        /// Создание вебсокет соединения. 
        /// </summary>
        #region  6 WebSocket creation
        // ConcurrentQueue<string>;
        private WebSocket _webSocketPublic;
        private WebSocket _webSocketPrivate;

        private readonly string _webSocketPublicUrl = "wss://api-pub.bitfinex.com/ws/2";
        private readonly string _webSocketPrivateUrl = "wss://api.bitfinex.com/ws/2";



        private void CreateWebSocketConnection()
        {
            try
            {
                if (_webSocketPublic != null)
                {
                    return;
                }

                //_socketPublicIsActive = false;
                // _socketPrivateIsActive = false;

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


        /// <summary>
        /// Обработка входящих сообщений от вёбсокета. И что важно в данном конкретном случае, Closed и Opened методы обязательно должны находиться здесь,
        /// </summary>
        #region  7 WebSocket events

        private bool _socketPublicIsActive;

        private bool _socketPrivateIsActive;

        private void WebSocketPublic_Opened(object sender, EventArgs e)
        {
            Thread.Sleep(2000);

            _socketPublicIsActive = true;//отвечает за соединение

            CheckActivationSockets();

            SendLogMessage("Websocket public Bitfinex Opened", LogMessageType.System);

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
                ErrorEventArgs error = e;

                if (error.Exception != null)
                {
                    SendLogMessage(error.Exception.ToString(), LogMessageType.Error);
                }
                if (e == null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(e.ToString()))
                {
                    return;
                }

                if (WebSocketPublicMessage == null)
                {
                    return;
                    //continue;
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

                if (WebSocketPublicMessage == null)
                {
                    return;
                }

                if (ServerStatus == ServerConnectStatus.Disconnect)
                {
                    return;
                }

                WebSocketPublicMessage.Enqueue(e.Message);

            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }


        public event Action<MarketDepth> MarketDepthEvent;


        public string GetSymbolByKeyInDepth(int channelId)
        {
            string symbol = "";

            if (depthDictionary.TryGetValue(channelId, out symbol))
            {
                return symbol;
            }

            return null; // Или любое другое значение по умолчанию
        }


        private DateTime _lastTimeMd = DateTime.MinValue;





        public MarketDepth GetMarketDepthByChannelId(int channelId)
        {
            if (_allDepths.TryGetValue(channelId, out MarketDepth marketDepth))
            {
                return marketDepth; // Возвращаем найденный стакан
            }
            return null; // Или можно вернуть новый MarketDepth, если требуется

        }


        public void UpdateDepth1(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                SendLogMessage("Пустое сообщение в UpdateDepth.", LogMessageType.Error);
                return;
            }

            List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
            if (root == null || root.Count < 2) return;

            int channelId = Convert.ToInt32(root[0]);
            if (!_allDepths.TryGetValue(channelId, out MarketDepth currentMarketDepth))
            {
                currentMarketDepth = new MarketDepth
                {
                    Bids = new List<MarketDepthLevel>(),
                    Asks = new List<MarketDepthLevel>(),
                    SecurityNameCode = GetSymbolByKeyInDepth(channelId),
                    Time = ServerTime
                };

                _allDepths[channelId] = currentMarketDepth;
            }

            List<object> update = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());
            decimal updatePrice = update[0].ToString().ToDecimal();
            decimal count = update[1].ToString().ToDecimal();
            decimal amount = update[2].ToString().ToDecimal();

            // Обновляем Bids или Asks
            if (count > 0)
            {
                if (amount > 0)
                {
                    UpdateOrAddLevel(currentMarketDepth.Bids, updatePrice, amount, isBid: true);
                }
                else if (amount < 0)
                {
                    UpdateOrAddLevel(currentMarketDepth.Asks, updatePrice, Math.Abs(amount), isBid: false);
                }
            }
            else
            {
                // Удаляем уровень
                if (amount > 0)
                {
                    RemoveLevel(currentMarketDepth.Bids, updatePrice);
                }
                else if (amount < 0)
                {
                    RemoveLevel(currentMarketDepth.Asks, updatePrice);
                }
            }

            // Проверяем корректность данных
            ValidateDepth(currentMarketDepth);

            // Обновляем время стакана
            currentMarketDepth.Time = ServerTime;

            // Вызываем событие обновления стакана
            MarketDepthEvent?.Invoke(currentMarketDepth);
        }

        // Метод для обновления или добавления уровня
        private void UpdateOrAddLevel(List<MarketDepthLevel> levels, decimal price, decimal volume, bool isBid)
        {
            MarketDepthLevel existingLevel = levels.Find(level => level.Price == price);
            if (existingLevel != null)
            {
                // Если уровень существует, обновляем объем
                if (isBid)
                    existingLevel.Bid = volume;
                else
                    existingLevel.Ask = volume;
            }
            else
            {
                // Добавляем новый уровень
                if (isBid)
                    levels.Add(new MarketDepthLevel { Price = price, Bid = volume });
                else
                    levels.Add(new MarketDepthLevel { Price = price, Ask = volume });
            }

            // Поддерживаем сортировку
            if (isBid)
                levels.Sort((a, b) => b.Price.CompareTo(a.Price)); // Убывание для Bid
            else
                levels.Sort((a, b) => a.Price.CompareTo(b.Price)); // Возрастание для Ask
        }

        // Метод для удаления уровня
        private void RemoveLevel(List<MarketDepthLevel> levels, decimal price)
        {
            levels.RemoveAll(level => level.Price == price);
        }

        // Метод для проверки корректности стакана
        private void ValidateDepth(MarketDepth depth)
        {
            // Проверка Bids
            for (int i = 1; i < depth.Bids.Count; i++)
            {
                if (depth.Bids[i].Price >= depth.Bids[i - 1].Price)
                {
                    SendLogMessage($"MD Error 18. Bids[{i}] price >= Bids[{i - 1}] price ({depth.SecurityNameCode}) ({depth.Bids[i].Price}-{depth.Bids[i - 1].Price})", LogMessageType.Error);
                }
            }

            // Проверка Asks
            for (int i = 1; i < depth.Asks.Count; i++)
            {
                if (depth.Asks[i].Price <= depth.Asks[i - 1].Price)
                {
                    SendLogMessage($"MD Error 20. Asks[{i}] price <= Asks[{i - 1}] price ({depth.SecurityNameCode}) ({depth.Asks[i].Price}-{depth.Asks[i - 1].Price})", LogMessageType.Error);
                }
            }

            // Проверка на уникальность
            if (depth.Bids.GroupBy(b => b.Price).Any(g => g.Count() > 1))
            {
                SendLogMessage($"MD Error 21. Bids with same price ({depth.SecurityNameCode})", LogMessageType.Error);
            }

            if (depth.Asks.GroupBy(a => a.Price).Any(g => g.Count() > 1))
            {
                SendLogMessage($"MD Error 22. Asks with same price ({depth.SecurityNameCode})", LogMessageType.Error);
            }
        }



        // Список подписанных инструментов
        private List<MarketDepth> _marketDepths = new List<MarketDepth>();

        private void SnapshotDepth(string message)
        {
        
                List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
            if (root == null || root.Count < 2)
            {
                // Проверяем корректность формата сообщения
                SendLogMessage("Некорректное сообщение SnapshotDepth: недостаточно элементов.", LogMessageType.Error);
                return;
            }

            // Извлекаем идентификатор канала
            int channelId = Convert.ToInt32(root[0]);
            string nameSecurity = GetSymbolByKeyInDepth(channelId);
           
            List<List<object>> snapshot = JsonConvert.DeserializeObject<List<List<object>>>(root[1].ToString());
            if (snapshot == null) return;

            try
            {
                //lock (_depthLocker)
                //{
                    if (_marketDepths == null)
                    {
                        _marketDepths = new List<MarketDepth>();
                    }

                    var needDepth = _marketDepths.Find(depth =>
                        depth.SecurityNameCode == nameSecurity);

                    if (needDepth == null)
                    {
                        needDepth = new MarketDepth();
                        needDepth.SecurityNameCode = nameSecurity;
                        _marketDepths.Add(needDepth);
                    }

                    List<MarketDepthLevel> ascs = new List<MarketDepthLevel>();
                    List<MarketDepthLevel> bids = new List<MarketDepthLevel>();

                    // Цикл для перебора элементов snapshot
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        // Получаем текущий уровень в стакане
                        List<object> value = snapshot[i];

                        // Проверяем объем уровня: если больше 0, то это Bid
                        if (Convert.ToDecimal(value[2]) > 0)
                        {
                            // Добавляем уровень в список бидов
                            bids.Add(new MarketDepthLevel()
                            {
                                Bid = Convert.ToDecimal(value[2]), // Устанавливаем объем бида
                                Price = Convert.ToDecimal(value[0]), // Устанавливаем цену уровня
                            });
                        }
                        else
                        {
                            // Добавляем уровень в список асков
                            ascs.Add(new MarketDepthLevel()
                            {
                                Ask = Convert.ToDecimal(Math.Abs(Convert.ToDecimal(value[2]))), // Устанавливаем объем аска (модуль числа)
                                Price = Convert.ToDecimal(value[0]), // Устанавливаем цену уровня
                            });
                        }
                    }

                    needDepth.Asks = ascs;
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
                //}
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }



        private void UpdateDepth(string message)
        {
            List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
            int channelId =Convert.ToInt32(root[0]);

           List<object> newData = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());

            string nameSecurity = GetSymbolByKeyInDepth(channelId);


            try
            {
                //lock (_depthLocker)
                //{
                    if (_marketDepths == null)
                    {
                        return;
                    }
                    var needDepth = _marketDepths.Find(depth =>
                        depth.SecurityNameCode == nameSecurity);

                    if (needDepth == null)
                    {
                        return;
                    }

                   
                    var price = Convert.ToDecimal(newData[0]);

                    var count = Convert.ToDecimal(newData[1]);

                    var amount = Convert.ToDecimal(newData[2]);


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


                // если колл-во ореров равно 0, значит надо найти уровень этой цены и удалить его
                if (count == 0)
                    {
                       
                        if (amount < 0)  // delete from asks / удаляем из асков
                        {
                            needDepth.Asks.Remove(needDepth.Asks.Find(level => level.Price == price));
                        }

                        if (amount > 0)  // delete from bids / удаляем из бидов
                        {
                            needDepth.Bids.Remove(needDepth.Bids.Find(level => level.Price == price));
                        }
                        return;
                    }

                    //если объем больше нуля, значит изменился какой-то бид, находим его и обновляем
                    else if (amount > 0)
                    {
                        var needLevel = needDepth.Bids.Find(bid => bid.Price == price);

                        if (needLevel == null)  // если такого уровня нет, добавляем его
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
                    // if less, then update the ask / если меньше, значит обновляем аск
                    else if (amount < 0)
                    {
                        var needLevel = needDepth.Asks.Find(asc => asc.Price == price);

                        if (needLevel == null)  // if there is no such level, add it / если такого уровня нет, добавляем его
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
              //  }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }













        // Обработчик события нового стакана
        private void UpdateMarketDepth(MarketDepth md)
        {
            // Проверяем, что обновление имеет валидный SecurityNameCode
            if (string.IsNullOrWhiteSpace(md.SecurityNameCode))
            {
               
                return;
            }

            // Находим стакан, соответствующий инструменту из обновления
            var existingMarketDepth = _marketDepths.Find(depth => depth.SecurityNameCode == md.SecurityNameCode);

            if (existingMarketDepth == null)
            {
                // Если стакан для этого инструмента не найден, возможно, нужно создать новый
                _marketDepths.Add(md);
              
            }
            else
            {
                // Если стакан найден, обновляем его содержимое
                UpdateMarketDepth(existingMarketDepth, md.Bids, md.Asks);
               
            }
        }

        // Метод для обновления конкретного стакана
        private void UpdateMarketDepth(MarketDepth existingDepth, List<MarketDepthLevel> bids, List<MarketDepthLevel> asks)
        {
            // Обновляем Bids и Asks согласно требованиям
            existingDepth.Bids = bids.OrderByDescending(b => b.Price).Take(25).ToList();
            existingDepth.Asks = asks.OrderBy(a => a.Price).Take(25).ToList();

            // Обновляем время стакана
            existingDepth.Time = DateTime.Now;
        }






        // Словарь для хранения данных всех стаканов
        private Dictionary<int, MarketDepth> _allDepths = new Dictionary<int, MarketDepth>();

        //////// Метод для обработки нового снапшота стакана
        //////public void SnapshotDepth(string message)
        //////{
        //////    lock (_depthLocker) { 
        //////        if (string.IsNullOrEmpty(message))
        //////    {
        //////        // Проверяем, не пустое ли сообщение
        //////        SendLogMessage("Пустое сообщение в SnapshotDepth.", LogMessageType.Error);
        //////        return;
        //////    }

        //////    // Десериализуем сообщение
        //////    List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
        //////    if (root == null || root.Count < 2)
        //////    {
        //////        // Проверяем корректность формата сообщения
        //////        SendLogMessage("Некорректное сообщение SnapshotDepth: недостаточно элементов.", LogMessageType.Error);
        //////        return;
        //////    }

        //////    // Извлекаем идентификатор канала
        //////    int channelId = Convert.ToInt32(root[0]);

        //////    // Создаем новый стакан
        //////    MarketDepth marketDepth = new MarketDepth
        //////    {
        //////        SecurityNameCode = GetSymbolByKeyInDepth(channelId), // Название инструмента
        //////        Time = ServerTime // Устанавливаем время обновления
        //////    };

        //////    // Десериализуем данные снапшота
        //////    List<List<object>> snapshot = JsonConvert.DeserializeObject<List<List<object>>>(root[1].ToString());
        //////    if (snapshot == null) return;

        //////    // Временные списки для бидов и асков
        //////    List<MarketDepthLevel> tempBids = new List<MarketDepthLevel>();
        //////    List<MarketDepthLevel> tempAsks = new List<MarketDepthLevel>();

        //////    // Обрабатываем данные снапшота
        //////    foreach (var entry in snapshot)
        //////    {
        //////        if (entry.Count < 3) continue; // Пропускаем некорректные записи

        //////        decimal price = entry[0].ToString().ToDecimal(); // Цена
        //////        decimal count = entry[1].ToString().ToDecimal(); // Количество
        //////        decimal amount = entry[2].ToString().ToDecimal(); // Объем

        //////        if (amount > 0)
        //////        {
        //////            // Добавляем в биды
        //////            tempBids.Add(new MarketDepthLevel { Price = price, Bid = amount });
        //////        }
        //////        else if (amount < 0)
        //////        {
        //////            // Добавляем в аски
        //////            tempAsks.Add(new MarketDepthLevel { Price = price, Ask = Math.Abs(amount) });
        //////        }
        //////    }

        //////    // Удаляем дубликаты и сортируем уровни
        //////    tempBids = tempBids.GroupBy(b => b.Price).Select(g => g.First()).ToList();
        //////    tempBids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Сортируем биды по убыванию цены

        //////    tempAsks = tempAsks.GroupBy(a => a.Price).Select(g => g.First()).ToList();
        //////    tempAsks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Сортируем аски по возрастанию цены

        //////    // Ограничиваем количество уровней
        //////    const int MaxLevels = 25;
        //////    if (tempBids.Count > MaxLevels) tempBids.RemoveRange(MaxLevels, tempBids.Count - MaxLevels);
        //////    if (tempAsks.Count > MaxLevels) tempAsks.RemoveRange(MaxLevels, tempAsks.Count - MaxLevels);

        //////    // Обновляем или добавляем новый стакан в словарь
        //////    if (_allDepths.ContainsKey(channelId))
        //////    {
        //////        _allDepths[channelId].Bids = tempBids;
        //////        _allDepths[channelId].Asks = tempAsks;
        //////        _allDepths[channelId].Time = ServerTime;
        //////    }
        //////    else
        //////    {
        //////        _allDepths[channelId] = new MarketDepth
        //////        {
        //////            SecurityNameCode = marketDepth.SecurityNameCode,
        //////            Bids = tempBids,
        //////            Asks = tempAsks,
        //////            Time = ServerTime
        //////        };
        //////    }

        //////    // Вызываем событие обновления стакана
        //////    MarketDepthEvent?.Invoke(_allDepths[channelId]);
        //////}
        //////}
        //////private readonly object _depthLocker = new object();
        //////// Метод для обновления стакана
        //////public void UpdateDepth(string message)
        //////{
        //////    lock (_depthLocker)
        //////    {
        //////        if (string.IsNullOrEmpty(message))
        //////        {
        //////            SendLogMessage("Пустое сообщение в UpdateDepth.", LogMessageType.Error);
        //////            return;
        //////        }

        //////        // Десериализуем сообщение
        //////        List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
        //////        List<object> update = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());

        //////        int channelId = Convert.ToInt32(root[0]);
        //////        if (!_allDepths.TryGetValue(channelId, out MarketDepth currentMarketDepth))
        //////        {
        //////            // Создаем новый стакан, если его нет
        //////            currentMarketDepth = new MarketDepth
        //////            {
        //////                Bids = new List<MarketDepthLevel>(),
        //////                Asks = new List<MarketDepthLevel>(),
        //////                SecurityNameCode = GetSymbolByKeyInDepth(channelId),
        //////                Time = ServerTime
        //////            };
        //////            _allDepths[channelId] = currentMarketDepth;
        //////        }

        //////        decimal updatePrice = update[0].ToString().ToDecimal(); // Цена
        //////        decimal count = update[1].ToString().ToDecimal(); // Количество
        //////        decimal amount = update[2].ToString().ToDecimal(); // Объем

        //////        if (count > 0)
        //////        {
        //////            // Добавляем или обновляем уровень
        //////            if (amount > 0)
        //////            {
        //////                // Для бидов
        //////                var bid = currentMarketDepth.Bids.Find(b => b.Price == updatePrice);
        //////                if (bid != null) bid.Bid = amount; // Обновляем
        //////                else currentMarketDepth.Bids.Add(new MarketDepthLevel { Price = updatePrice, Bid = amount }); // Добавляем
        //////            }
        //////            else if (amount < 0)
        //////            {
        //////                // Для асков
        //////                var ask = currentMarketDepth.Asks.Find(a => a.Price == updatePrice);
        //////                if (ask != null) ask.Ask = Math.Abs(amount); // Обновляем
        //////                else currentMarketDepth.Asks.Add(new MarketDepthLevel { Price = updatePrice, Ask = Math.Abs(amount) }); // Добавляем
        //////            }
        //////        }
        //////        else
        //////        {
        //////            // Удаляем уровень
        //////            if (amount > 0) currentMarketDepth.Bids.RemoveAll(b => b.Price == updatePrice); // Из бидов
        //////            else if (amount < 0) currentMarketDepth.Asks.RemoveAll(a => a.Price == updatePrice); // Из асков
        //////        }

        //////        // Удаляем дубликаты, сортируем и ограничиваем количество уровней
        //////        currentMarketDepth.Bids = currentMarketDepth.Bids.GroupBy(b => b.Price).Select(g => g.First()).ToList();
        //////        currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price));

        //////        currentMarketDepth.Asks = currentMarketDepth.Asks.GroupBy(a => a.Price).Select(g => g.First()).ToList();
        //////        currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price));

        //////        if (currentMarketDepth.Bids.Count > 25) currentMarketDepth.Bids.RemoveRange(25, currentMarketDepth.Bids.Count - 25);
        //////        if (currentMarketDepth.Asks.Count > 25) currentMarketDepth.Asks.RemoveRange(25, currentMarketDepth.Asks.Count - 25);

        //////        // Обновляем время и вызываем событие
        //////        currentMarketDepth.Time = ServerTime;
        //////        MarketDepthEvent?.Invoke(currentMarketDepth);
        //////    }
        //////}


        ////////private Dictionary<int, MarketDepth> _allDepths = new Dictionary<int, MarketDepth>();

        //////public void SnapshotDepth(string message)
        //////{

        //////    MarketDepth marketDepth = new MarketDepth();

        //////    if (string.IsNullOrEmpty(message))
        //////    {
        //////        SendLogMessage("Пустое сообщение в SnapshotDepth.", LogMessageType.Error);
        //////        return;
        //////    }

        //////    List<object> root = JsonConvert.DeserializeObject<List<object>>(message);

        //////    if (root == null || root.Count < 2)
        //////    {
        //////        SendLogMessage("Некорректное сообщение SnapshotDepth: недостаточно элементов.", LogMessageType.Error);
        //////        return;
        //////    }

        //////    int channelId = Convert.ToInt32(root[0]);
        //////    marketDepth.SecurityNameCode = GetSymbolByKeyInDepth(channelId);
        //////    string symbol = marketDepth.SecurityNameCode;
        //////    marketDepth.Time = ServerTime;

        //////    //if (currentChannelIdDepth != channelId)
        //////    //{
        //////    //    currentChannelIdDepth = channelId;
        //////    //    //marketDepth.SecurityNameCode = GetSymbolByKeyInDepth(currentChannelIdDepth);
        //////    //    return;
        //////    //}


        //////    //if (currentChannelIdDepth != channelId)
        //////    //{
        //////    //    // Обновляем идентификатор текущего канала
        //////    //    currentChannelIdDepth = channelId;

        //////    //    // Обновляем название инструмента
        //////    //    marketDepth.SecurityNameCode = GetSymbolByKeyInDepth(currentChannelIdDepth);

        //////    //    // Очищаем данные стакана
        //////    //    //bidsSnapshot.Clear(); // Очищаем список бидов
        //////    //    //asksSnapshot.Clear(); // Очищаем список асков
        //////    //    //marketDepth.Bids.Clear(); // Очищаем глобальный стакан бидов
        //////    //    //marketDepth.Asks.Clear(); // Очищаем глобальный стакан асков

        //////    //    return;
        //////    //}

        //////    List<List<object>> snapshot = JsonConvert.DeserializeObject<List<List<object>>>(root[1].ToString());

        //////    if (snapshot == null)
        //////    {
        //////        return;
        //////    }

        //////    List<MarketDepthLevel> tempBids = new List<MarketDepthLevel>();
        //////    List<MarketDepthLevel> tempAsks = new List<MarketDepthLevel>();

        //////    for (int i = 0; i < snapshot.Count; i++)
        //////    {
        //////        var entry = snapshot[i];

        //////        if (entry.Count < 3) continue;

        //////        decimal price = entry[0].ToString().ToDecimal();
        //////        decimal count = entry[1].ToString().ToDecimal();
        //////        decimal amount = entry[2].ToString().ToDecimal();

        //////        if (amount > 0)
        //////        {
        //////            tempBids.Add(new MarketDepthLevel { Price = price, Bid = amount });

        //////        }
        //////        else if (amount < 0)
        //////        {
        //////            tempAsks.Add(new MarketDepthLevel { Price = price, Ask = Math.Abs(amount) });
        //////        }
        //////    }

        //////    if (_allDepths.ContainsKey(channelId))
        //////    {
        //////        // Если существует, перезаписываем значение
        //////        _allDepths[channelId] = marketDepth;

        //////        // Если стакан существует, возвращаем его
        //////        //  var depth= _allDepths[channelId];

        //////    }
        //////    else
        //////    {
        //////        // Если не существует, добавляем новый
        //////        _allDepths.Add(channelId, new MarketDepth
        //////        {
        //////            Bids = tempBids,
        //////            Asks = tempAsks,
        //////            SecurityNameCode = symbol,
        //////            Time = DateTime.UtcNow
        //////        });

        //////    }

        //////    if (!_allDepths.TryGetValue(channelId, out MarketDepth currentMarketDepth))
        //////    {
        //////        // Если запись отсутствует, создаем новую
        //////        currentMarketDepth = new MarketDepth
        //////        {
        //////            SecurityNameCode = GetSymbolByKeyInDepth(channelId),
        //////            Bids = new List<MarketDepthLevel>(),
        //////            Asks = new List<MarketDepthLevel>(),
        //////            Time = DateTime.UtcNow
        //////        };

        //////        // Добавляем новую запись в словарь
        //////        _allDepths[channelId] = currentMarketDepth;
        //////    }



        //////    currentMarketDepth.Bids = tempBids;/////////////
        //////    currentMarketDepth.Asks = tempAsks;/////////////

        //////    marketDepth.Time = ServerTime;

        //////    //if (marketDepth.Time < _lastTimeMd)
        //////    //{
        //////    //    marketDepth.Time = _lastTimeMd;
        //////    //}
        //////    //else if (marketDepth.Time == _lastTimeMd)
        //////    //{
        //////    //    _lastTimeMd = DateTime.FromBinary(_lastTimeMd.Ticks + 1);
        //////    //    marketDepth.Time = _lastTimeMd;
        //////    //}

        //////    //_lastTimeMd = marketDepth.Time;



        //////    MarketDepthEvent?.Invoke(currentMarketDepth);
        //////}




        ////////public void UpdateDepth(string message/*,int channelId*/)
        ////////{

        ////////    List<List<object>> updates;

        ////////    List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
        ////////    List<object> update = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());


        ////////    int channelId = Convert.ToInt32(root[0]);
        ////////    string symbol = GetSymbolByKeyInDepth(channelId);

        ////////    // Найти запись в словаре по ключу или создать новую
        ////////    if (!_allDepths.TryGetValue(channelId, out MarketDepth currentMarketDepth))
        ////////    {
        ////////        // Создаем новую запись, если её нет
        ////////        currentMarketDepth = new MarketDepth
        ////////        {
        ////////            Bids = new List<MarketDepthLevel>(),
        ////////            Asks = new List<MarketDepthLevel>(),
        ////////            SecurityNameCode = GetSymbolByKeyInDepth(channelId),
        ////////            Time = DateTime.UtcNow
        ////////        };

        ////////        _allDepths[channelId] = currentMarketDepth;
        ////////    }







        ////////    //if( channelId != chanlId )
        ////////    //{
        ////////    //    channelId = chanlId;
        ////////    //    MarketDepth marketDepth = currentMarketDepth;
        ////////    //}

        ////////    //// Проверяем, изменился ли channelId
        ////////    //if (_currentChannelId != newChannelId)
        ////////    //{
        ////////    //    // Обновляем текущий channelId и инструмент
        ////////    //    _currentChannelId = newChannelId;
        ////////    //    _currentInstrument = GetSymbolByKeyInDepth(newChannelId);

        ////////    //    SendLogMessage($"Переключение на новый инструмент: {_currentInstrument} (Channel ID: {_currentChannelId})", LogMessageType.Info);
        ////////    //}

        ////////    //// Получаем или создаем стакан для текущего channelId
        ////////    //if (!_allDepths.TryGetValue(_currentChannelId, out MarketDepth marketDepth))
        ////////    //{
        ////////    //    marketDepth = new MarketDepth
        ////////    //    {
        ////////    //        SecurityNameCode = _currentInstrument,
        ////////    //        Bids = new List<MarketDepthLevel>(),
        ////////    //        Asks = new List<MarketDepthLevel>(),
        ////////    //        Time = DateTime.UtcNow
        ////////    //    };

        ////////    //    _allDepths[_currentChannelId] = marketDepth;
        ////////    //    SendLogMessage($"Создан новый стакан для инструмента: {_currentInstrument}", LogMessageType.Info);
        ////////    //}

        ////////    decimal updatePrice = update[0].ToString().ToDecimal();
        ////////    decimal count = update[1].ToString().ToDecimal();
        ////////    decimal amount = update[2].ToString().ToDecimal();


        ////////    // Добавление, обновление или удаление уровней
        ////////    if (count > 0)
        ////////    {
        ////////        // Добавить или обновить уровень
        ////////        if (amount > 0)
        ////////        {
        ////////            // Для Bids
        ////////            var existingBid = currentMarketDepth.Bids.Find(level => level.Price == updatePrice);

        ////////            if (existingBid != null)
        ////////            {
        ////////                existingBid.Bid = amount; // Обновление
        ////////            }
        ////////            else
        ////////            {
        ////////                currentMarketDepth.Bids.Add(new MarketDepthLevel { Price = updatePrice, Bid = amount }); // Добавление

        ////////            }
        ////////            // currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Биды по убыванию

        ////////        }
        ////////        else if (amount < 0)
        ////////        {
        ////////            // Для Asks
        ////////            var existingAsk = currentMarketDepth.Asks.Find(level => level.Price == updatePrice);

        ////////            if (existingAsk != null)
        ////////            {
        ////////                existingAsk.Ask = Math.Abs(amount); // Обновление
        ////////            }
        ////////            else
        ////////            {
        ////////                currentMarketDepth.Asks.Add(new MarketDepthLevel { Price = updatePrice, Ask = Math.Abs(amount) }); // Добавление

        ////////            }
        ////////            // currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Аски по возрастанию
        ////////        }


        ////////    }
        ////////    else if (count == 0)
        ////////    {
        ////////        if (amount > 0)
        ////////        {
        ////////            // Удаляем из Bids
        ////////            currentMarketDepth.Bids.RemoveAll(level => level.Price == updatePrice);
        ////////        }
        ////////        else if (amount < 0)
        ////////        {
        ////////            // Удаляем из Asks
        ////////            currentMarketDepth.Asks.RemoveAll(level => level.Price == updatePrice);
        ////////        }
        ////////        //// Удалить уровень
        ////////        //if (amount > 0)
        ////////        //{
        ////////        //    // Удаляем из Bids
        ////////        //    for (int i = 0; i < currentMarketDepth.Bids.Count; i++)
        ////////        //    {
        ////////        //        if (currentMarketDepth.Bids[i].Price == updatePrice)
        ////////        //        {
        ////////        //            currentMarketDepth.Bids.RemoveAt(i);


        ////////        //            // break;
        ////////        //        }
        ////////        //    }
        ////////        //    // currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Биды по убыванию
        ////////        //}
        ////////        //else if (amount < 0)
        ////////        //{
        ////////        //    // Удаляем из Asks
        ////////        //    for (int i = 0; i < currentMarketDepth.Asks.Count; i++)
        ////////        //    {
        ////////        //        if (currentMarketDepth.Asks[i].Price == updatePrice)
        ////////        //        {
        ////////        //            currentMarketDepth.Asks.RemoveAt(i);


        ////////        //            // break;
        ////////        //        }
        ////////        //    }
        ////////        //    //  currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Аски по возрастанию
        ////////        //}

        ////////    }


        ////////    currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Биды по убыванию
        ////////    currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Аски по возрастанию
        ////////    // Ограничение количества уровней
        ////////    const int MaxLevels = 25;
        ////////    if (currentMarketDepth.Bids.Count > MaxLevels) currentMarketDepth.Bids.RemoveRange(MaxLevels, currentMarketDepth.Bids.Count - MaxLevels);
        ////////    if (currentMarketDepth.Asks.Count > MaxLevels) currentMarketDepth.Asks.RemoveRange(MaxLevels, currentMarketDepth.Asks.Count - MaxLevels);

        ////////    // Обновляем время последнего изменения
        ////////    currentMarketDepth.Time = DateTime.UtcNow;
        ////////    // Удаление дубликатов (опционально)
        ////////    currentMarketDepth.Bids = currentMarketDepth.Bids.GroupBy(b => b.Price).Select(g => g.First()).ToList();
        ////////    currentMarketDepth.Asks = currentMarketDepth.Asks.GroupBy(a => a.Price).Select(g => g.First()).ToList();

        ////////    // Вызываем событие обновления стакана
        ////////    MarketDepthEvent?.Invoke(currentMarketDepth);
        ////////}



        public void ProcessMessage(int newChannelId, string symbol)
        {

            //// Проверяем, изменился ли channelId
            //if (_currentChannelId != newChannelId)
            //{
            //    // Обновляем текущий channelId и инструмент
            //    _currentChannelId = newChannelId;
            //   symbol = GetSymbolByKeyInDepth(newChannelId);

            //    SendLogMessage($"Переключение на новый инструмент: {symbol} (Channel ID: {_currentChannelId})", LogMessageType.Error);
            //}

            // Получаем или создаем стакан для текущего channelId
            if (!_allDepths.TryGetValue(newChannelId, out MarketDepth marketDepth))
            {
                marketDepth = new MarketDepth
                {
                    SecurityNameCode = symbol,
                    Bids = new List<MarketDepthLevel>(),
                    Asks = new List<MarketDepthLevel>(),
                    Time = DateTime.UtcNow
                };

                _allDepths[newChannelId] = marketDepth;
                SendLogMessage($"Создан новый стакан для инструмента: {symbol}", LogMessageType.Error);
            }

            // Обрабатываем сообщение

        }



        ////public void UpdateDepth(string message, int channelId)
        ////{
        ////    List<List<object>> updates;
        ////    if (!_allDepths.TryGetValue(channelId, out MarketDepth currentMarketDepth))
        ////    {
        ////        // Если запись отсутствует, создаем новую
        ////        currentMarketDepth = new MarketDepth
        ////        {
        ////            SecurityNameCode = GetSymbolByKeyInDepth(channelId),
        ////            Bids = new List<MarketDepthLevel>(),
        ////            Asks = new List<MarketDepthLevel>(),
        ////            Time = DateTime.UtcNow
        ////        };

        ////        // Добавляем новую запись в словарь
        ////        _allDepths[channelId] = currentMarketDepth;
        ////    }

        ////    // currentMarketDepth теперь содержит либо существующую запись, либо новую



        ////    List<object> root = JsonConvert.DeserializeObject<List<object>>(message);
        ////    List<object> update = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());

        ////    // Проходим по каждому изменению в обновлении



        ////    decimal price = update[0].ToString().ToDecimal();
        ////    decimal count = update[1].ToString().ToDecimal();
        ////    decimal amount = update[2].ToString().ToDecimal();

        ////    // Условие: count > 0 — добавляем или обновляем уровень цены
        ////    if (count > 0)
        ////    {
        ////        if (amount > 0)
        ////        {
        ////            AddOrUpdateLevel(currentMarketDepth.Bids, price, amount, isBid: true);
        ////        }
        ////        else if (amount < 0)
        ////        {
        ////            AddOrUpdateLevel(currentMarketDepth.Asks, price, Math.Abs(amount), isBid: false);

        ////        }
        ////        currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Сортируем биды по убыванию
        ////        currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Сортируем аски по возрастанию
        ////    }


        ////    else if (count == 0)
        ////    {
        ////        if (amount > 0)
        ////        {
        ////            // Удаляем из текущего списка бидов уровень с указанной ценой
        ////            for (int i = 0; i < currentMarketDepth.Bids.Count; i++)
        ////            {
        ////                if (currentMarketDepth.Bids[i].Price == price)
        ////                {
        ////                    currentMarketDepth.Bids.RemoveAt(i);
        ////                    // break; // Прекращаем поиск после удаления
        ////                   currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price));
        ////                }

        ////            }
        ////             // Сортируем биды по убыванию

        ////        }
        ////        else if (amount < 0)
        ////        {
        ////            // Удаляем из текущего списка асков уровень с указанной ценой
        ////            for (int i = 0; i < currentMarketDepth.Asks.Count; i++)
        ////            {
        ////                if (currentMarketDepth.Asks[i].Price == price)
        ////                {
        ////                    currentMarketDepth.Asks.RemoveAt(i);
        ////                    //break; // Прекращаем поиск после удаления

        ////                currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Сортируем аски по возрастанию
        ////                }

        ////            }

        ////        }

        ////    }


        ////    if (currentMarketDepth.Asks.Count < 2 ||
        ////       currentMarketDepth.Bids.Count < 2)
        ////    {
        ////        return;
        ////    }

        ////    //if (currentMarketDepth.Asks[0].Price > currentMarketDepth.Asks[1].Price)
        ////    //{
        ////    //    currentMarketDepth.Asks.RemoveAt(0);
        ////    //}
        ////    //if (currentMarketDepth.Bids[0].Price < currentMarketDepth.Bids[1].Price)
        ////    //{
        ////    //    currentMarketDepth.Asks.RemoveAt(0);
        ////    //}

        ////    //if (currentMarketDepth.Asks[0].Price < currentMarketDepth.Bids[0].Price)
        ////    //{
        ////    //    if (currentMarketDepth.Asks[0].Price < currentMarketDepth.Bids[1].Price)
        ////    //    {
        ////    //        currentMarketDepth.Asks.Remove(currentMarketDepth.Asks[0]);
        ////    //    }
        ////    //    else if (currentMarketDepth.Bids[0].Price > currentMarketDepth.Asks[1].Price)
        ////    //    {
        ////    //        currentMarketDepth.Bids.Remove(currentMarketDepth.Bids[0]);
        ////    //    }
        ////    //}
        ////    //Ограничиваем количество уровней
        ////    const int MaxLevels = 25;
        ////    if (currentMarketDepth.Bids.Count > MaxLevels) currentMarketDepth.Bids.RemoveRange(MaxLevels, currentMarketDepth.Bids.Count - MaxLevels);
        ////    if (currentMarketDepth.Asks.Count > MaxLevels) currentMarketDepth.Asks.RemoveRange(MaxLevels, currentMarketDepth.Asks.Count - MaxLevels);

        ////    //currentMarketDepth.Bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Сортируем биды по убыванию
        ////    //currentMarketDepth.Asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Сортируем аски по возрастанию
        ////    //  currentMarketDepth.Asks.Sort((level, depthLevel) => level.Price.CompareTo(depthLevel.Price));



        ////    currentMarketDepth.Time = DateTime.UtcNow;

        ////    _allDepths[channelId] = currentMarketDepth;

        ////    MarketDepthEvent?.Invoke(currentMarketDepth);
        ////}






      
        private void ValidateDepthOrder(List<MarketDepthLevel> bids, List<MarketDepthLevel> asks)
        {
            // Проверяем порядок для бидов (по убыванию)
            for (int i = 1; i < bids.Count; i++)
            {
                if (bids[i].Price > bids[i - 1].Price)
                {
                    SendLogMessage(
                        $"MD Error 18. Bids[{i}] price ({bids[i].Price}) > Bids[{i - 1}] price ({bids[i - 1].Price} ).",
                        LogMessageType.Error);
                    bids.RemoveAt(i); // Удаляем проблемный уровень
                    i--; // Возвращаем индекс, чтобы повторно проверить текущий элемент
                }
            }

            // Проверяем порядок для асков (по возрастанию)
            for (int i = 1; i < asks.Count; i++)
            {
                if (asks[i].Price < asks[i - 1].Price)
                {
                    SendLogMessage(
                        $"MD Error 20. Asks[{i}] price ({asks[i].Price}) < Asks[{i - 1}] price ({asks[i - 1].Price}).",
                        LogMessageType.Error);
                    asks.RemoveAt(i); // Удаляем проблемный уровень
                    i--; // Возвращаем индекс, чтобы повторно проверить текущий элемент
                }
            }

            // Убираем дублирующиеся цены из списка бидов
            for (int i = 1; i < bids.Count; i++)
            {
                if (bids[i].Price == bids[i - 1].Price)
                {
                    SendLogMessage(
                        $"MD Error 21. Bids with same price ({bids[i].Price}) detected.",
                        LogMessageType.Error);
                    bids.RemoveAt(i); // Удаляем дублирующий уровень
                    i--; // Возвращаем индекс, чтобы повторно проверить текущий элемент
                }
            }

            // Убираем дублирующиеся цены из списка асков
            for (int i = 1; i < asks.Count; i++)
            {
                if (asks[i].Price == asks[i - 1].Price)
                {
                    SendLogMessage(
                        $"MD Error 22. Asks with same price ({asks[i].Price}) detected.",
                        LogMessageType.Error);
                    asks.RemoveAt(i); // Удаляем дублирующий уровень
                    i--; // Возвращаем индекс, чтобы повторно проверить текущий элемент
                }
            }

            // Финальная сортировка для восстановления порядка
            bids.Sort((a, b) => b.Price.CompareTo(a.Price)); // Сортируем биды по убыванию
            asks.Sort((a, b) => a.Price.CompareTo(b.Price)); // Сортируем аски по возрастанию
        }



        // Проверка и устранение ошибок с ценами
        //private void ValidateDepthOrder(List<MarketDepthLevel> bids, List<MarketDepthLevel> asks)
        //{
        //    // Проверяем порядок для бидов (по убыванию)
        //    for (int i = 1; i < bids.Count; i++)
        //    {
        //        if (bids[i].Price > bids[i - 1].Price)
        //        {
        //            SendLogMessage($"MD Error 18. Bids[i] price > Bids[i-1] price (Error at index {i})", LogMessageType.Error);
        //            bids.RemoveAt(i); // Удаляем проблемный уровень
        //            break; // Выход из цикла после удаления
        //        }
        //    }

        //    // Проверяем порядок для асков (по возрастанию)
        //    for (int i = 1; i < asks.Count; i++)
        //    {
        //        if (asks[i].Price < asks[i - 1].Price)
        //        {
        //            SendLogMessage($"MD Error 20. Asks[i] price < Asks[i-1] price (Error at index {i})", LogMessageType.Error);
        //            asks.RemoveAt(i); // Удаляем проблемный уровень
        //            break; // Выход из цикла после удаления
        //        }
        //    }
        //}





        //private Dictionary<int, MarketDepth> _allDepths = new Dictionary<int, MarketDepth>();
        //private MarketDepth marketDepth = new MarketDepth();

        //public event Action<MarketDepth> MarketDepthEvent;

        //// Метод для обработки снапшотов
        //public void SnapshotDepth(string message, int currentChannelIdDepth)
        //{
        //    if (string.IsNullOrEmpty(message))
        //    {
        //        SendLogMessage("Пустое сообщение в SnapshotDepth.", LogMessageType.Error);
        //        return;
        //    }

        //    List<object> root;
        //    try
        //    {
        //        root = JsonConvert.DeserializeObject<List<object>>(message);
        //    }
        //    catch (Exception ex)
        //    {
        //        SendLogMessage($"Ошибка десериализации SnapshotDepth: {ex.Message}", LogMessageType.Error);
        //        return;
        //    }

        //    if (root == null || root.Count < 2)
        //    {
        //        SendLogMessage("Некорректное сообщение SnapshotDepth: недостаточно элементов.", LogMessageType.Error);
        //        return;
        //    }

        //    List<List<object>> snapshot;
        //    try
        //    {
        //        snapshot = JsonConvert.DeserializeObject<List<List<object>>>(root[1].ToString());
        //    }
        //    catch (Exception ex)
        //    {
        //        SendLogMessage($"Ошибка десериализации снапшота: {ex.Message}", LogMessageType.Error);
        //        return;
        //    }

        //    if (snapshot == null || snapshot.Count == 0)
        //    {
        //        SendLogMessage("Снапшот пустой.", LogMessageType.Error);
        //        return;
        //    }

        //    // Временные списки
        //    var tempBids = new List<MarketDepthLevel>();
        //    var tempAsks = new List<MarketDepthLevel>();

        //    foreach (var entry in snapshot)
        //    {
        //        if (entry.Count < 3) continue;

        //        decimal price = entry[0].ToString().ToDecimal();
        //        decimal count = entry[1].ToString().ToDecimal();
        //        decimal amount = entry[2].ToString().ToDecimal();

        //        if (amount > 0)
        //        {
        //            tempBids.Add(new MarketDepthLevel { Price = price, Bid = amount });
        //        }
        //        else if (amount < 0)
        //        {
        //            tempAsks.Add(new MarketDepthLevel { Price = price, Ask = Math.Abs(amount) });
        //        }
        //    }

        //    int channelId = Convert.ToInt32(root[0]);
        //    string symbol = GetSymbolByKeyInDepth(channelId);
        //    //_allDepths.Add(channelId,MarketDepth marketDepth ))

        //    if (!_allDepths.TryGetValue(channelId, out MarketDepth marketDepth))
        //    {
        //        marketDepth = new MarketDepth
        //        {
        //            SecurityNameCode = symbol,
        //            Bids = new List<MarketDepthLevel>(),
        //            Asks = new List<MarketDepthLevel>()
        //        };
        //        _allDepths[channelId] = marketDepth;
        //    }

        //    marketDepth.Bids = tempBids;
        //    marketDepth.Asks = tempAsks;


        //    ApplyDepthChanges(marketDepth.Bids, marketDepth.Asks);

        //    marketDepth.Time = DateTime.UtcNow;
        //    _allDepths[channelId] = marketDepth;

        //    MarketDepthEvent?.Invoke(marketDepth);

        //}

        //// Метод для обновления данных
        //public void UpdateDepth(string message, int currentChannelIdDepth)
        //{
        //    if (string.IsNullOrEmpty(message))
        //    {
        //        SendLogMessage("Пустое сообщение в UpdateDepth.", LogMessageType.Error);
        //        return;
        //    }

        //    List<object> root;
        //    try
        //    {
        //        root = JsonConvert.DeserializeObject<List<object>>(message);
        //    }
        //    catch (Exception ex)
        //    {
        //        SendLogMessage($"Ошибка десериализации UpdateDepth: {ex.Message}", LogMessageType.Error);
        //        return;
        //    }

        //    if (root == null || root.Count < 2)
        //    {
        //        SendLogMessage("Некорректное сообщение UpdateDepth: недостаточно элементов.", LogMessageType.Error);
        //        return;
        //    }

        //    int channelId = Convert.ToInt32(root[0]);
        //    string symbol = GetSymbolByKeyInDepth(channelId);

        //    if (!_allDepths.TryGetValue(channelId, out MarketDepth marketDepth))
        //    {
        //        marketDepth = new MarketDepth
        //        {
        //            SecurityNameCode = symbol,
        //            Bids = new List<MarketDepthLevel>(),
        //            Asks = new List<MarketDepthLevel>(),
        //            Time = DateTime.UtcNow
        //        };
        //        _allDepths[channelId] = marketDepth;
        //    }

        //    List<object> bookEntry;
        //    try
        //    {
        //        bookEntry = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());
        //    }
        //    catch (Exception ex)
        //    {
        //        SendLogMessage($"Ошибка десериализации обновления: {ex.Message}", LogMessageType.Error);
        //        return;
        //    }

        //    if (bookEntry == null || bookEntry.Count < 3)
        //    {
        //        SendLogMessage("Некорректный bookEntry: недостаточно элементов.", LogMessageType.Error);
        //        return;
        //    }

        //    decimal price = bookEntry[0].ToString().ToDecimal();
        //    decimal count = bookEntry[1].ToString().ToDecimal();
        //    decimal amount = bookEntry[2].ToString().ToDecimal();

        //    if (count > 0)
        //    {
        //        if (amount > 0)
        //        {
        //            UpdateLevel(marketDepth.Bids, price, amount, true);
        //        }
        //        else
        //        {
        //            UpdateLevel(marketDepth.Asks, price, Math.Abs(amount), false);
        //        }
        //    }
        //    else if (count == 0)
        //    {
        //        if (amount > 0)
        //        {
        //            RemoveLevel(marketDepth.Bids, price);
        //        }
        //        else if (amount < 0)
        //        {
        //            RemoveLevel(marketDepth.Asks, price);
        //        }
        //    }

        //    ApplyDepthChanges(marketDepth.Bids, marketDepth.Asks);

        //    marketDepth.Time = DateTime.UtcNow;
        //    _allDepths[channelId] = marketDepth;

        //    MarketDepthEvent?.Invoke(marketDepth);

        //}

        //// Метод для добавления или обновления снапшотов
        //public void AddOrUpdateSnapshot(int channelId, MarketDepth marketDepth)
        //{
        //    if (marketDepth == null)
        //    {
        //        SendLogMessage("Попытка добавить пустой MarketDepth в словарь.", LogMessageType.Error);
        //        return;
        //    }

        //    if (_allDepths.ContainsKey(channelId))
        //    {
        //        var existingDepth = _allDepths[channelId];
        //        existingDepth.Bids = new List<MarketDepthLevel>(marketDepth.Bids);
        //        existingDepth.Asks = new List<MarketDepthLevel>(marketDepth.Asks);
        //        existingDepth.Time = marketDepth.Time;
        //        existingDepth.SecurityNameCode = marketDepth.SecurityNameCode;

        //        SendLogMessage($"Снапшот для канала {channelId} обновлён.", LogMessageType.Error);
        //    }
        //    else
        //    {
        //        _allDepths[channelId] = marketDepth;
        //        SendLogMessage($"Снапшот для канала {channelId} добавлен.", LogMessageType.Error);
        //    }
        //}

        //// Метод для сортировки и проверки уровней
        //private void ApplyDepthChanges(List<MarketDepthLevel> bids, List<MarketDepthLevel> asks)
        //{
        //    bids.Sort((a, b) => b.Price.CompareTo(a.Price));
        //    asks.Sort((a, b) => a.Price.CompareTo(b.Price));

        //    if (bids.Count > 0 && asks.Count > 0 && bids[0].Price >= asks[0].Price)
        //    {
        //        SendLogMessage($"Ошибка пересечения цен: Bid({bids[0].Price}) >= Ask({asks[0].Price}).", LogMessageType.Error);
        //        bids.RemoveAt(0);
        //        asks.RemoveAt(0);
        //    }
        //}

        //private void UpdateLevel(List<MarketDepthLevel> levels, decimal price, decimal amount, bool isBid)
        //{
        //    var existingLevel = levels.FirstOrDefault(level => level.Price == price);
        //    if (existingLevel != null)
        //    {
        //        if (isBid)
        //            existingLevel.Bid = amount;
        //        else
        //            existingLevel.Ask = amount;
        //    }
        //    else
        //    {
        //        levels.Add(new MarketDepthLevel
        //        {
        //            Price = price,
        //            Bid = isBid ? amount : 0,
        //            Ask = isBid ? 0 : amount
        //        });
        //    }
        //}

        //private void RemoveLevel(List<MarketDepthLevel> levels, decimal price)
        //{
        //    levels.RemoveAll(level => level.Price == price);
        //}










        private void WebSocketPrivate_Opened(object sender, EventArgs e)
        {
            GenerateAuthenticate();
            _socketPrivateIsActive = true;//отвечает за соединение
            //CheckActivationSockets();
            SendLogMessage("Connection to private data is Open", LogMessageType.System);
        }

        private void GenerateAuthenticate()
        {

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();

            string payload = $"AUTH{nonce}";

            string signature = ComputeHmacSha384(payload, _secretKey);

            // Create the payload
            var authMessage = new
            {
                @event = "auth",
                apiKey = _publicKey,
                authSig = signature,
                authPayload = payload,
                authNonce = nonce

            };
            string authMessageJson = JsonConvert.SerializeObject(authMessage);

            _webSocketPrivate.Send(authMessageJson);

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
                    _webSocketPublic.State == WebSocketState.Open && _webSocketPrivate.State == WebSocketState.Open)
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
                SendLogMessage("Connection Closed by Bitfinex. WebSocket Private сlosed ", LogMessageType.Error);

                if (ServerStatus != ServerConnectStatus.Disconnect)
                {
                    ServerStatus = ServerConnectStatus.Disconnect;
                    DisconnectEvent();
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
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

                if (WebSocketPrivateMessage == null)
                {
                    return;
                }

                WebSocketPrivateMessage.Enqueue(e.Message);
            }

            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        #endregion

        /// <summary>
        /// Подписка на бумагу.С обязательным контролем скорости и кол-ву запросов к методу Subscrible через rateGate.
        /// </summary>
        #region  8 Security subscrible 

        private RateGate _rateGateSecurity = new RateGate(30, TimeSpan.FromMinutes(1));
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
                _rateGateSecurity.WaitToProceed();

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

        public event Action<Order> MyOrderEvent;

        public event Action<MyTrade> MyTradeEvent;

        private readonly ConcurrentQueue<string> WebSocketPublicMessage = new ConcurrentQueue<string>();

        private readonly ConcurrentQueue<string> WebSocketPrivateMessage = new ConcurrentQueue<string>();

        private Dictionary<int, string> tradeDictionary = new Dictionary<int, string>();
        private Dictionary<int, string> depthDictionary = new Dictionary<int, string>();
        int currentChannelIdDepth;
        int channelIdTrade;
        private void PublicMessageReader()
        {
            Thread.Sleep(1000);

            while (true)
            {
                try
                {
                    if (ServerStatus != ServerConnectStatus.Connect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (WebSocketPublicMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    WebSocketPublicMessage.TryDequeue(out string message);

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
                        tradeDictionary.Add(Convert.ToInt32(responseTrade.ChanId), responseTrade.Symbol);
                        channelIdTrade = Convert.ToInt32(responseTrade.ChanId);
                    }


                    else if (message.Contains("book"))
                    {
                        BitfinexSubscriptionResponse responseDepth = JsonConvert.DeserializeObject<BitfinexSubscriptionResponse>(message);
                        depthDictionary.Add(Convert.ToInt32(responseDepth.ChanId), responseDepth.Symbol);
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

                        // Проверяем, совпадает ли channelId с channelIdDepth
                        if (channelId == currentChannelIdDepth)
                        {
                            SnapshotDepth(message); // Вызов метода обработки снапшота стакана
                        }


                    }

                    if (!message.Contains("[[") && !message.Contains("te") && !message.Contains("tu") && !message.Contains("ws") && !message.Contains("event") && !message.Contains("hb"))
                    {
                        UpdateDepth(message/*, currentChannelIdDepth*/);


                    }

                    if ((message.Contains("te") || message.Contains("tu")) && channelIdTrade != 0)//\"te\"
                    {

                        UpdateTrade(message);
                    }

                    else if (message.Contains("[0,\"tu\",[")/*message.Contains("te") && channelIdTrade == 0*/ )
                    {
                        UpdateMyTrade(message);
                    }
                }

                catch (Exception exception)
                {
                    SendLogMessage(exception.ToString(), LogMessageType.Error);
                }
            }
        }


        public string GetSymbolByKeyInTrades(int channelId)
        {
            string symbol = "";

            if (tradeDictionary.TryGetValue(channelId, out symbol))
            {
                return symbol;
            }

            return null; // Или любое другое значение по умолчанию
        }
        private void UpdateTrade(string message)//[10098,\"tu\",[1657561837,1726071091967,-28.61178052,0.1531]]"/    jsonMessage	"[171733,\"te\",[1660221249,1727123813028,0.001652,63473]]"	string
        {
            if (message.Contains("tu"))
            {
                return;
            }
            try
            {
                var root = JsonConvert.DeserializeObject<List<object>>(message);

                // Проверяем наличие нужного индекса
                if (root.Count > 2 && root[2] != null)
                {
                    // Десериализуем элемент по индексу 2 как массив объектов
                    var tradeData = JsonConvert.DeserializeObject<List<object>>(root[2].ToString());

                    // Парсим строку как массив
                    int channelId = Convert.ToInt32(root[0]);


                    if (tradeData != null && tradeData.Count >= 4)
                    {
                        Trade newTrade = new Trade();
                        newTrade.SecurityNameCode = GetSymbolByKeyInTrades(channelId);
                        newTrade.Id = tradeData[0].ToString();   // ID сделки
                        decimal tradeAmount = tradeData[2].ToString().ToDecimal();// Объём сделки (может быть отрицательным)
                        newTrade.Price = tradeData[3].ToString().ToDecimal();  // Цена сделки
                        newTrade.Volume = Math.Abs(tradeAmount); // Абсолютное значение объёма
                        newTrade.Side = tradeAmount > 0 ? Side.Buy : Side.Sell; // Определяем сторону сделки
                        newTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(tradeData[1]));


                        /* ServerTime = newTrade.Time;  */// Временная метка сделки

                        NewTradesEvent?.Invoke(newTrade);
                    }

                }
                else
                {
                    // Обработка ошибки: недостаточное количество элементов
                    SendLogMessage("Недостаточно данных в JSON или элемент [2] отсутствует.", LogMessageType.Error);
                }
            }

            catch (Exception exception)
            {

                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        private void PrivateMessageReader()
        {
            Thread.Sleep(1000);

            while (true)
            {
                try
                {
                    if (WebSocketPrivateMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    if (ServerStatus != ServerConnectStatus.Connect)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    WebSocketPrivateMessage.TryDequeue(out string message);

                    if (message == null)
                    {
                        continue;
                    }

                    if (message.Contains("info") || message.Contains("auth"))
                    {
                        //continue;
                    }

                    if (message.Contains("ou") || message.Contains("on") || message.Contains("oc"))//Создание ордера (on) — подтверждение, что ордер был создан.// Изменение ордера(ou) — обновления статуса ордера, например, изменения цены или количества.

                    //Исполнение ордера(oc) — уведомление о том, что ордер исполнен(или отменён).
                    {
                        UpdateOrder(message);

                    }
                    if (message.Contains("[0,\"tu\",["))
                    {
                        UpdateMyTrade(message);

                    }
                    if (message.Contains("ws"))
                    {
                        // UpdatePortfolio(message);
                    }
                }
                catch (Exception exception)
                {
                    SendLogMessage(exception.ToString(), LogMessageType.Error);
                }
            }
        }

        private void UpdateMyTrade(string message)
        {
            try
            {

                List<List<object>> tradyList = JsonConvert.DeserializeObject<List<List<object>>>(message);

                if (tradyList == null)
                {
                    return;
                }

                List<BitfinexMyTrade> ListMyTrade = new List<BitfinexMyTrade>(); ///надо или нет.,


                //for (int i = 0; i < 3; i++)
                for (int i = 0; i < tradyList.Count; i++)
                {
                    List<object> item = tradyList[i];

                    BitfinexMyTrade response = new BitfinexMyTrade();//{}


                    MyTrade myTrade = new MyTrade();

                    myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(response.MtsCreate));
                    myTrade.SecurityNameCode = response.Symbol;
                    myTrade.NumberOrderParent = response.Cid;//что тут должно быть
                    myTrade.Price = response.OrderPrice.ToDecimal();
                    myTrade.NumberTrade = response.OrderId;//что тут должно быт
                    myTrade.Side = response.ExecAmount.Contains("-") ? Side.Sell : Side.Buy;
                    // myTrade.Side = response.Amount > 0 ? Side.Buy : Side.Sell;
                    // myTrade.Volume = (response.Amount).ToString().ToDecimal(),


                    // при покупке комиссия берется с монеты и объем уменьшается и появляются лишние знаки после запятой
                    decimal preVolume = myTrade.Side == Side.Sell ? response.ExecAmount.ToDecimal() : response.ExecAmount.ToDecimal() - response.Fee.ToDecimal();

                    myTrade.Volume = GetVolumeForMyTrade(response.Symbol, preVolume);


                    MyTradeEvent?.Invoke(myTrade);

                    SendLogMessage(myTrade.ToString(), LogMessageType.Trade);
                }
            }
            catch (Exception exception)
            {
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }

        //округление объемом
        private readonly Dictionary<string, int> _decimalVolume = new Dictionary<string, int>();
        // метод для округления знаков после запятой
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
                    return Math.Truncate(preVolume * forTruncate) / forTruncate; // при округлении может получиться больше доступного объема, поэтому обрезаем
                }
            }
            return preVolume;
        }

        private void UpdateOrder(string message)
        {
            try
            {
                List<object> rootArray = JsonConvert.DeserializeObject<List<object>>(message);

                // Извлекаем третий элемент, который является массивом
                string thirdElement = rootArray[2].ToString();

                // Десериализуем третий элемент как объект с типами, соответствующими структуре
                BitfinexResponseOrder response = JsonConvert.DeserializeObject<BitfinexResponseOrder>(thirdElement);

                // Теперь можно получить данные ордера
                List<BitfinexOrderData> orderResponse = response.OrderData;

                // Десериализация сообщения в объект BitfinexOrderData
                // var response = JsonConvert.DeserializeObject<List<BitfinexOrderData>>(message);

                if (response == null)
                {
                    return;
                }


                if (message.Contains("[0,oc)"))
                {
                    SendLogMessage($"Order cancel, id: {thirdElement}", LogMessageType.Error);
                }
                if (message.Contains("[0,on)"))
                {
                    SendLogMessage($"New order , id:{thirdElement}", LogMessageType.Error);
                }
                if (message.Contains("[0,ou)"))
                {
                    SendLogMessage($"Update order , id:{thirdElement}", LogMessageType.Error);
                }

                // Перебор всех ордеров в ответе
                for (int i = 0; i < orderResponse.Count; i++)
                {
                    BitfinexOrderData orderData = orderResponse[i];

                    if (string.IsNullOrEmpty(orderData.Cid))
                    {
                        continue;
                    }

                    // Определение состояния ордера
                    OrderStateType stateType = GetOrderState(orderData.Status);

                    // Игнорируем ордера типа "EXCHANGE MARKET" и активные
                    if (orderData.OrderType.Equals("EXCHANGE LIMIT", StringComparison.OrdinalIgnoreCase) && stateType == OrderStateType.Active)
                    {
                        continue;
                    }

                    // Создаем новый объект ордера
                    Order updateOrder = new Order();

                    updateOrder.SecurityNameCode = orderData.Symbol;
                    updateOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData.MtsCreate));
                    updateOrder.NumberUser = Convert.ToInt32(orderData.Cid);
                    updateOrder.NumberMarket = orderData.Id;
                    updateOrder.Side = orderData.Amount.Equals("-") ? Side.Sell : Side.Buy; // Продаем, если количество отрицательное
                    updateOrder.State = stateType;
                    updateOrder.TypeOrder = orderData.OrderType.Equals("EXCHANGE MARKET", StringComparison.OrdinalIgnoreCase) ? OrderPriceType.Market : OrderPriceType.Limit;
                    updateOrder.Volume = Math.Abs(orderData.Amount.ToDecimal()); // Абсолютное значение объема
                    updateOrder.Price = orderData.Price.ToDecimal();
                    updateOrder.ServerType = ServerType.Bitfinex;
                    updateOrder.VolumeExecute = orderData.AmountOrig.ToDecimal();////////////////////
                    updateOrder.PortfolioNumber = "BitfinexPortfolio";


                    // Если ордер исполнен или частично исполнен, обновляем сделку
                    if (stateType == OrderStateType.Done || stateType == OrderStateType.Partial) ///////////
                    {
                        UpdateMyTrade(message);
                    }

                    // Вызываем событие для обновленного ордера
                    MyOrderEvent?.Invoke(updateOrder);

                }
            }
            catch (Exception exception)
            {
                // Логируем ошибку
                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }
        }


        private OrderStateType GetOrderState(string orderStateResponse)
        {
            // Объявляем переменную для хранения состояния ордера
            OrderStateType stateType = OrderStateType.None; // Начальное состояние

            switch (orderStateResponse)
            {
                case "ACTIVE":
                    stateType = OrderStateType.Active; // Активный ордер
                    break;

                case "EXECUTED":
                    stateType = OrderStateType.Done; // Исполненный ордер
                    break;

                case "REJECTED":
                    stateType = OrderStateType.Fail; // Отклонённый ордер
                    break;

                case "CANCELED":
                    stateType = OrderStateType.Cancel; // Отменённый ордер
                    break;

                case "PARTIALLY FILLED":
                    stateType = OrderStateType.Partial; // Частично исполненный ордер
                    break;

                default:
                    stateType = OrderStateType.None; // Неопределённое состояние
                    break;
            }

            return stateType;
        }

        #endregion


        /// <summary>
        /// посвящённый торговле. Выставление ордеров, отзыв и т.д
        /// </summary>
        #region  10 Trade

        private readonly RateGate _rateGateSendOrder = new RateGate(90, TimeSpan.FromMinutes(1));

        private readonly RateGate _rateGateCancelOrder = new RateGate(90, TimeSpan.FromMinutes(1));

        public void SendOrder(Order order)
        {
            _rateGateSendOrder.WaitToProceed();

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/w/order/submit";

            BitfinexOrderData newOrder = new BitfinexOrderData();

            newOrder.Cid = order.NumberUser.ToString();
            newOrder.Id = order.NumberMarket;//пустой id
            newOrder.Symbol = order.SecurityNameCode;

            if (order.TypeOrder.ToString() == "Limit") //приходит лимит,а должен маркет
            {
                newOrder.OrderType = "EXCHANGE LIMIT";
            }
            else
            {
                newOrder.OrderType = "EXCHANGE MARKET";
            }
            // newOrder.OrderType = order.TypeOrder.ToString() == "Limit" ? "EXCHANGE LIMIT" : "EXCHANGE MARKET";
            newOrder.Price = order.TypeOrder == OrderPriceType.Market ? null : order.Price.ToString().Replace(",", ".");
            newOrder.MtsCreate = order.TimeCreate.ToString();
            newOrder.Status = order.State.ToString();
            newOrder.MtsUpdate = order.TimeDone.ToString();//////////

            if (order.Side.ToString() == "Sell")
            {
                //newOrder.Amount = (-order.Volume).ToString(CultureInfo.InvariantCulture);
                newOrder.Amount = (-order.Volume).ToString().Replace(",", ".");
            }
            else
            {
                //newOrder.Amount = order.Volume.ToString(CultureInfo.InvariantCulture);
                newOrder.Amount = (order.Volume).ToString().Replace(",", ".");
            }

            string body = $"{{\"type\":\"{newOrder.OrderType}\",\"symbol\":\"{newOrder.Symbol}\",\"amount\":\"{newOrder.Amount}\",\"price\":\"{newOrder.Price}\"}}";

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


                    // string responseBody = "[1723557977,\"on-req\",null,null,[[167966185075,null,1723557977011,\"tTRXUSD\",1723557977011,1723557977011,22,22,\"EXCHANGE LIMIT\",null,null,null,0,\"ACTIVE\",null,null,0.12948,0,0,0,null,null,null,0,0,null,null,null,\"API>BFX\",null,null,{}]],null,\"SUCCESS\",\"Submitting 1 orders.\"]";

                    // Десериализация верхнего уровня в список объектов
                    List<object> responseArray = JsonConvert.DeserializeObject<List<object>>(responseBody);

                    if (responseArray == null)
                    {
                        return;
                    }

                    //// Путь к статусу "ACTIVE" в JSON структуре
                    //string status = (string)jsonArray[4][0][13];

                    //if (responseArray.Contains("on-req"))
                    //{
                    //    // Извлечение нужных элементов
                    string dataJson = responseArray[4].ToString();
                    // string status = jsonArray[4][0][13].ToString();

                    //string status = responseArray[6].ToString();
                    string text = responseArray[7].ToString();

                    // Десериализация dataJson в список заказов
                    List<List<object>> ordersArray = JsonConvert.DeserializeObject<List<List<object>>>(dataJson);
                    List<object> orders = ordersArray[0]; // Получаем первый заказ из массива

                    // Создание объекта BitfinexOrderData
                    BitfinexOrderData orderData = new BitfinexOrderData();


                    //Cid = Convert.ToString(orders[2]),
                    orderData.Id = orders[0].ToString();
                    orderData.Symbol = orders[3].ToString();
                    orderData.Status = orders[13].ToString();

                    OrderStateType stateType = GetOrderState(orderData.Status);

                    if (orderData.Id != null)
                    {
                        order.NumberMarket = orderData.Id;


                    }


                    order.State = stateType;

                    SendLogMessage($"Order num {order.NumberMarket} on exchange.{text}", LogMessageType.Trade);


                    PortfolioEvent?.Invoke(_portfolios);///////////////

                    //UpdatePortfolio(_portfolios);

                    //если ордер исполнен, вызываем MyTradeEvent
                    if (order.State == OrderStateType.Done
                        || order.State == OrderStateType.Partial)
                    {

                        // UpdateMyTrade(message);
                        newOrder.MtsUpdate = order.TimeDone.ToString(); //надо переносить вниз?
                    }

                    GetPortfolios();
                }
                else
                {
                    SendLogMessage($"Error Order exception {response.Content}", LogMessageType.Error);
                    CreateOrderFail(order);
                }
            }
            catch (Exception exception)
            {
                CreateOrderFail(order);
                SendLogMessage("Order send exception " + exception.ToString(), LogMessageType.Error);

            }
            MyOrderEvent?.Invoke(order);
        }
        private void CreateOrderFail(Order order)
        {
            order.State = OrderStateType.Fail;
            MyOrderEvent?.Invoke(order);
        }

        private readonly RateGate rateGateCancelAllOrder = new RateGate(90, TimeSpan.FromMinutes(1));
        public void CancelAllOrders()
        {
            rateGateCancelAllOrder.WaitToProceed();

            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/w/order/cancel/multi";

            string body = $"{{\"all\":1}}";//1 отменить все ордера Идентификатор ордера для отмены

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
                    // Десериализация верхнего уровня в список объектов
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

            //если ордер уже отменен ничего не делаем
            if (order.State == OrderStateType.Cancel)//если ордер активный можно снять
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

                    //1732544442238, "oc-req", null, null, [184220863794, null, 1732543350365, "tTRXUSD", 1732543350366, 1732543350366, 23, 23, "EXCHANGE LIMIT", null, null, null, 0, "ACTIVE", null, null, 0.1043, 0, 0, 0, null, null, null, 0, 0, null, null, null, "API>BFX", null, null,{ }],null,"SUCCESS","Submitted for cancellation; waiting for confirmation (ID: 184220863794)."]
                    // Выводим тело ответа
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

        private RateGate rateGateChangePriceOrder = new RateGate(90, TimeSpan.FromMinutes(1));


        public void ChangeOrderPrice(Order order, decimal OldPrice/*, string amount*/)// еще можно менять объем
        {

            rateGateChangePriceOrder.WaitToProceed();
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            try
            {
                // Проверка типа ордера
                if (order.TypeOrder == OrderPriceType.Market)
                {
                    SendLogMessage("Can't change price for  Order Market", LogMessageType.Error);
                    return;
                }

                string _apiPath = "v2/auth/w/order/update";
                string price = OldPrice.ToString();

                string body = $"{{\"id\":{order.NumberMarket},\"price\":{price}}}";

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

                int qty = Convert.ToInt32(order.Volume - order.VolumeExecute);

                if (qty <= 0 || order.State != OrderStateType.Active)
                {
                    SendLogMessage("Can't change price for the order. It is not in Active state", LogMessageType.Error);
                    return;
                }
                if (order.State == OrderStateType.Cancel)//если ордер активный можно снять
                {
                    return;
                }

                // if(order.State == OrderStateType.Activ)

                if (response.StatusCode == HttpStatusCode.OK)
                {  // Выводим тело ответа
                    string responseBody = response.Content;

                    string newPrice = responseBody;
                    // ПЕРЕДЕЛАТЬ!!!!!!!!!
                    order.Price = newPrice.ToDecimal();/////////////////


                    //SendLogMessage("Order change price. New price: " + newPrice
                    //  + "  " + order.SecurityNameCode, LogMessageType.Trade);//LogMessageType.System

                }
                else
                {
                    SendLogMessage("Change price order Fail. Status: "
                                + response.Content + "  " + order.SecurityNameCode, LogMessageType.Error);

                    if (response.Content != null)
                    {
                        SendLogMessage("Fail reasons: "
                      + response.Content, LogMessageType.Error);
                    }
                }
                // Вызов события изменения ордера
                MyOrderEvent?.Invoke(order);
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


        public List<Order> GetAllOrdersFromExchange()
        {

            // post https://api.bitfinex.com/v2/auth/r/orders

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
                    if (responseBody.Contains("[]"))
                    {
                        SendLogMessage("Don't have open positions on the exchange", LogMessageType.Trade);

                    }

                    List<List<object>> listOrders = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);

                    List<BitfinexOrderData> activeOrders = new List<BitfinexOrderData>();

                    if (orders != null && orders.Count > 0)
                    {
                        for (int i = 0; i < orders.Count; i++)
                        {
                            Order activOrder = new Order();

                            activOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(long.Parse(activeOrders[i].MtsUpdate));
                            activOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(long.Parse(activeOrders[i].MtsCreate));
                            activOrder.ServerType = ServerType.Bitfinex;
                            activOrder.SecurityNameCode = activeOrders[i].Symbol;
                            activOrder.NumberUser = Convert.ToInt32(activeOrders[i].Cid);
                            activOrder.NumberMarket = activeOrders[i].Id;
                            activOrder.Side = activeOrders[i].Amount.Equals("-") ? Side.Sell : Side.Buy;
                            activOrder.State = GetOrderState(activeOrders[i].Status);
                            activOrder.Volume = activeOrders[i].Amount.ToDecimal();
                            activOrder.Price = activeOrders[i].Price.ToDecimal();
                            activOrder.VolumeExecute = activeOrders[i].AmountOrig.ToDecimal();
                            activOrder.PortfolioNumber = "BitfinexPortfolio";

                            orders.Add(activOrder);

                            //orders[i].TimeCreate = orders[i].TimeCallBack;

                            MyOrderEvent?.Invoke(orders[i]);
                        }
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
            // Получаем ордер с биржи по рыночному номеру ордера
            Order orderFromExchange = GetOrderFromExchange(order.NumberMarket);

            if (orderFromExchange == null)
            {
                return;
            }
            // Объявляем переменную для хранения ордера на рынке
            Order orderOnMarket = null;

            // Если пользовательский номер ордера (NumberUser) и номер ордера с биржи (NumberUser) совпадают, сохраняем ордер с биржи
            if (order.NumberUser != 0 && orderFromExchange.NumberUser != 0 && orderFromExchange.NumberUser == order.NumberUser)
            {
                orderOnMarket = orderFromExchange;
            }

            // Если рыночный номер ордера (NumberMarket) совпадает, также сохраняем ордер с биржи
            if (!string.IsNullOrEmpty(order.NumberMarket) && order.NumberMarket == orderFromExchange.NumberMarket)
            {
                orderOnMarket = orderFromExchange;
            }

            // Если ордер на рынке не найден, выходим из метода
            if (orderOnMarket == null)
            {
                return;
            }

            // Если ордер на рынке найден и существует обработчик события, вызываем событие MyOrderEvent
            if (orderOnMarket != null && MyOrderEvent != null)
            {
                MyOrderEvent(orderOnMarket);
            }

            // Проверяем состояние ордера: если ордер выполнен (Done) или частично выполнен (Patrial)
            if (orderOnMarket.State == OrderStateType.Done || orderOnMarket.State == OrderStateType.Partial)
            {
                // Получаем список сделок по номеру ордера
                List<MyTrade> tradesBySecurity = GetMyTradesBySecurity(order.SecurityNameCode, order.NumberMarket);

                // Если сделки не найдены, выходим из метода
                if (tradesBySecurity == null)
                {
                    return;
                }

                // Объявляем список для хранения сделок, связанных с данным ордером
                List<MyTrade> tradesByMyOrder = new List<MyTrade>();

                // Используем цикл for для перебора всех сделок в списке tradesBySecurity
                for (int i = 0; i < tradesBySecurity.Count; i++)
                {
                    // Если сделка связана с данным ордером (по совпадению родительского номера ордера), добавляем её в список
                    if (tradesBySecurity[i].NumberOrderParent == orderOnMarket.NumberMarket)
                    {
                        tradesByMyOrder.Add(tradesBySecurity[i]);
                    }
                }

                // Используем цикл for для обработки всех найденных сделок по ордеру
                for (int i = 0; i < tradesByMyOrder.Count; i++)
                {
                    // Если существует обработчик события MyTradeEvent, вызываем его для каждой сделки
                    MyTradeEvent?.Invoke(tradesByMyOrder[i]);
                }
            }
        }

        private Order GetOrderFromExchange(string numberMarket)
        {
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            string _apiPath = "v2/auth/r/orders";

            string body = $"{{\"id\":[{numberMarket}]}}";////надо или нет квадратные скобки

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

            Order newOrder = new Order();
            try
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var data = JsonConvert.DeserializeObject<List<List<object>>>(response.Content);
                    //[[184206213491,null,1732536416875,"tTRXUSD",1732536416876,1732536416876,22,22,"EXCHANGE LIMIT",null,null,null,0,"ACTIVE",null,null,0.0958,0,0,0,null,null,null,0,0,null,null,null,"API>BFX",null,null,{}]]

                    if (data != null && data.Count > 0 && data[0].Count > 0)
                    {
                        List<object> orderData = data[0]; // Берём первый массив данных из массива


                        //    for (int i = 0; i < listOrder.Count; i++)
                        //{
                        //BitfinexOrderData order = listOrder[i];

                        newOrder.NumberMarket = orderData[0].ToString();
                        newOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[4]));
                        newOrder.TimeCreate = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(orderData[5]));
                        newOrder.ServerType = ServerType.Bitfinex;
                        newOrder.SecurityNameCode = orderData[3].ToString();
                        newOrder.Side = orderData[6].Equals("-") ? Side.Sell : Side.Buy;
                        newOrder.State = GetOrderState(orderData[13].ToString());
                        decimal volume = orderData[6].ToString().ToDecimal();

                        if (volume < 0)
                        {
                            newOrder.Volume = -Math.Abs(volume);
                        }
                        else
                        {
                            newOrder.Volume = volume;
                        }
                        newOrder.Price = orderData[16].ToString().ToDecimal();
                        newOrder.PortfolioNumber = "BitfinexPortfolio";
                        newOrder.VolumeExecute = orderData[7].ToString().ToDecimal();
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
            return newOrder;

        }

        private List<MyTrade> GetMyTradesBySecurity(string symbol, string orderId)
        {
            string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString();
            try
            {
                // https://api.bitfinex.com/v2/auth/r/trades/{symbol}/hist

                string _apiPath = $"v2/auth/r/orders/{symbol}";

                string body = $"{{\"id\":\"{orderId}\",\"symbol\":\"{symbol}\"}}";

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

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    //string responseBody = response.Content;

                    CreateQueryPosition();
                }
                else
                {
                    SendLogMessage($" {response.Content}", LogMessageType.Error);
                }

                return new List<MyTrade>();
            }
            catch (Exception exception)
            {

                SendLogMessage(exception.ToString(), LogMessageType.Error);
            }

            return null;
        }


        #endregion
        /// <summary>
        /// Логирование.
        /// </summary>


        public void GetAllActivOrders()
        {
            // https://api.bitfinex.com/v2/auth/r/orders

            List<Order> orders = GetAllOrdersFromExchange();

            for (int i = 0; orders != null && i < orders.Count; i++)
            {
                if (orders[i] == null)
                {
                    continue;
                }

                if (orders[i].State != OrderStateType.Active
                    && orders[i].State != OrderStateType.Partial
                    && orders[i].State != OrderStateType.Pending)
                {
                    continue;
                }

                orders[i].TimeCreate = orders[i].TimeCallBack;

                MyOrderEvent?.Invoke(orders[i]);
            }
        }


        #region 12 Log

        public event Action<string, LogMessageType> LogMessageEvent;
        private void SendLogMessage(string message, LogMessageType messageType)
        {
            LogMessageEvent(message, messageType);
        }
        #endregion


        #region Методы,которые, могут пригодиться



        #endregion



    }
}





