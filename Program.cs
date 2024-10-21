using System;
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

class Program
{
    // Списки для фильтрации недопустимых email и IP-адресов
    static List<string> INVALID_EMAIL = new List<string> { "API]", "Found", "(normal)", "timeout", "EOF", "address", "INFO", "request" };
    static List<string> INVALID_IPS = new List<string> { "1.1.1.1", "8.8.8.8", "0.0.0.0" };
    static List<string> VALID_IPS = new List<string>(); // Список валидных IP-адресов
    static bool WRITE_LOGS_TF, SEND_LOGS_TO_TEL; // Флаги для логирования и отправки данных в Telegram
    static string LOG_FILE_NAME, TELEGRAM_BOT_URL, PANEL_USERNAME, PANEL_PASSWORD, PANEL_DOMAIN, SERVER_NAME; // Параметры конфигурации
    static int CHAT_ID, TIME_TO_CHECK; // ID чата в Telegram и время между проверками
    static int USER_IP_LIMIT; // Лимит адресов для пользователей
    static Dictionary<string, string> last_usage_d = new Dictionary<string, string>(); // Словарь для хранения последнего времени активности пользователей
    static List<(string, string)> users_list_l = new List<(string, string)>(); // Список пар (email, IP)

    static async Task Main(string[] args)
    {
        ReadConfig(); // Чтение конфигурации из JSON файла
        Task.Run(() => GetLogsRun()); // Запуск задачи для получения логов
        Task.Run(() => TelegramUpdater()); // Запуск задачи для обработки Telegram-команд
        Task.Run(() => DeleteValidList()); // Запуск задачи для удаления валидных IP через определенный интервал

        // Получение списка нод и запуск задачи для каждой ноды
        List<int> nodes = GetNodes();
        foreach (var node in nodes)
        {
            int n = node;
            Task.Run(() => GetLogsRun(n)); // Запуск задачи для получения логов от каждой ноды
            Thread.Sleep(2000); // Короткая задержка перед запуском следующей задачи
        }

        // Основной цикл программы, периодически выполняющий задачу Job()
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

        CHAT_ID = int.Parse(config["CHAT_ID"]?.ToString() ?? "0");
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



    // Функция для получения логов по веб-сокетам
    static async Task GetLogsRun(int id = 0)
    {
        while (true) // Бесконечный цикл для постоянного получения данных
        {
            try
            {
                var token = GetToken(); // Получение токена для API
                // Формирование URL для подключения к веб-сокетам (зависит от id ноды)
                string url = id == 0 
                    ? $"wss://{PANEL_DOMAIN}/api/core/logs?token={token}" 
                    : $"wss://{PANEL_DOMAIN}/api/node/{id}/logs?token={token}";

                using (var webSocket = new ClientWebSocket()) // Инициализация веб-сокета
                {
                    await webSocket.ConnectAsync(new Uri(url), CancellationToken.None); // Подключение к веб-сокету
                    Console.WriteLine($"Connected to WebSocket (node {id})");

                    var buffer = new byte[1024 * 4]; // Буфер для получения данных
                    while (webSocket.State == WebSocketState.Open) // Пока подключение открыто
                    {
                        // Чтение данных из веб-сокета
                        WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count); // Преобразование данных в строку
                        string[] logs = message.Split('\n'); // Разделение строки на несколько логов
                        foreach (var log in logs)
                        {
                            ReadLogs(log); // Обработка каждого лога
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Если произошла ошибка, выводим сообщение и отправляем его в Telegram
                string errorMessage = $"Node {id}: WebSocket connection failed. Error: {ex.Message}";
                Console.WriteLine(errorMessage);
                SendLogsToTelegram(errorMessage); // Отправляем сообщение об ошибке в Telegram

                // Ожидание 20 секунд перед повторной попыткой
                await Task.Delay(20000);
            }
        }
    }

    // Получение токена для API через POST запрос
    static string GetToken()
    {
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
            dynamic json = JsonConvert.DeserializeObject(responseContent); // Парсинг JSON
            return json.access_token; // Возвращение токена
        }
    }

    // Получение списка нод с панели через API
    static List<int> GetNodes()
    {
        using (var client = new HttpClient())
        {
            string token = GetToken(); // Получение токена
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token); // Добавление токена в заголовки запроса
            var response = client.GetAsync($"https://{PANEL_DOMAIN}/api/nodes").Result; // Выполнение GET запроса
            var responseContent = response.Content.ReadAsStringAsync().Result;
            dynamic json = JsonConvert.DeserializeObject(responseContent); // Парсинг ответа
            List<int> nodes = new List<int>();
            // Добавление id каждой ноды в список
            foreach (var node in json)
            {
                nodes.Add((int)node.id);
            }
            return nodes; // Возвращение списка нод
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


    // Функция для извлечения IP-адреса из лога
    static string ExtractIp(string log)
    {
        var ipv4Match = Regex.Match(log, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        if (ipv4Match.Success)
        {
            return ipv4Match.Value; // Возвращение найденного IP
        }
        return null; // Если IP не найден, возвращаем null
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

    // Основная логика программы: обработка собранных данных и генерация отчетов
static void Job()
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
        if (ips.Count > USER_IP_LIMIT) // Проверяем только тех, у кого больше, чем USER_IP_LIMIT, IP-адресов
        {
            string formattedIps = string.Join("', '", ips); // Форматируем IP-адреса в строку
            report += $"\n{SERVER_NAME} \n\n❗️ {email} [ {ips.Count} ] IP-Адреса :  ['{formattedIps}']"; // Формируем отчет
        }
    }

    if (!string.IsNullOrEmpty(report))
    {
        string currentTime = DateTime.Now.ToString("dd-MM-yy | HH:mm:ss"); // Форматируем текущее время
        string logMessage = $"{report}\n\n{currentTime}\nЧисло юзеров превысили лимиты IP: {emailToIps.Count}";
        WriteLog(logMessage); // Запись отчета в лог
        SendLogsToTelegram(logMessage); // Отправка отчета в Telegram
        Console.WriteLine(logMessage); // Вывод в консоль
    }

    users_list_l.Clear(); // Очищаем список пользователей
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
                await Task.Delay(30000); // Задержка между проверками
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram updater error: {ex.Message}"); // Обработка ошибок
                await Task.Delay(1000);
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

    // Запись логов в файл
    static void WriteLog(string logInfo)
    {
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
            await Task.Delay(21000); // Задержка перед очисткой
            VALID_IPS.Clear(); // Очистка списка валидных IP-адресов
            Console.WriteLine("Cleared valid IPs list."); // Вывод сообщения о очистке
        }
    }
}
