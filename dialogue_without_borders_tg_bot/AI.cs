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
            string endpoint = "https://api.groq.com/openai/v1"; // переадресация запросов на сайт qroq

            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
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
                    textToParse = note.Text;
                }

                var messages = new List<ChatMessage>
                {
                new SystemChatMessage($"Проанализируй документ и/или текст преподавателя. Разбей его на отдельные объявления. Для каждой записи верни JSON с ключом \"announcements\", внутри которого массив объектов: TargetGroup (группа, если указана, или \"all\"), TargetCourse (номер курса), если указан, или 0), Topic (тема: пересдача, дедлайн, лекция), Content (суть объявления), EventDate (дата события в формате день.месяц.год часы:минуты. Нынешнаяя дата - {DateTime.Today.ToString("d")}), NoteName - название, которое ты бы дал этой заметке (придумай лаконичное название). Если заметка не связана с учебой - игнорируй ее. Если группа написана в формате 2ИСИП124 - трактуй это как группа ИСИП-124 2 курса. Не нужно присылать пояснения, необходим только JSON файл."), // системная инструкция
                new UserChatMessage(textToParse) // текст самой заметки
                };

                ChatCompletion response = await chatClient.CompleteChatAsync(messages, chatOptions);
                string jsonResult = response.Content[0].Text;

                // Превращаем текст в готовые объекты C#
                var parsedData = JsonSerializer.Deserialize<GroqAnnouncementResponse>(jsonResult);

                if (parsedData != null && parsedData.announcements.Count > 0)
                {
                    if (note == null)
                    {
                        return;
                    }

                    foreach (var item in parsedData.announcements)
                    {
                        // Создаем дочернюю сущность для БД
                        var announcement = new StudyAnnouncement
                        {
                            TargetGroup = item.TargetGroup,
                            Topic = item.Topic,
                            Content = item.Content,
                            EventDate = item.EventDate,
                            TargetСourse = item.TargetCourse
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
        public async Task<string> GenerationAnswer(DbContextOptions<ApplicationContext> options, string group, int course, string name, string surname, string question)
        {
            using (ApplicationContext db = new ApplicationContext(options))
            {
                var cutoffDate = DateTime.Now.AddDays(-30); // Заметки не старше 30 дней

                // подходящие записи
                var announcements = db.studyAnnouncements
                    .Include(a => a.Document) // Подтягиваем родительскую заметку
                    .Where(a => (a.TargetGroup == group || a.TargetGroup == "all")
                          && a.Document.CreatedTime >= cutoffDate) // Фильтр по дате создания
                    .OrderByDescending(a => a.Document.CreatedTime)   // Сначала самые свежие
                    .Take(15)                                        // Ограничиваем 15 последними записями
                    .ToList();

                
                //формирование запроса
                string currectAnnouncements = "";

                foreach (var announcement in announcements)
                {
                    currectAnnouncements += $"|Создано: {announcement.Document.CreatedTime}|Тема: {announcement.Topic}|Содержание: {announcement.Content}|Дата события: {announcement.EventDate}\n";
                }


                string endpoint = "https://api.groq.com/openai/v1"; // переадресация запросов на сайт qroq

                var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
                var client = new OpenAIClient(new ApiKeyCredential(_apiKey), clientOptions);
                var chatClient = client.GetChatClient("openai/gpt-oss-20b");

                var chatOptions = new ChatCompletionOptions
                {
                    ResponseFormat = ChatResponseFormat.CreateTextFormat(),
                    Temperature = 0.1f
                };

                var messages = new List<ChatMessage>
                {
                new SystemChatMessage($"Ты - помощник учителя. Отвечай студенту вежливо и кратко, используя ТОЛЬКО факты из списка ниже. Если ответа на вопрос студента в списке нет, прямо напиши, что информации не найдено. Имя студента - {name}. Фамилия студента - {surname}. Курс - {course}. Группа - {group}. Вопрос студента - {question}"), // системная инструкция
                new UserChatMessage(currectAnnouncements) // текст самой заметки
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