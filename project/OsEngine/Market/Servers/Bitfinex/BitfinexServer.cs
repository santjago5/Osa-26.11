


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

        //   private  MarketDepth marketDepth = new MarketDepth();
        private List<MarketDepth> marketDepths;
        List<MarketDepthLevel> ascs = new List<MarketDepthLevel>();
        List<MarketDepthLevel> bids = new List<MarketDepthLevel>();

        public string GetSymbolByKeyInDepth(int channelId)
        {
            string symbol = "";

            if (depthDictionary.TryGetValue(channelId, out symbol))
            {
                return symbol;
            }

            return null; // Или любое другое значение по умолчанию
        }

        // Класс для хранения уровня стакана


        // Метод для обработки снапшота стакана
        public void SnapshotDepth(string jsonResponse)
        {
            //Очистим предыдущие данные стакана
            //marketDepths.Clear();
            //marketDepths.Clear();

            JArray root = JArray.Parse(jsonResponse);

            int channelId = Convert.ToInt32(root[0]);
            JArray snapshot = (JArray)root[1];


            string symbol = GetSymbolByKeyInDepth(channelId);


            if (marketDepths == null)
            {
                marketDepths = new List<MarketDepth>();
            }

            var needDepth = marketDepths.Find(depth =>
                depth.SecurityNameCode == symbol);

            if (needDepth == null)
            {
                needDepth = new MarketDepth();
                needDepth.SecurityNameCode = symbol;
                marketDepths.Add(needDepth);
            }

            // marketDepth.SecurityNameCode = symbol;

            // Обрабатываем снапшот
            foreach (var entry in snapshot)
            {
                decimal price = entry[0].ToString().ToDecimal();  // Цена уровня
                decimal count = entry[1].ToString().ToDecimal();  // Количество заявок
                decimal amount = entry[2].ToString().ToDecimal();  // Объем

                if (amount > 0) // Биды
                {
                    //marketDepth.Bids.Add(new MarketDepthLevel
                    //{
                    //    Price = price,
                    //    Bid = amount,

                    //});

                    bids.Add(new MarketDepthLevel()
                    {
                        Price = price,
                        Bid = amount,
                    });
                }
                else if (amount < 0) // Аски
                {
                    //marketDepth.Asks.Add(new MarketDepthLevel
                    //{
                    //    Price = price,
                    //    Ask = Math.Abs(amount) // Объем аска
                    //});

                    ascs.Add(new MarketDepthLevel()
                    {
                        Price = price,
                        Ask = Math.Abs(amount)
                    });
                }
            }
            needDepth.Asks = ascs;
            needDepth.Bids = bids;
            needDepth.Time = ServerTime;
            //Сортируем и обрезаем стакан
            // TrimAndSortDepth();

            // Вызываем событие обновления стакана
            //MarketDepthEvent?.Invoke(marketDepth/*.GetCopy()*/);
            MarketDepthEvent?.Invoke(needDepth/*.GetCopy()*/);
        }

        // Метод для сортировки и ограничения размеров стакана
        //private void TrimAndSortDepth()
        //{
        //    //// Сортируем биды по убыванию цены
        //    //marketDepth.Bids.Sort((b1, b2) => b2.Price.CompareTo(b1.Price));

        //    //// Сортируем аски по возрастанию цены
        //    //marketDepth.Asks.Sort((a1, a2) => a1.Price.CompareTo(a2.Price));

        //    // Обрезаем списки до 25 уровней
        //    if (marketDepth.Bids.Count > 25)
        //        marketDepth.Bids.RemoveRange(25, marketDepth.Bids.Count - 25);

        //    if (marketDepth.Asks.Count > 25)
        //        marketDepth.Asks.RemoveRange(25, marketDepth.Asks.Count - 25);
        //}

        public void UpdateDepth(string message)
        {

            if (message == null)
            {
                return;
            }

            var root = JsonConvert.DeserializeObject<List<object>>(message);

            int channelId = Convert.ToInt32(root[0]);  // Преобразуем элемент в тип long
            string symbol = GetSymbolByKeyInDepth(channelId);
            if (root == null || root.Count < 2)
            {
               SendLogMessage("Сообщение имеет недостаточное количество элементов.", LogMessageType.Error);
                return;
            }
            if (marketDepths == null)
            {
                return;
            }
            var needDepth = marketDepths.Find(depth =>
                depth.SecurityNameCode == symbol);

            if (needDepth == null)
            {
                return;
            }

            var bookEntry = JsonConvert.DeserializeObject<List<object>>(root[1].ToString());

        
                // Парсим элементы массива BOOK_ENTRY
                decimal price = bookEntry[0].ToString().ToDecimal();
                int count = Convert.ToInt32(bookEntry[1].ToString());
               decimal amount = bookEntry[2].ToString().ToDecimal();


            if (count > 0)   // Если количество > 0, обновляем или добавляем уровень
            {
                if (amount > 0)
                {
                    // Добавляем или обновляем бид
                    //var existingBid = marketDepth.Bids.Find(b => b.Price == price);
                    var needLevel = needDepth.Bids.Find(bid => bid.Price == price);
                    //if (existingBid != null)
                    //{
                    //    // Обновляем существующий бид
                    //    existingBid.Bid = amount;
                    //}
                    if (needLevel == null)  // if there is no such level, add it / если такого уровня нет, добавляем его
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
                        // Добавляем новый бид
                        // marketDepth.Bids.Add(new MarketDepthLevel { Price = price, Bid = amount });
                        needLevel.Bid = amount;
                    }

                }
                else if (amount < 0)
                {
                    //    // Добавляем или обновляем аск
                    //    var existingAsk = marketDepth.Asks.Find(a => a.Price == price);
                    //    if (existingAsk != null)
                    //    {
                    //        // Обновляем существующий аск
                    //        existingAsk.Ask = amount;
                    //    }
                    //    else
                    //    {
                    //        // Добавляем новый аск
                    //        marketDepth.Asks.Add(new MarketDepthLevel { Price = price, Ask = amount });
                    //    }
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
                // return;
            }
            // Если количество == 0, удаляем уровень
            else if (count == 0)
            {
                if (amount == 1)
                {
                    // Удаляем из бидов
                    // marketDepth.Bids.RemoveAll(b => b.Price == price);
                    needDepth.Bids.Remove(needDepth.Bids.Find(level => level.Price == price));
                }
                else if (amount == -1)
                {
                    // Удаляем из асков
                    // marketDepth.Asks.RemoveAll(a => a.Price == price);
                    needDepth.Asks.Remove(needDepth.Asks.Find(level => level.Price == price));
                }
                //return;
            }

            // Обрезаем до 25 элементов
            if (needDepth.Bids.Count > 25)
                needDepth.Bids = needDepth.Bids.Take(25).ToList();
            if (needDepth.Asks.Count > 25)
                needDepth.Asks = needDepth.Asks.Take(25).ToList();
            //needDepth.Bids = needDepth.Bids.Count > 25 ? needDepth.Bids.Take(25).ToList() : needDepth.Bids;
            //needDepth.Asks = needDepth.Asks.Count > 25 ? needDepth.Asks.Take(25).ToList() : needDepth.Asks;

            //if (needDepth.Asks.Count < 2 || needDepth.Bids.Count < 2)
            //{
            //    return;
            //}

            //if (marketDepth.Asks[0].Price > marketDepth.Asks[1].Price)
            //{
            //    marketDepth.Asks.RemoveAt(0);
            //}
            //if (marketDepth.Bids[0].Price < marketDepth.Bids[1].Price)
            //{
            //    marketDepth.Asks.RemoveAt(0);
            //}

            //if (marketDepth.Asks[0].Price < marketDepth.Bids[0].Price)
            //{
            //    if (marketDepth.Asks[0].Price < marketDepth.Bids[1].Price)
            //    {
            //        marketDepth.Asks.Remove(marketDepth.Asks[0]);
            //    }
            //    else if (marketDepth.Bids[0].Price > marketDepth.Asks[1].Price)
            //    {
            //        marketDepth.Bids.Remove(marketDepth.Bids[0]);
            //    }
            //}

            //marketDepth.Bids.Sort((b1, b2) => b2.Price.CompareTo(b1.Price)); // Сортировка бидов по убыванию
            //marketDepth.Asks.Sort((a1, a2) => a1.Price.CompareTo(a2.Price)); // Сортировка асков по возрастанию

            /// Обрезаем до 25 элементов
            //if (marketDepth.Bids.Count > 25)
            //    marketDepth.Bids = marketDepth.Bids.Take(25).ToList();
            //if (marketDepth.Asks.Count > 25)
            //    marketDepth.Asks = marketDepth.Asks.Take(25).ToList();

            //    if (marketDepth.Asks.Count == 0 || marketDepth.Bids.Count == 0)
            //    {
            //        return;
            //    }
            needDepth.Time = DateTime.UtcNow;

            if (MarketDepthEvent != null)
            {
                MarketDepthEvent(needDepth);
            }
        }





        //private void ClientOnUpdateMarketDepth(string newData, string nameSecurity)
        //{
        //    JArray root = JArray.Parse(newData);  // Парсим строку как массив JSON

        //    string channelId = root[0].ToString();  // Преобразуем элемент в тип long
        //    marketDepth.SecurityNameCode = GetSymbolByKeyInDepth(channelId);

        //    JToken data = root[1];

        //    try
        //    {
        //        lock (_depthLocker)
        //        {
        //            if (_depths == null)
        //            {
        //                return;
        //            }
        //            var needDepth = _depths.Find(depth =>
        //                depth.SecurityNameCode == nameSecurity);

        //            if (needDepth == null)
        //            {
        //                return;
        //            }

        //            if (root.Count < 4)
        //            {
        //                return;
        //            }

        //            var price = Convert.ToDecimal(data);

        //            var count = Convert.ToDecimal(data);

        //            var amount = Convert.ToDecimal(data);

        //            needDepth.Time = ServerTime;

        //            // if the number of orders is 0, then you need to find the level of this price and delete it / если колл-во ореров равно 0, значит надо найти уровень этой цены и удалить его
        //            if (count == 0)
        //            {
        //                // delete level / удаляем уровень
        //                if (amount < 0)  // delete from asks / удаляем из асков
        //                {
        //                    needDepth.Asks.Remove(needDepth.Asks.Find(level => level.Price == price));
        //                }

        //                if (amount > 0)  // delete from bids / удаляем из бидов
        //                {
        //                    needDepth.Bids.Remove(needDepth.Bids.Find(level => level.Price == price));
        //                }
        //                return;
        //            }

        //            // if the volume is greater than zero, then some bid has changed, we find it and update / если объем больше нуля, значит изменился какой-то бид, находим его и обновляем
        //            else if (amount > 0)
        //            {
        //                var needLevel = needDepth.Bids.Find(bid => bid.Price == price);

        //                if (needLevel == null)  // if there is no such level, add it / если такого уровня нет, добавляем его
        //                {
        //                    needDepth.Bids.Add(new MarketDepthLevel()
        //                    {
        //                        Bid = amount,
        //                        Price = price
        //                    });

        //                    needDepth.Bids.Sort((level, depthLevel) => level.Price > depthLevel.Price ? -1 : level.Price < depthLevel.Price ? 1 : 0);
        //                }
        //                else
        //                {
        //                    needLevel.Bid = amount;
        //                }

        //            }
        //            // if less, then update the ask / если меньше, значит обновляем аск
        //            else if (amount < 0)
        //            {
        //                var needLevel = needDepth.Asks.Find(asc => asc.Price == price);

        //                if (needLevel == null)  // if there is no such level, add it / если такого уровня нет, добавляем его
        //                {
        //                    needDepth.Asks.Add(new MarketDepthLevel()
        //                    {
        //                        Ask = Math.Abs(amount),
        //                        Price = price
        //                    });

        //                    needDepth.Asks.Sort((level, depthLevel) => level.Price > depthLevel.Price ? 1 : level.Price < depthLevel.Price ? -1 : 0);
        //                }
        //                else
        //                {
        //                    needLevel.Ask = Math.Abs(amount);
        //                }

        //            }

        //            if (needDepth.Asks.Count < 2 ||
        //                needDepth.Bids.Count < 2)
        //            {
        //                return;
        //            }

        //            if (needDepth.Asks[0].Price > needDepth.Asks[1].Price)
        //            {
        //                needDepth.Asks.RemoveAt(0);
        //            }
        //            if (needDepth.Bids[0].Price < needDepth.Bids[1].Price)
        //            {
        //                needDepth.Asks.RemoveAt(0);
        //            }

        //            if (needDepth.Asks[0].Price < needDepth.Bids[0].Price)
        //            {
        //                if (needDepth.Asks[0].Price < needDepth.Bids[1].Price)
        //                {
        //                    needDepth.Asks.Remove(needDepth.Asks[0]);
        //                }
        //                else if (needDepth.Bids[0].Price > needDepth.Asks[1].Price)
        //                {
        //                    needDepth.Bids.Remove(needDepth.Bids[0]);
        //                }
        //            }

        //            if (MarketDepthEvent != null)
        //            {
        //                MarketDepthEvent(needDepth.GetCopy());
        //            }
        //        }
        //    }
        //    catch (Exception error)
        //    {
        //        SendLogMessage(error.ToString(), LogMessageType.Error);
        //    }
        //}



        private void WebSocketPrivate_Opened(object sender, EventArgs e)
        {
            GenerateAuthenticate();
            _socketPrivateIsActive = true;//отвечает за соединение
            //CheckActivationSockets();/////// надо или нет
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
        int channelIdDepth;
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


                    else if (message.Contains("info") || message.Contains("hb") || message.Contains("auth"))
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
                        channelIdDepth = Convert.ToInt32(responseDepth.ChanId);
                    }

                    // Проверяем, совпадает ли channelId с channelIdDepth
                    if (message.Contains("[["))
                    {
                        var root = JsonConvert.DeserializeObject<List<object>>(message);
                        int channelId = Convert.ToInt32(root[0]);
                        if (root == null || root.Count < 2)
                        {
                            SendLogMessage("Некорректный формат сообщения: недостаточно элементов.", LogMessageType.Error);
                            return;
                        }

                        // Проверяем, является ли второй элемент массивом массивов (снапшотом стакана)
                        if (channelId == channelIdDepth)
                        {
                            SnapshotDepth(message); // Вызов метода обработки снапшота стакана
                        }
                    }
                    if (!message.Contains("[[") && !message.Contains("te") && !message.Contains("ws") && !message.Contains("event") && !message.Contains("hb"))
                    {
                        UpdateDepth(message);
                    }
                    if (message.Contains("te") && channelIdTrade != 0)//\"te\",
                    {
                        UpdateTrade(message);
                    }
 
                    else if(message.Contains("[0,\"tu\",[")/*message.Contains("te") && channelIdTrade == 0*/ )
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
        private void UpdateTrade(string jsonMessage)//[10098,\"tu\",[1657561837,1726071091967,-28.61178052,0.1531]]"/    jsonMessage	"[171733,\"te\",[1660221249,1727123813028,0.001652,63473]]"	string
        {
            try
            {
                JArray root = JArray.Parse(jsonMessage);  // Парсим строку как массив
                int channelId = Convert.ToInt32(root[0]);
                JArray tradeData = root[2] as JArray;

                if (tradeData != null && tradeData.Count >= 4)
                {
                    Trade newTrade = new Trade();
                    newTrade.SecurityNameCode = GetSymbolByKeyInTrades(channelId);
                    newTrade.Id = tradeData[0].ToString();   // ID сделки
                    long tradeTime = tradeData[1].ToObject<long>(); // Метка времени сделки
                    decimal tradeAmount = tradeData[2].ToString().ToDecimal();// Объём сделки (может быть отрицательным)
                    newTrade.Price = tradeData[3].ToString().ToDecimal();  // Цена сделки

                    newTrade.Volume = Math.Abs(tradeAmount); // Абсолютное значение объёма
                    newTrade.Side = tradeAmount > 0 ? Side.Buy : Side.Sell; // Определяем сторону сделки
                    newTrade.Time = TimeManager.GetDateTimeFromTimeStamp(tradeTime);


                    /* ServerTime = newTrade.Time;  */// Временная метка сделки

                    NewTradesEvent?.Invoke(newTrade);
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
        {//получить все активные ордера
         //  public List<Order> GetAllOrdersFromExchange(int? id = null) 
         //       string _apiPath = id.HasValue
         //? $"v2/auth/r/order/{id.Value}" // Если передан ID
         //: "v2/auth/r/orders";
         //      тело запроса, если есть id  {\"id\":[54564]}

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

        private Order GetOrderFromExchange(string numberMarket)//184072834570
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

        //  string nonce = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000).ToString();



        ////////////////////////////////////////////////////////////отписаться от всех подписок
        //public class BitfinexUnsubscribe
        //{
        //    // Список идентификаторов активных каналов
        //    private List<int> channelIds = new List<int>();

        //    // Метод для отписки от всех событий
        //    public void UnsubscribeAll()
        //    {
        //        try
        //        {
        //            // Проходим по каждому идентификатору канала и отправляем команду отписки
        //            for (int i = 0; i < channelIds.Count; i++)
        //            {
        //                int channelId = channelIds[i];

        //                // Формируем команду отписки для канала
        //                string unsubscribeMessage = $"{{\"event\": \"unsubscribe\", \"chanId\": {channelId}}}";

        //                // Отправляем команду через WebSocket
        //                SendWebSocketMessage(unsubscribeMessage);
        //            }

        //            // Очищаем список после отписки от всех каналов
        //            channelIds.Clear();
        //        }
        //        catch (Exception exception)
        //        {
        //            SendLogMessage(exception.ToString(), LogMessageType.Error);
        //        }
        //    }


        ////////////////////////////////////////////////////////

        //private void UnsubscribeFromAllWebSockets()
        //{
        //    if (_webSocketPublic == null || _webSocketPrivate == null)
        //    { return; }

        //    for (int i = 0; i < _subscribedSecurities.Count; i++)
        //    {
        //        string securityName = _subscribedSecurities[i];

        //        _webSocketPublic.Send($"{{\"event\":\"unsubscribe\", \"chanId\": \"{chanId}\"}}"); 

        //        _webSocketPrivate.Send($"{{\"event\":\"unsubscribe\", \"chanId\": \"{chanId}\"}}");
        //    }
        //}

        //public void UnsubscribeFromChannel(int channelId)//отписаться от канала
        //{
        //    var unsubscribeMessage = new
        //    {
        //        @event = "unsubscribe",
        //        chanId = channelId
        //    };

        //    string message = JsonConvert.SerializeObject(unsubscribeMessage);

        //    // Отправка сообщения через WebSocket
        //    webSocket.Send(message);
        //}


        //private OrderStateType GetOrderState(string orderStateResponse)
        //{
        //    var stateType = orderStateResponse switch
        //    {
        //        "ACTIVE" => OrderStateType.Activ,
        //        "EXECUTED" => OrderStateType.Done,
        //        "REJECTED" => OrderStateType.Fail,
        //        "CANCELED" => OrderStateType.Cancel,
        //        "PARTIALLY FILLED" => OrderStateType.Patrial,
        //        _ => OrderStateType.None,
        //    };
        //    return stateType;
        //}

        //else if (baseMessage.status == "CANCELED")
        //{
        //    lock (_changePriceOrdersArrayLocker)
        //    {
        //        DateTime now = DateTime.UtcNow;
        //        for (int i = 0; i < _changePriceOrders.Count; i++)
        //        {
        //            if (_changePriceOrders[i].TimeChangePriceOrder.AddSeconds(2) < now)
        //            {
        //                _changePriceOrders.RemoveAt(i);
        //                i--;
        //                continue;
        //            }

        //            //if (_changePriceOrders[i].MarketId == order.NumberMarket)
        //            //{
        //            //    return null;
        //            //}
        //        }
        //    }

        //    if (string.IsNullOrEmpty(baseMessage.amount))
        //    {
        //        order.State = OrderStateType.Cancel;
        //    }
        //    else if (baseMessage.amount == "0")
        //    {
        //        order.State = OrderStateType.Cancel;
        //    }
        //    else
        //    {
        //        try
        //        {
        //            decimal volFilled = baseMessage.amount.ToDecimal();

        //            if (volFilled > 0)
        //            {
        //                order.State = OrderStateType.Done;
        //            }
        //            else
        //            {
        //                order.State = OrderStateType.Cancel;
        //            }
        //        }
        //        catch
        //        {
        //            order.State = OrderStateType.Cancel;
        //        }
        //    }
        //}

        //return order;
        //                }

        //if (securityList[i].Contains("e") || securityList[i].Contains("E"))
        //              {

        //    // Преобразуем строку в число, используя double.Parse
        //    double scientificNumber = double.Parse(item[i].ToString(), System.Globalization.NumberStyles.Float);

        // }



        ///////////////////////////////////////////////////////////////
        ///  // PositionOnBoard position = new PositionOnBoard
        //            {
        //                PortfolioName = portfolios[i].Type,
        //                ValueBegin = availableBalance,
        //                ValueCurrent = availableBalance,
        //                ValueBlocked = unsettledInterest,
        //                SecurityNameCode = portfolios[i].Currency
        //            };




        //private void UpdatePortfolio(string message/*bool isUpdateValueBeginList<BitfinexPortfolioRest> portfolios*/)
        //{
        //    try
        //    {
        //        var jsonDoc = JsonDocument.Parse(message);
        //        var root = jsonDoc.RootElement;

        //        List<BitfinexPortfolioRest> response = JsonConvert.DeserializeObject<List<BitfinexPortfolioRest>>(message);

        //         if (root.ValueKind == JsonValueKind.Array)
        //        {
        //            // Handle data messages
        //            int channelId = root[0].GetInt32();
        //            string msgType = root[1].GetString();

        //            if (channelId == 0)
        //            {
        //                // Wallet messages
        //                switch (msgType)
        //                {
        //                    case "ws":
        //                        SendLogMessage("Received wallet snapshot", LogMessageType.System);
        //                       // HandleWalletSnapshot(root[2]);
        //                        break;
        //                    case "wu":
        //                        SendLogMessage("Received wallet update", LogMessageType.System);
        //                       // HandleWalletUpdate(root[2], response);
        //                        break;
        //                }
        //            }
        //        }


        /// <summary>
        /// Проверка вёбсокета на работоспособность путём отправки ему пингов.
        /// </summary>
        //private void SendPing(object sender, EventArgs e)// ElapsedEventArgs e)
        //{
        //    // Проверяем, инициализирован ли _webSocketPublic и открыт ли он
        //    if (_webSocketPublic != null && _webSocketPublic.State == WebSocketState.Open)
        //    {
        //        string Ping = "{\"event\":\"ping\",\"cid\":1234}";

        //        _webSocketPublic.Send(Ping);
        //    }
        //    else
        //    {
        //       ("WebSocket is not open. Ping not sent.");
        //    }


        //}



        #endregion



    }
}





