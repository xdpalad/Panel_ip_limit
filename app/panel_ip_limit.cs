using System;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Threading.Tasks;



class Program
{
    // Глобальная переменная для хранения токена
    static string apiToken = null;
    static DateTime tokenExpiration; // Переменная для хранения времени истечения токена

    // Списки для фильтрации недопустимых email и IP-адресов
    static List<string> INVALID_EMAIL = new List<string> { "API]", "Found", "(normal)", "timeout", "EOF", "address", "INFO", "request" };
    static List<string> INVALID_IPS = new List<string> { "1.1.1.1", "8.8.8.8", "0.0.0.0" };
    static List<string> VALID_IPS = new List<string>(); // Список валидных IP-адресов
    static bool WRITE_LOGS_TF, SEND_LOGS_TO_TEL; // Флаги для логирования и отправки данных в Telegram
    static string LOG_FILE_NAME, TELEGRAM_BOT_URL, CHAT_ID, PANEL_USERNAME, PANEL_PASSWORD, PANEL_DOMAIN, SERVER_NAME; // Параметры конфигурации

    static Dictionary<string, string> last_usage_d = new Dictionary<string, string>(); // Словарь для хранения последнего времени активности пользователей

    static int TIME_TO_CHECK; // ID чата в Telegram и время между проверками
    static int USER_IP_LIMIT; // Лимит адресов для пользователей
    static ConcurrentBag<(string, string)> users_list_l = new ConcurrentBag<(string, string)>(); // Используем ConcurrentBag для одновременной записи и чтения

    static async Task Main(string[] args)
    {
        ReadConfig(); // Чтение конфигурации из JSON файла

        // Получение списка нод и запуск задачи для каждой ноды с указанием id и address
        List<Node> nodes = GetNodes();
        foreach (var node in nodes)
        {
            int n = node.Id;
            string address = node.Address;

            // Запуск задачи для получения логов от каждой ноды
            Task.Run(() => GetLogsRun(n, address));
            Thread.Sleep(2000); // Короткая задержка перед запуском следующей задачи
        }

        // Основной цикл программы
        while (true)
        {
            try
            {
                Job(); // Выполнение основной логики программы: обработка логов и отправка отчетов
                Thread.Sleep(TIME_TO_CHECK * 1000); // Задержка между выполнениями задачи
            }
            catch (Exception ex)
            {
                // Ловим исключения и отправляем логи об ошибке в Telegram и лог-файл
                SendLogsToTelegram($"Error: {ex.Message}");
                WriteLog($"Error: {ex.Message}");
                Console.WriteLine(ex.Message);
                Thread.Sleep(10000); // Задержка перед повторной попыткой
            }
        }
    }


    // Чтение конфигурационного файла JSON
    static void ReadConfig()
    {
        string configFile = "limit_config.json"; // Имя файла конфигурации

        // Получение полного пути к файлу конфигурации относительно запускаемого приложения
        string configFilePath = Path.Combine(AppContext.BaseDirectory, configFile);

        if (File.Exists(configFilePath)) // Проверка, существует ли файл
        {
            var configJson = File.ReadAllText(configFilePath); // Чтение файла конфигурации
            var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(configJson); // Парсинг JSON

            // Чтение и присвоение параметров конфигурации
            WRITE_LOGS_TF = bool.Parse(config["WRITE_LOGS_TF"]?.ToString() ?? "false");
            SEND_LOGS_TO_TEL = bool.Parse(config["SEND_LOGS_TO_TEL"]?.ToString() ?? "false");
            LOG_FILE_NAME = config["LOG_FILE_NAME"]?.ToString() ?? "logs.txt";
            
            // Чтение только токена и построение полного URL для Telegram API
            string telegramBotToken = config["TELEGRAM_BOT_TOKEN"]?.ToString();
            TELEGRAM_BOT_URL = $"https://api.telegram.org/bot{telegramBotToken}/sendMessage";

            CHAT_ID = config["CHAT_ID"]?.ToString();;
            PANEL_USERNAME = config["PANEL_USERNAME"]?.ToString();
            PANEL_PASSWORD = config["PANEL_PASSWORD"]?.ToString();
            PANEL_DOMAIN = config["PANEL_DOMAIN"]?.ToString();
            TIME_TO_CHECK = int.Parse(config["TIME_TO_CHECK"]?.ToString() ?? "60");
            SERVER_NAME = config.ContainsKey("SERVER_NAME") ? config["SERVER_NAME"]?.ToString() : null;

            // Добавление новой переменной USER_IP_LIMIT с проверкой на наличие в файле конфигурации
            USER_IP_LIMIT = int.Parse(config["USER_IP_LIMIT"]?.ToString() ?? "3");
        }
        else
        {
            // Если файл конфигурации не найден, выводим сообщение об ошибке
            Console.WriteLine($"Config file not found at {configFilePath}!");
            Environment.Exit(1); // Прекращаем выполнение программы
        }
    }


// Добавим функцию для записи данных в локальный файл
static void SaveLogsToFile(string filePath, string logData)
{
    try
    {
        // Открываем файл для добавления (если файл не существует, он будет создан)
        using (StreamWriter sw = new StreamWriter(filePath, true))
        {
            sw.WriteLine(logData); // Записываем данные в файл
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error writing to log file: {ex.Message}");
    }
}

// Измененный метод GetLogsRun
static async Task GetLogsRun(int id, string address)
{
    string logFilePath = "received_logs.txt"; // Путь к файлу для записи всех логов

    while (true) // Бесконечный цикл для постоянного получения данных
    {
        try
        {
            var token = GetToken(); // Получение токена для API
            // Формирование URL для подключения к веб-сокетам (зависит от id ноды)
            string url = $"wss://{PANEL_DOMAIN}/api/node/{id}/logs?token={token}";

            using (var webSocket = new ClientWebSocket()) // Инициализация веб-сокета
            {
                await webSocket.ConnectAsync(new Uri(url), CancellationToken.None); // Подключение к веб-сокету
                Console.WriteLine($"Connected to WebSocket (node {id}, address {address})");

                // Отправляем сообщение в лог и Telegram об успешном подключении
                string connectMessage = $"✅ Node {id} ({address}): Successfully connected to WebSocket.";
                WriteLog(connectMessage);
                SendLogsToTelegram(connectMessage);

                var buffer = new byte[512 * 1024]; // Буфер на получение данных на 512 КБ для каждого WebSocket-соединения
                var messageBuffer = new List<byte>(); // Список для накопления всех частей сообщения

                while (webSocket.State == WebSocketState.Open) // Пока соединение открыто
                {
                    WebSocketReceiveResult result;

                    do
                    {
                        // Чтение данных из WebSocket
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        
                        // Добавляем прочитанные байты в общий список
                        messageBuffer.AddRange(buffer.Take(result.Count));

                        // Если это конец сообщения
                        if (result.EndOfMessage)
                        {
                            // Преобразуем накопленные байты в строку, когда сообщение полностью получено
                            string messages = Encoding.UTF8.GetString(messageBuffer.ToArray());

                            // Разделяем полученные данные на отдельные сообщения, если их несколько
                            string[] logs = messages.Split(new[] { '\n' },StringSplitOptions.RemoveEmptyEntries);

                            // Обрабатываем каждое сообщение
                            foreach (var log in logs)
                            {
                                ReadLogs(log); // Обработка каждого лога
                                SaveLogsToFile(logFilePath, log); // Сохраняем каждый лог в файл
                            }

                            // Очищаем буфер для следующего сообщения
                            messageBuffer.Clear();
                        }

                    } while (!result.EndOfMessage); // Пока сообщение не завершено, продолжаем получать его части
                }
            }
        }
        catch (Exception ex)
        {
            // Если произошла ошибка, выводим сообщение и отправляем его в лог и Telegram
            string errorMessage = $"❗️ Node {id} ({address}): WebSocket connection failed. Error: {ex.Message}";
            Console.WriteLine(errorMessage);
            WriteLog(errorMessage);
            SendLogsToTelegram(errorMessage);

            // Ожидание 20 секунд перед повторной попыткой
            await Task.Delay(20000);
        }
    }
}



static string GetToken()
{
    if (apiToken != null && DateTime.UtcNow < tokenExpiration)
    {
        return apiToken; // Если токен валиден, возвращаем его
    }

    using (var client = new HttpClient())
    {
        // Подготовка данных для POST запроса
        var payload = new Dictionary<string, string>
        {
            { "username", PANEL_USERNAME },
            { "password", PANEL_PASSWORD }
        };

        // Выполнение запроса и получение ответа
        var response = client.PostAsync($"https://{PANEL_DOMAIN}/api/admin/token", new FormUrlEncodedContent(payload)).Result;
        var responseContent = response.Content.ReadAsStringAsync().Result;
        dynamic json = JsonConvert.DeserializeObject(responseContent);

        // Сохраняем токен
        apiToken = json.access_token;

        // Декодируем JWT-токен для получения времени истечения
        var handler = new JwtSecurityTokenHandler();
        var jsonToken = handler.ReadToken(apiToken) as JwtSecurityToken;

        if (jsonToken != null)
        {
            // Извлечение времени истечения (exp) и преобразование его в DateTime
            var exp = jsonToken.Payload.Exp;
            if (exp.HasValue)
            {
                tokenExpiration = DateTimeOffset.FromUnixTimeSeconds(exp.Value).UtcDateTime;
            }
        }

        return apiToken;
    }
}

// Класс для хранения данных о ноде
class Node
{
    public int Id { get; set; }
    public string Address { get; set; }
}

    // Пример использования токена при запросе списка нод
    static List<Node> GetNodes()
    {
        using (var client = new HttpClient())
        {
            string token = GetToken(); // Получение токена для доступа к API
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token); // Добавление токена в заголовки

            var response = client.GetAsync($"https://{PANEL_DOMAIN}/api/nodes").Result; // Запрос к API для получения списка нод
            var responseContent = response.Content.ReadAsStringAsync().Result; // Чтение ответа в строку
            dynamic json = JsonConvert.DeserializeObject(responseContent); // Парсинг JSON ответа

            List<Node> nodes = new List<Node>(); // Список для хранения нод

            // Обход всех нод и извлечение их id и address
            foreach (var node in json)
            {
                nodes.Add(new Node
                {
                    Id = (int)node.id, // Извлечение id
                    Address = (string)node.address // Извлечение address
                });
            }

            return nodes; // Возвращаем список нод с id и address
        }
    }


// Функция для обработки логов
static void ReadLogs(string log)
{
    bool dontCheck = !log.Contains("accepted"); // Проверка, содержит ли лог слово "accepted"

    // Извлечение IP из лога
    string ip = ExtractIp(log);
    if (ip != null && !INVALID_IPS.Contains(ip)) // Проверка валидности IP
    {
        // Извлечение email из лога
        string email = ExtractEmail(log);

        if (email != null && !INVALID_EMAIL.Contains(email)) // Проверка валидности email
        {
            // Убираем нумерацию в имени пользователя с помощью регулярного выражения
            email = Regex.Replace(email, @"^\d+\.", ""); // Удаление номера перед именем (например, 1. или 2.)

            SaveData(email, ip); // Сохранение данных

            // Извлечение времени использования из лога
            string usageTime = ExtractUsageTime(log);
            if (usageTime != null)
            {
                LastUsage(email, usageTime); // Сохранение последнего времени активности
            }
        }
    }
}


// Добавим список IP-адресов Cloudflare
static List<string> CLOUDFLARE_IPS = new List<string>
{
    // Добавьте сюда диапазоны IP-адресов Cloudflare
    "173.245.48.0/20",
    "103.21.244.0/22",
    "103.22.200.0/22",
    "103.31.4.0/22",
    "141.101.64.0/18",
    "108.162.192.0/18",
    "190.93.240.0/20",
    "188.114.96.0/20",
    "197.234.240.0/22",
    "198.41.128.0/17",
    "162.158.0.0/15",
    "104.16.0.0/13",
    "104.24.0.0/14",
    "172.64.0.0/13",
    "131.0.72.0/22",
    // Вы можете добавить и другие диапазоны, если нужно
};

// Функция для проверки, является ли IP-адрес из диапазона Cloudflare
static bool IsCloudflareIp(string ipAddress)
{
    foreach (string range in CLOUDFLARE_IPS)
    {
        if (IpInRange(ipAddress, range))
        {
            return true;
        }
    }
    return false;
}

// Функция для проверки, находится ли IP в диапазоне
static bool IpInRange(string ipAddress, string cidrRange)
{
    string[] parts = cidrRange.Split('/');
    var ip = System.Net.IPAddress.Parse(ipAddress);
    var baseIp = System.Net.IPAddress.Parse(parts[0]);
    int maskLength = int.Parse(parts[1]);

    var ipBytes = ip.GetAddressBytes();
    var baseIpBytes = baseIp.GetAddressBytes();

    int bytesToCheck = maskLength / 8;
    int bitsToCheck = maskLength % 8;

    for (int i = 0; i < bytesToCheck; i++)
    {
        if (ipBytes[i] != baseIpBytes[i])
        {
            return false;
        }
    }

    if (bitsToCheck > 0)
    {
        int mask = (byte)(0xFF << (8 - bitsToCheck));
        if ((ipBytes[bytesToCheck] & mask) != (baseIpBytes[bytesToCheck] & mask))
        {
            return false;
        }
    }

    return true;
}

// Функция для извлечения первого IP-адреса до слова "accepted"
static string ExtractIp(string log)
{
    // Регулярное выражение для поиска первого IP-адреса, который идет до "accepted"
    var ipv4Match = Regex.Match(log, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}).*?accepted");

    if (ipv4Match.Success)
    {
        string ipAddress = ipv4Match.Groups[1].Value;

        // Фильтрация локальных IP-адресов и IP Cloudflare
        if (IsLocalIpAddress(ipAddress) || IsCloudflareIp(ipAddress))
        {
            return null; // Если это локальный IP или IP Cloudflare, возвращаем null, чтобы игнорировать его
        }

        return ipAddress; // Возвращаем только первый найденный IP-адрес до "accepted"
    }

    return null; // Если IP не найден, возвращаем null
}


// Функция для проверки, является ли IP-адрес локальным
static bool IsLocalIpAddress(string ipAddress)
{
    // Локальный IP-адрес (например, 127.0.0.1)
    if (ipAddress.StartsWith("127.") || ipAddress == "0.0.0.0")
    {
        return true;
    }

    // Добавьте сюда другие диапазоны для фильтрации, если требуется
    // Например, если нужно исключить внутренние диапазоны сети, такие как 10.0.0.0/8 или 192.168.0.0/16

    return false;
}


    // Функция для извлечения email из лога
    static string ExtractEmail(string log)
    {
        var emailMatch = Regex.Match(log, @"email:\s*([A-Za-z0-9._%+-]+)");
        if (emailMatch.Success)
        {
            return emailMatch.Groups[1].Value; // Возвращение найденного email
        }
        return null; // Если email не найден, возвращаем null
    }

    // Функция для извлечения времени использования из лога
    static string ExtractUsageTime(string log)
    {
        var timeMatch = Regex.Match(log, @"\d{2}:\d{2}:\d{2}");
        if (timeMatch.Success)
        {
            return timeMatch.Value; // Возвращение времени
        }
        return null; // Если время не найдено, возвращаем null
    }

    // Сохранение данных пользователя и IP
    static void SaveData(string email, string ip)
    {
        users_list_l.Add((email, ip)); // Добавление пары (email, IP) в список
    }

    // Сохранение последнего времени использования пользователя
    static void LastUsage(string email, string time)
    {
        if (!last_usage_d.ContainsKey(email)) // Если записи для этого пользователя еще нет
        {
            last_usage_d.Add(email, time); // Добавляем новую запись
        }
        else
        {
            last_usage_d[email] = time; // Обновляем время для пользователя
        }
    }



static async Task Job()
{
    var emailToIps = new Dictionary<string, HashSet<string>>(); // Используем HashSet для уникальных IP
    var ipCounter = users_list_l.GroupBy(u => u.Item2).ToDictionary(g => g.Key, g => g.Count());

    foreach (var (email, ip) in users_list_l)
    {
        if (!emailToIps.ContainsKey(email))
        {
            emailToIps[email] = new HashSet<string>(); // Используем HashSet для хранения уникальных IP
        }
        emailToIps[email].Add(ip); // Добавляем уникальный IP для пользователя
    }

    string report = "";
    foreach (var email in emailToIps.Keys)
    {
        var ips = emailToIps[email];
        
        // Группировка IP-адресов по подсетям с учетом количества
        var groupedBySubnet = ips
            .GroupBy(ip => GetSubnet(ip, 22)) // Группировка по подсети /22
            .Select(g => g.Count() == 1 
                         ? g.First() // Если в группе один IP, используем его как есть
                         : $"{g.Key} ({g.Count()})") // Иначе - подсеть с количеством
            .ToList();

        if (groupedBySubnet.Count > USER_IP_LIMIT)
        {
            string formattedIps = string.Join("', '", groupedBySubnet);
            report += $"\n\n❗️ {email} [ {groupedBySubnet.Count} ] IP-Адреса (подсети): ['{formattedIps}']";
            await SendIncrementRequest(email);
        }
    }

    if (!string.IsNullOrEmpty(report))
    {
        string currentTime = DateTime.Now.ToString("dd-MM-yy | HH:mm:ss");
        string logMessage = $"{report}\n\n{currentTime}\nЧисло активных юзеров: {emailToIps.Count}";
        WriteLog(logMessage);
        SendLogsToTelegram($"\n{SERVER_NAME} {logMessage}");
        Console.WriteLine(logMessage);
    }

    users_list_l = new ConcurrentBag<(string, string)>();
}

// Метод для определения подсети с указанной маской (например, 95.153.160.0/22)
static string GetSubnet(string ipAddress, int cidrMask)
{
    var ip = IPAddress.Parse(ipAddress);
    byte[] ipBytes = ip.GetAddressBytes();

    // Применение маски /22 к IP
    int mask = ~(int.MaxValue >> cidrMask);
    byte[] maskBytes = BitConverter.GetBytes(mask).Reverse().ToArray();

    byte[] networkBytes = new byte[4];
    for (int i = 0; i < 4; i++)
    {
        networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
    }

    return $"{networkBytes[0]}.{networkBytes[1]}.{networkBytes[2]}.0/{cidrMask}";
}




// Функция для выполнения curl запроса через HttpClient
static async Task SendIncrementRequest(string email)
{
    using (var client = new HttpClient())
    {
        // Устанавливаем заголовок авторизации
        client.DefaultRequestHeaders.Add("Authorization", "Bearer 4269c25b-a975-44c8-9122-59d595bd7cd0");

        try
        {
            // Формируем URL с подстановкой email
            string url = $"https://vpn-api-block.akkerman-dev.work/increment?vpn_id={email}";

            // Выполняем GET-запрос
            HttpResponseMessage response = await client.PostAsync(url, null);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\nIncrement request for {email} was successful.");
            }
            else
            {
                Console.WriteLine($"\nFailed to increment for {email}. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError while sending increment request for {email}: {ex.Message}");
        }
    }
}



    // Функция для обновления данных из Telegram
    static async Task TelegramUpdater()
    {
        while (SEND_LOGS_TO_TEL) // Пока включена отправка логов в Telegram
        {
            try
            {
                var updates = GetTelegramUpdates(); // Получение обновлений из Telegram
                if (updates != null)
                {
                    foreach (var update in updates)
                    {
                        HandleTelegramCommand(update); // Обработка каждой команды
                    }
                }
                await Task.Delay(60000); // Задержка между проверками
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram updater error: {ex.Message}"); // Обработка ошибок
                await Task.Delay(2000);
            }
        }
    }

    // Получение обновлений из Telegram через API
    static List<dynamic> GetTelegramUpdates(int offset = 0)
    {
        using (var client = new HttpClient())
        {
            // Запрос к Telegram API для получения обновлений
            var response = client.GetStringAsync($"{TELEGRAM_BOT_URL.Replace("sendMessage", "getUpdates")}?offset={offset}&timeout=30").Result;
            var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
            return json["result"] as List<dynamic>; // Возвращение списка обновлений
        }
    }

    // Обработка команд, полученных из Telegram
    static void HandleTelegramCommand(dynamic update)
    {
        if (update.message != null) // Проверка наличия команды
        {
            string text = update.message.text.ToString().ToLower(); // Получение текста команды
            if (text.StartsWith("/usagetime")) // Если команда /usagetime
            {
                string user = text.Split(' ')[1]; // Получение имени пользователя
                if (last_usage_d.ContainsKey(user))
                {
                    SendLogsToTelegram($"{user}: {last_usage_d[user]}"); // Отправка времени использования в Telegram
                }
                else
                {
                    SendLogsToTelegram("User not found"); // Если пользователь не найден
                }
            }
        }
    }

    static void CheckLogFileSizeAndClear()
{
    const long maxLogSize = 10 * 1024 * 1024; // 10 мегабайт в байтах

    // Проверка, существует ли файл
    if (File.Exists(LOG_FILE_NAME))
    {
        FileInfo fileInfo = new FileInfo(LOG_FILE_NAME);
        
        // Если размер файла превышает 10 мегабайт, очищаем его
        if (fileInfo.Length > maxLogSize)
        {
            try
            {
                // Очищаем файл, записывая в него пустую строку
                File.WriteAllText(LOG_FILE_NAME, string.Empty);
                Console.WriteLine($"Log file {LOG_FILE_NAME} was cleared because it exceeded 10MB.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing log file: {ex.Message}");
            }
        }
    }
}

    // Запись логов в файл
    static void WriteLog(string logInfo)
    {
        CheckLogFileSizeAndClear(); // Проверяем размер файла перед записью

        if (WRITE_LOGS_TF) // Если включена запись логов
        {
            File.AppendAllText(LOG_FILE_NAME, logInfo + Environment.NewLine); // Запись данных в файл
        }
    }

    // Отправка сообщений в Telegram через API
    static void SendLogsToTelegram(string message)
    {
        if (SEND_LOGS_TO_TEL) // Если включена отправка логов в Telegram
        {
            using (var client = new HttpClient())
            {
                // Подготовка данных для отправки
                var sendData = new Dictionary<string, string>
                {
                    { "chat_id", CHAT_ID.ToString() },
                    { "text", message },
                    { "parse_mode", "HTML" }
                };
                var content = new FormUrlEncodedContent(sendData);
                client.PostAsync(TELEGRAM_BOT_URL, content).Wait(); // Отправка данных
            }
        }
    }

    // Периодическое удаление валидных IP-адресов из кэша
    static async Task DeleteValidList()
    {
        while (true)
        {
            await Task.Delay(42000); // Задержка перед очисткой
            VALID_IPS.Clear(); // Очистка списка валидных IP-адресов
            Console.WriteLine("Cleared valid IPs list."); // Вывод сообщения о очистке
        }
    }
}