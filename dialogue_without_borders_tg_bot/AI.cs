using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;
using Telegram.Bot;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace dialogue_without_borders_tg_bot
{
    internal class AI
    {
        private readonly string _apiKey;

        public AI(IConfiguration config)
        {
            _apiKey = config["GroqConfiguration:ApiKey"]
                ?? throw new InvalidOperationException("Groq ApiKey не найден в config.json");
        }
        public async Task GenerationNote(DbContextOptions<ApplicationContext> options, int noteId, ITelegramBotClient bot)
        {
            string endpoint = "https://api.groq.com/openai/v1"; // переадресация запросов на сайт groq

            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromMinutes(2) }; // чтобы зависший запрос к groq не висел бесконечно
            var client = new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions);
            var chatClient = client.GetChatClient("openai/gpt-oss-120b");

            var chatOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                Temperature = 0.1f
            };

            using (ApplicationContext db = new ApplicationContext(options))
            {
                var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteId);

                if (note == null || (string.IsNullOrWhiteSpace(note.Text) && string.IsNullOrWhiteSpace(note.FileId)))
                {
                    return;
                }

                // берем текст который будем парсить
                string textToParse = "";

                // если отправлен файл
                if (note.FileId != null)
                {
                    // 1. У нас есть FileId из базы данных
                    string fileId = note.FileId;

                    // 2. Узнаем внутренний путь к файлу на серверах Telegram
                    var fileInfo = await bot.GetFile(fileId);

                    // 3. Создаем в оперативной памяти поток (контейнер для байтов)
                    using var memoryStream = new MemoryStream();

                    // 4. Скачиваем байты файла из интернета прямо в memoryStream
                    await bot.DownloadFile(fileInfo.FilePath!, memoryStream);

                    // 5. Перематываем каретку потока на самое начало (байт 0)
                    memoryStream.Position = 0;

                    // 6. Определяем расширение и вызываем нужный метод парсинга
                    string extension = Path.GetExtension(note.FileName)?.ToLower() ?? "";

                    string fileContent = extension switch
                    {
                        ".docx" => ExtractTextFromDocx(memoryStream),
                        ".txt" => await ExtractTextFromTxtAsync(memoryStream),
                        _ => ""
                    };

                    // Если к файлу была подпись, объединяем её с текстом файла
                    textToParse = string.IsNullOrWhiteSpace(note.Text)
                        ? fileContent
                        : $"{note.Text}\n{fileContent}".Trim();
                }

                // если отправлен текст
                else
                {
                    textToParse = note.Text!;
                }

                // Промпт для анализа заметки/ведомости преподавателя и разбивки на записи
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage($$"""
                        Ты — структуризатор учебных документов. Твоя задача — извлечь факты из текста преподавателя и вернуть СТРОГО JSON-объект с ключом "announcements".
                        Нынешняя дата: {{DateTime.Today:dd.MM.yyyy}}.

                        СТРУКТУРА ОТВЕТА:
                        {
                          "announcements": [
                            {
                              "TargetGroup": "Название группы в формате ИСИП 124 (без номера курса в начале) или 'all', если для всех",
                              "TargetCourse": Номер курса числом (0 если не указан),
                              "Topic": "Краткая тема или 'Тема: Фамилия'",
                              "Content": "Суть объявления или персональный статус студента",
                              "EventDate": "Дата и время в формате ДД.ММ.ГГГГ ЧЧ:ММ (или null)"
                            }
                          ]
                        }

                        ПРАВИЛА ИЗВЛЕЧЕНИЯ:
                        1. Если текст адресован всей группе (пересдача, лекция, перенос) — создай ОДИН объект в массиве "announcements".
                        2. Если группа написана слитно с курсом (например, 3ИСИП124 или 2 ИСИП-124) — запиши курс в TargetCourse (3 или 2), а в TargetGroup оставь специальность и номер (например, "ИСИП 124").
                        3. Если текст содержит список/таблицу с результатами, оценками или статусами (сдал/не сдал) конкретных студентов — создай ОТДЕЛЬНЫЙ объект в массиве "announcements" на КАЖДОГО упомянутого студента.
                        4. Для персональной записи:
                           - В поле "Topic" укажи тему и фамилию (например: "Сдача контрольной 5: Щеголев").
                           - В поле "Content" укажи статус конкретного человека (например: "Щеголев: Сдал").
                        5. В ответе должен быть ТОЛЬКО чистый валидный JSON без пояснений и лишнего текста.
                        """),

                    new UserChatMessage($"""
                        Дисциплина: {note.Discipline ?? "Не указана"}
                        Название заметки: {note.NoteName}
                        Текст заметки:
                        {textToParse}
                        """)
                };

                ChatCompletion response = await chatClient.CompleteChatAsync(messages, chatOptions);
                string jsonResult = response.Content[0].Text;

                // Превращаем текст в готовые объекты C#
                var parsedData = JsonSerializer.Deserialize<GroqAnnouncementResponse>(jsonResult, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsedData != null && parsedData.announcements != null && parsedData.announcements.Count > 0)
                {
                    foreach (var item in parsedData.announcements)
                    {
                        // Создаем дочернюю сущность для БД
                        var announcement = new StudyAnnouncement
                        {
                            TargetGroup = item.TargetGroup,
                            Topic = item.Topic,
                            Content = item.Content,
                            EventDate = item.EventDate,
                            TargetCourse = item.TargetCourse
                        };
                        note.Announcements.Add(announcement);
                    }
                    db.SaveChanges();
                }
            }
        }
        public static string ExtractTextFromDocx(Stream stream)
        {
            var sb = new StringBuilder();

            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) return string.Empty;

            foreach (var element in body.Elements())
            {
                if (element is Paragraph p && !string.IsNullOrWhiteSpace(p.InnerText))
                {
                    sb.AppendLine(p.InnerText);
                }
                else if (element is Table table)
                {
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                        sb.AppendLine(string.Join(" | ", cells));
                    }
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
        public static async Task<string> ExtractTextFromTxtAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
        public async Task<string> GenerationAnswer(DbContextOptions<ApplicationContext> options, string group,string speciality, int course, string name, string surname, string question)
        {
            using (ApplicationContext db = new ApplicationContext(options))
            {
                if(string.IsNullOrWhiteSpace(question))
                {
                    return "К сожалению, актуальных объявлений для вашей группы не найдено.";
                }

                //var cutoffDate = DateTime.Now.AddDays(-30); // Заметки не старше 30 дней

                string cleanGroup = group.Replace(" ", "").Replace("-", "").ToLower();

                // подходящие записи
                var announcements = db.studyAnnouncements
                    .Include(a => a.Document)
                    .AsEnumerable() // переводим фильтрацию строк в память для удобного сравнения
                    .Where(a => a.TargetGroup == "all" ||
                                a.TargetGroup.Replace(" ", "").Replace("-", "").ToLower() == cleanGroup)
                    //.Where(a => a.Document.CreatedTime >= cutoffDate)
                    .OrderByDescending(a => a.Document.CreatedTime)
                    .Take(15)
                    .ToList();


                //формирование запроса
                string currentAnnouncements = "";

                foreach (var announcement in announcements)
                {
                    currentAnnouncements += $"|Предмет: {announcement.Document.Discipline ?? "Общий"}" +
                        $"|Заметка: {announcement.Document.NoteName}" +
                        $"|Тема: {announcement.Topic}" +
                        $"|Содержание: {announcement.Content}" +
                        $"|Дата события: {announcement.EventDate}\n";
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[DEBUG] Поиск для группы: '{group}' (clean: '{cleanGroup}')");
                Console.WriteLine($"[DEBUG] Найдено объявлений в БД: {announcements.Count}");
                Console.WriteLine($"[DEBUG] Сформированный контекст:\n{currentAnnouncements}");
                Console.ResetColor();

                string endpoint = "https://api.groq.com/openai/v1"; // переадресация запросов на сайт groq

                var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromMinutes(2) }; // чтобы зависший запрос к groq не висел бесконечно
                var client = new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions);
                var chatClient = client.GetChatClient("openai/gpt-oss-20b");

                var chatOptions = new ChatCompletionOptions
                {
                    ResponseFormat = ChatResponseFormat.CreateTextFormat(),
                    Temperature = 0.1f
                };

                var messages = new List<ChatMessage>
                {
                  new SystemChatMessage(
                                        $"Ты — виртуальный помощник учебной части. Отвечай студенту вежливо, кратко и по делу.\n" +
                                        $"Имя студента: {name} {surname}, Курс: {course}, Группа: {group}, Специальность: {speciality}.\n\n" +
                                        $"Правила:\n" +
                                        $"1. Отвечай, опираясь ТОЛЬКО на список объявлений ниже.\n" +
                                        $"2. Если в объявлениях нет точного ответа на вопрос студента, прямо скажи: 'Информации по вашему запросу не найдено'.\n\n" +
                                        $"Список объявлений:\n{currentAnnouncements}"
                                     ),
                  new UserChatMessage(question) // Сам вопрос студента
                };

                ChatCompletion response = await chatClient.CompleteChatAsync(messages, chatOptions);
                string answer = response.Content[0].Text;

                return answer;
            }
        }

        public class GroqAnnouncementResponse
        {
            public List<StudyAnnouncementDto> announcements { get; set; } = new();
        }

        public class StudyAnnouncementDto
        {
            public string TargetGroup { get; set; } = "all";
            public string Topic { get; set; } = null!;
            public string Content { get; set; } = null!;
            public string? EventDate { get; set; }
            public int TargetCourse { get; set; }
        }
    }
}