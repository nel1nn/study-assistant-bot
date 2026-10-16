using dialogue_without_borders_tg_bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using var cts = new CancellationTokenSource();

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
};

var builder = new ConfigurationBuilder();
builder.SetBasePath(Directory.GetCurrentDirectory());
builder.AddJsonFile("config.json");
var config = builder.Build();

var connectionString = config.GetConnectionString("DefaultConnection");
string botToken = config["BotConfiguration:BotToken"]
    ?? throw new InvalidOperationException("BotToken не найден в config.json");

var botClient = new TelegramBotClient(botToken);

var optionsBuilder = new DbContextOptionsBuilder<ApplicationContext>();
var options = optionsBuilder.UseSqlite(connectionString).Options;

using (var initDb = new ApplicationContext(options))
{
    initDb.Database.EnsureCreated();
}

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandleErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

Console.WriteLine("Бот запущен. Нажми Enter для безопасного завершения работы\n");
if (Console.ReadLine() == null)
    await Task.Delay(Timeout.Infinite);

using (var db = new ApplicationContext(options))
{
    var users = db.Users.ToList();
    Console.ForegroundColor = ConsoleColor.Green;
    foreach (var user in users)
    {
        if (user.State == null || user.State == "WaitingForUploadNote" || user.State == "WaitingForUploadNoteName" || user.State == "WaitingAskedQuestion" ||
            user.State.StartsWith("WaitingSendNewNoteName:") || user.State.StartsWith("WaitingSendNewNoteContent:"))
        {
            user.LastInlineMessageId = 0;
            user.State = null;
        }
        Console.WriteLine($"Пользователь {user.Id} обработан");
    }
    Console.ResetColor();
    db.SaveChanges();
}

cts.Cancel();

Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
{
    Task.Run(async () =>
    {
        try
        {
            if (update.Message != null)
            {
                await OnMessageReceived(bot, update.Message, ct);
                return;
            }

            if (update.CallbackQuery != null)
            {
                await OnCallbackReceived(bot, update.CallbackQuery, ct);
                return;
            }
        }
        catch (Exception ex)
        {
            await HandleErrorAsync(bot, ex, HandleErrorSource.HandleUpdateError, ct);
        }
    });

    return Task.CompletedTask;
}
async Task OnMessageReceived(ITelegramBotClient bot, Message msg, CancellationToken ct)
{
    long chatId = msg.Chat.Id;
    long fromUserId = msg.From?.Id ?? 0;

    if (msg.Text != null)
    {
        switch(msg.Text)
        {
            case "/start":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    bool exists = db.Users.Any(u => u.TgId == fromUserId);

                    if (exists)
                    {
                        await bot.SendMessage(chatId, "Вы уже зарегистрировались!", cancellationToken: ct);
                        return;
                    }
                }

                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("👨‍🏫 Преподаватель", "reg:btn_teacher"),
                        InlineKeyboardButton.WithCallbackData("🧑‍🎓 Студент", "reg:btn_student")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:cancel")
                    }
                    });

                    await bot.SendMessage(
                        chatId: chatId,
                        text: "Выберите свой статус",
                        replyMarkup: inlineKeyboard,
                        cancellationToken: ct
                    );

                }
                    
                return;

            case "/help":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var user = db.Users.FirstOrDefault(u => u.TgId == fromUserId);

                    var buttons = new List<InlineKeyboardButton[]>
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("📝 Как зарегистрироваться?", "help:topic:1") },
                        new[] { InlineKeyboardButton.WithCallbackData("⏳ Сколько ждать проверки заявки?", "help:topic:2") },
                        new[] { InlineKeyboardButton.WithCallbackData("⛔ Что делать, если заявку отклонили?", "help:topic:3") }
                    };

                    if (user != null)
                    {
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Как изменить свои данные?", "help:topic:4") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🗑️ Как удалить профиль?", "help:topic:5") });
                    }

                    if (user is StudentDB)
                    {
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❓ Как задать вопрос боту?", "help:topic:6") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🤔 Почему бот не нашел ответ?", "help:topic:7") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🪪 Зачем нужно фото студенческого?", "help:topic:8") });
                    }

                    if (user is TeacherDB)
                    {
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 Как загрузить заметку?", "help:topic:9") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📎 Какие файлы можно загрузить?", "help:topic:10") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("✏️ Как изменить или удалить заметку?", "help:topic:11") });
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🎯 Как заметка попадет нужной группе?", "help:topic:12") });
                    }

                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "help:close") });

                    await bot.SendMessage(chatId, "Выберите интересующую тему", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
                }
                return;

            case "/admin":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var admin = db.Admins.FirstOrDefault(a => a.TgId == fromUserId);

                    if (admin == null || !(admin is AdminDB)) { await bot.SendMessage(chatId, "Вы не являетесь администратором", cancellationToken: ct); return; }

                    if (admin.LastInlineMessageId != 0)
                    {
                        try { await bot.DeleteMessage(chatId, admin.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }
                        admin.State = null;
                    }

                    var inlineAdminKeyboard = (new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❔ Верифицировать пользователей", "admin:startUserVerification"),
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("📋 Вывести список пользователей", "admin:allUsers"),
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❌ Отмена", "admin:cancel")
                    }});

                    var messageToDelete = await bot.SendMessage(
                    chatId: chatId,
                    text: "Меню",
                    replyMarkup: inlineAdminKeyboard,
                    cancellationToken: ct
                    );

                    admin.LastInlineMessageId = messageToDelete.Id;

                    db.SaveChanges();
                }
                return;

            case "/teacher":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var teacher = db.Teachers.FirstOrDefault(u => u.TgId == fromUserId);

                    if (teacher == null)
                    {
                        await bot.SendMessage(chatId, "Вы не являетесь преподавателем.");

                        return;
                    }

                    if (!teacher.Isverified)
                    {
                        await bot.SendMessage(chatId, "Ваш аккаунт не верифицирован.");
                        return;
                    }

                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📋 Загрузить новую заметку","teacher:uploadNote"),
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ Управление заметками","teacher:editNote"),
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:cancel")
                }
                });

                    if (teacher.LastInlineMessageId != 0)
                    {
                        try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }
                        teacher.State = null;
                    }

                    var messageToDelete = await bot.SendMessage(chatId, "Для навигации по меню воспользуйтесь кнопками", replyMarkup: inlineKeyboard, cancellationToken: ct);

                    teacher.LastInlineMessageId = messageToDelete.Id;

                    db.SaveChanges();
                }
                return;

            case "/student":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var student = db.Students.FirstOrDefault(u => u.TgId == fromUserId);

                    if (student == null)
                    {
                        await bot.SendMessage(chatId, "Вы не являетесь студентом.");
                        return;
                    }

                    if (!student.Isverified)
                    {
                        await bot.SendMessage(chatId, "Ваш аккаунт не верифицирован.");
                        return;
                    }

                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                    InlineKeyboardButton.WithCallbackData("❌ Отмена","student:cancel")
                });

                    if (student.LastInlineMessageId != 0)
                    {
                        try { await bot.DeleteMessage(chatId, student.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }
                        student.State = null;
                    }

                    var messageToDelete = await bot.SendMessage(chatId, "Отправьте свой вопрос. Он должен быть связан только с вашей учебой. \nПостарайтесь задать конкретный вопрос.", replyMarkup: inlineKeyboard, cancellationToken: ct);

                    student.State = "WaitingAskedQuestion";
                    student.LastInlineMessageId = messageToDelete.Id;

                    db.SaveChanges();
                }
                return;

            case "/profile":
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var user = db.Users.FirstOrDefault(u=>u.TgId == fromUserId);

                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("🗑️ Удалить профиль", "profile:delete") },
                        new[] { InlineKeyboardButton.WithCallbackData("✏️ Редактировать информацию", "profile:edit") },
                        new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть меню", "profile:close") }
                    });

                    switch (user)
                    {
                        case StudentDB student:
                            await bot.SendMessage(chatId, $"Роль: 🧑‍🎓 студент\nФИО: {student.Surname} {student.Name} {student.Patronymic}\nКурс: {student.Course}\nГруппа: {student.Group}\nВерифицирован: {(student.Isverified ? "✅" : "❌")}", replyMarkup: inlineKeyboard, cancellationToken: ct);
                            return;

                        case TeacherDB teacher:
                            await bot.SendMessage(chatId, $"Роль: 🧑‍🏫 преподаватель\nФИО: {teacher.Surname} {teacher.Name} {teacher.Patronymic}\nСпециальность: {teacher.Specialty}\nВерифицирован: {(teacher.Isverified ? "✅" : "❌")}", replyMarkup: inlineKeyboard, cancellationToken: ct);
                            return;

                        case AdminDB admin:
                            await bot.SendMessage(chatId, $"Роль: администратор", replyMarkup: inlineKeyboard, cancellationToken: ct);
                            return;

                        default:
                            await bot.SendMessage(chatId, "Ваш аккаунт не зарегистрирован. Воспользуйтесь командой /start", cancellationToken: ct);
                            return;
                    }
                }
                    return;

            default:
                using (ApplicationContext db = new ApplicationContext(options))
                {
                    var user = db.Users.FirstOrDefault(u => u.TgId == fromUserId);

                    if (user == null)
                    {
                        await bot.SendMessage(chatId, "Ваш аккаунт не зарегистрирован. Воспользуйтесь командой /start", cancellationToken: ct);
                        return;
                    }

                    switch (user.State)
                    {
                        case "WaitingForUploadNote":
                            TeacherNote note = new TeacherNote();
                            note.FileId = null;
                            note.TeacherTgId = fromUserId;
                            note.Discipline = (user as TeacherDB).Specialty;
                            note.Text = msg.Text;
                            note.CreatedTime = DateTime.Now;
                            note.NoteName = (user as TeacherDB)!.NoteName!;
                            user.State = null;
                            db.teacherNotes.Add(note);

                            db.SaveChanges();

                            int noteId = note.Id;

                            var messageToEdit = await bot.SendMessage(chatId, "Обработка заметки...", cancellationToken: ct);

                            AI aiService = new AI(config);
                            try
                            {
                                await aiService.GenerationNote(options, note.Id, bot);
                                await bot.EditMessageText(chatId, messageToEdit.Id, "Заметка успешно добавлена!", cancellationToken: ct);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] (GenerationNote): {ex.Message}");
                                await bot.EditMessageText(chatId, messageToEdit.Id, "⚠️ Заметка сохранена, но обработать ее не удалось. Попробуйте позже заново загрузить содержимое через управление заметками.", cancellationToken: ct);
                            }

                            try { await bot.DeleteMessage(chatId, user.LastInlineMessageId, cancellationToken: ct); }
                            catch (Exception) { }

                            user.LastInlineMessageId = 0;
                            db.SaveChanges();
                            return;

                        case "WaitingForUploadNoteName":
                            await bot.DeleteMessage(chatId, user.LastInlineMessageId);

                            var name = msg.Text;

                            var inlineKeyboard = new InlineKeyboardMarkup(new[]
                            {
                                 new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "teacher:confirmNoteName"),},
                                 new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "teacher:rejectNoteName") }
                            });

                            var message = await bot.SendMessage(chatId, $"Заметка будет названа '{name}'", cancellationToken: ct, replyMarkup: inlineKeyboard);

                            (user as TeacherDB).NoteName = name;

                            user.LastInlineMessageId = message.Id;
                            db.SaveChanges();
                            return;

                        case string s when s.StartsWith("WaitingSendNewNoteName:"):
                            int noteNum = int.Parse(user.State.Split(':')[1]);

                            var teacher = user as TeacherDB;
                            note = db.teacherNotes.FirstOrDefault(n => n.Id == noteNum);

                            inlineKeyboard = new InlineKeyboardMarkup(new[]
                            {
                             new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить", $"teacher:confirmNoteRename:{noteNum}")},
                             new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", $"teacher:rejectNoteRename:{noteNum}") }
                        });

                            try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                            catch (Exception) { }
                            var messageToDelete = await bot.SendMessage(chatId, $"Подтвердить переименование?\n<s>{WebUtility.HtmlEncode(note.NoteName)}</s> ➡️ {WebUtility.HtmlEncode(msg.Text)}", cancellationToken: ct, replyMarkup: inlineKeyboard, parseMode: ParseMode.Html);
                            teacher.LastInlineMessageId = messageToDelete.Id;
                            teacher.NoteName = msg.Text;

                            db.SaveChanges();
                            break;

                        case string s when s.StartsWith("WaitingSendNewNoteContent:"):
                            teacher = user as TeacherDB;
                            noteNum = int.Parse(user.State.Split(':')[1]);
                            note = db.teacherNotes.FirstOrDefault(n => n.Id == noteNum);
                            string newContent = msg.Text;
                            inlineKeyboard = (new[]
                            { new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить",$"teacher:confirmNoteTextContent:{noteNum}")},
                              new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена",$"teacher:rejectNoteContent:{noteNum}"), } }
                            );
                            messageToDelete = await bot.SendMessage(chatId, $"Подтвердите изменение содержания файла.\n\nНовое содержание:<blockquote expandable>{WebUtility.HtmlEncode(newContent.Length > 3000 ? newContent.Substring(0, 3000) + "..." : newContent)}</blockquote>", parseMode: ParseMode.Html, replyMarkup: inlineKeyboard, cancellationToken: ct);

                            teacher.LastInlineMessageId = messageToDelete.Id;

                            teacher.NoteName = msg.Text;

                            db.SaveChanges();
                            break;

                        case "WaitingAskedQuestion":
                            StudentDB student = (StudentDB)user;
                            string group = $"{student?.Speciality}{student?.Group}".Trim();
                            int course = student!.Course;
                            string speciality = student.Speciality;
                            name = student.Name;
                            string surname = student.Surname!;
                            string question = msg.Text;

                            await bot.DeleteMessage(chatId, user.LastInlineMessageId);
                            var messageResponse = await bot.SendMessage(chatId, "Ищем ответ на Ваш вопрос... 🔎");

                            aiService = new AI(config);
                            string answer;
                            try
                            {
                                answer = await aiService.GenerationAnswer(options, group, speciality, course, name, surname, question);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] (GenerationAnswer): {ex.Message}");
                                answer = "⚠️ Не удалось получить ответ. Попробуйте задать вопрос позже.";
                            }

                            if (string.IsNullOrWhiteSpace(answer)) answer = "Информации по вашему запросу не найдено.";
                            if (answer.Length > 4000) answer = answer.Substring(0, 4000) + "...";

                            user.LastInlineMessageId = 0;
                            user.State = null;
                            db.SaveChanges();

                            await bot.EditMessageText(chatId, messageResponse.Id, answer, cancellationToken: ct);
                            break;

                        case "WaitingForFullName":
                            try
                            {
                                string[] inputText = msg.Text.Split(' ');
                                user.Surname = inputText[0];
                                user.Name = inputText[1];
                                user.Patronymic = inputText[2];
                                inlineKeyboard = new InlineKeyboardMarkup(new[]
                                {
                                   new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirmFullName") },
                                   new[] {InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:rejectFullName") }
                                });
                                messageToDelete = await bot.SendMessage(chatId, $"Подтвердите корректность данных:\nФамилия: {user.Surname}\nИмя: {user.Name}\nОтчество: {user.Patronymic}", replyMarkup: inlineKeyboard, cancellationToken: ct);
                                user.LastInlineMessageId = messageToDelete.Id;
                                db.SaveChanges();
                            }
                            catch (Exception)
                            {
                                await bot.SendMessage(chatId, "Ошибка обработки. Повторите попытку");
                            }
                        break;

                        case "WaitForGroup":
                            try
                            {
                                string[] inputText = msg.Text.Split(' ');
                                student = user as StudentDB;
                                student.Course = int.Parse(inputText[0]);
                                student.Speciality = inputText[1];
                                student.Group = int.Parse(inputText[2]);
                                inlineKeyboard = new InlineKeyboardMarkup(new[]
{
                                   new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirmGroup") },
                                   new[] {InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:rejectGroup") }
                                });
                                messageToDelete = await bot.SendMessage(chatId, $"Подтвердите корректность данных:\nСпециальность: ~{student.Speciality}~\nКурс: ~{student.Course}~\nГруппа: ~{student.Group}~", cancellationToken: ct,replyMarkup:inlineKeyboard);
                                student.LastInlineMessageId = messageToDelete.Id;

                                db.SaveChanges();
                            }
                            catch (Exception)
                            {
                                await bot.SendMessage(chatId, "Ошибка обработки. Повторите попытку");
                            }
                            break;

                        case "WaitForSpecialty":
                            try
                            {
                                teacher = user as TeacherDB;
                                teacher.Specialty = msg.Text;

                                inlineKeyboard = new InlineKeyboardMarkup(new[]
{
                                   new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirmSpecialty") },
                                   new[] {InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:rejectSpecialty") }
                                });
                                messageToDelete = await bot.SendMessage(chatId, $"Подтвердите корректность данных:\nСпециальность: ~{teacher.Specialty}~", cancellationToken: ct, replyMarkup: inlineKeyboard);
                                teacher.LastInlineMessageId = messageToDelete.Id;
                                db.SaveChanges();
                            }
                            catch (Exception)
                            {
                                await bot.SendMessage(chatId, "Ошибка обработки. Повторите попытку");
                            }

                            return;

                        default:
                            await bot.SendMessage(chatId, "🤖 Для навигации по боту воспользуйтесь командами");
                            break;
                    }
                }
                return;
        }
    }

    if (msg.Photo != null)
    {
        using (ApplicationContext db = new ApplicationContext(options))
        {
            var user = db.Students.FirstOrDefault(u => u.TgId == fromUserId);

            if (user == null)
                return;

            if (user.State == "WaitingForStudentCard")
            {
                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirmPhoto") },
                    new[] {InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:rejectPhoto") }
                });
                var messageToDelete = await bot.SendMessage(chatId, "Подтвердить отправку фотографии?", replyMarkup: inlineKeyboard, cancellationToken: ct);

                user.StudentCardFileId = msg.Photo[^1].FileId;

                user.LastInlineMessageId = messageToDelete.MessageId;

                db.SaveChanges();
                return;
            }
        }
    }

    if (msg.Document != null)
    {
        using (ApplicationContext db = new ApplicationContext(options))
        {
            var teacher = db.Teachers.FirstOrDefault(t => t.TgId == fromUserId);

            if (teacher == null)
                return;

            if (msg.Document.FileName == null || !(msg.Document.FileName.EndsWith(".txt") || msg.Document.FileName.EndsWith(".docx")) )
            {
                 await bot.SendMessage(chatId, "Файл такого расширения не поддерживается! Повторите попытку!", cancellationToken: ct);

                 return;
            }

            if (teacher.State != null && teacher.State.StartsWith("WaitingSendNewNoteContent:"))
            {
                int noteNum = int.Parse(teacher.State.Split(':')[1]);
                var note = db.teacherNotes.FirstOrDefault(n=>n.Id==noteNum);
                if (note == null) return;

                InlineKeyboardMarkup inlineKeyboard = (new[]
{
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Подтвердить",$"teacher:confirmNoteDocumentContent:{noteNum}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена",$"teacher:rejectNoteContent:{noteNum}"),
                        }
                    }
                );

                var messageToDelete = await bot.SendMessage(chatId, $"Подтвердите загрузку файла. Старое содержимое будет удалено.", replyMarkup: inlineKeyboard, cancellationToken: ct);

                teacher.LastInlineMessageId = messageToDelete.Id;

                teacher.NoteName = $"{msg.Document.FileId}\n{msg.Document.FileName}\n{msg.Caption}";

                db.SaveChanges();
            }

            if (teacher.State == "WaitingForUploadNote")
            {
                TeacherNote note = new TeacherNote();
                note.FileId = msg.Document.FileId;
                note.TeacherTgId = fromUserId;
                note.Discipline = teacher.Specialty;
                note.Text = msg.Caption;
                note.CreatedTime = DateTime.Now;
                note.FileName = msg.Document.FileName;
                note.NoteName = teacher.NoteName!;
                teacher.State = null;

                db.teacherNotes.Add(note);

                db.SaveChanges();

                int noteId = note.Id;

                await bot.SendMessage(chatId, "Обработка загруженного файла... Время ожидания зависит от его объема, пожалуйста, подождите.\nОбычно это занимает не более 30 секунд. 🕕", cancellationToken: ct);

                AI aiService = new AI(config);

                try
                {
                    await aiService.GenerationNote(options, note.Id,bot);
                    await bot.SendMessage(chatId, "Заметка успешно добавлена!", cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] (GenerationNote): {ex.Message}");
                    await bot.SendMessage(chatId, "⚠️ Заметка сохранена, но обработать файл не удалось. Попробуйте позже заново загрузить содержимое через управление заметками.", cancellationToken: ct);
                }

                return;
            }

        }
    }
}

async Task OnCallbackReceived(ITelegramBotClient bot, CallbackQuery query, CancellationToken ct)
{
    await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);

    string action = query.Data ?? "";
    string[] parts = action.Split(':');

    if (parts.Length < 2) return;

    string type = parts[0];
    string command = parts[1];
    long chatId = query.Message!.Chat.Id;
    int messageId = query.Message.Id;

    switch (type)
    {
        case "reg":
            using (ApplicationContext db = new ApplicationContext(options))
            {
                switch (command)
                {
                    case "cancel":
                        await bot.DeleteMessage(chatId, messageId, ct);
                        await bot.SendMessage(chatId, "Регистрация отменена 😞", cancellationToken: ct);
                        return;

                    case "btn_teacher":
                        if (db.Users.Any(u => u.TgId == query.From.Id))
                        {
                            await bot.DeleteMessage(chatId, messageId, ct);
                            await bot.SendMessage(chatId, "Вы уже зарегистрировались!", cancellationToken: ct);
                            return;
                        }
                        TeacherDB newTeacher = new TeacherDB();
                        newTeacher.TgId = query.From.Id;
                        newTeacher.TgUsername = query.From.Username ?? "AnonymousTeacher";
                        db.Users.Add(newTeacher);
                        await bot.DeleteMessage(chatId, messageId, ct);
                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirm") },
                            new[] {InlineKeyboardButton.WithCallbackData("❌ Изменить роль", "reg:reject") }
                        });
                        var messageToDelete = await bot.SendMessage(chatId, "Ваша роль: 👨‍🏫 преподаватель", cancellationToken: ct,replyMarkup: inlineKeyboard);
                        newTeacher.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                        return;

                    case "btn_student":
                        if (db.Users.Any(u => u.TgId == query.From.Id))
                        {
                            await bot.DeleteMessage(chatId, messageId, ct);
                            await bot.SendMessage(chatId, "Вы уже зарегистрировались!", cancellationToken: ct);
                            return;
                        }
                        StudentDB newStudent = new StudentDB();
                        newStudent.TgId = query.From.Id;
                        newStudent.TgUsername = query.From.Username ?? "AnonymousStudent";
                        db.Users.Add(newStudent);
                        await bot.DeleteMessage(chatId, messageId, ct);
                        inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "reg:confirm") },
                            new[] {InlineKeyboardButton.WithCallbackData("❌ Изменить роль", "reg:reject") }
                        });
                        messageToDelete = await bot.SendMessage(chatId, "Ваша роль: 🧑‍🎓 студент", cancellationToken: ct, replyMarkup: inlineKeyboard);
                        newStudent.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                        return;

                    case "confirm":
                        var user = db.Users.FirstOrDefault(s=>s.TgId == query.From.Id);
                        if (user == null) throw new InvalidOperationException($"Пользователь с TgId {query.From.Id} не найден в БД."); ; 
                        await bot.DeleteMessage(chatId, user.LastInlineMessageId, ct);
                        user.LastInlineMessageId = 0;
                        await bot.SendMessage(chatId, "Отправьте ваше ФИО одним сообщением.", cancellationToken: ct);
                        user.State = "WaitingForFullName";
                        db.SaveChanges();
                        return;

                    case "reject":
                        user = db.Users.FirstOrDefault(t=>t.TgId == query.From.Id);
                        if (user == null) return;
                        await bot.EditMessageText(chatId, user.LastInlineMessageId, "Регистрация отменена 😞");
                        db.Users.Remove(user);
                        db.SaveChanges();
                        return;

                    case "rejectFullName":
                        user = db.Users.FirstOrDefault(u=> u.TgId == query.From.Id);
                        if (user == null) throw new InvalidOperationException($"Пользователь с TgId {query.From.Id} не найден в БД.");
                        user.Name = null;
                        user.Surname = null;
                        user.Patronymic = null;
                        await bot.DeleteMessage(chatId,user.LastInlineMessageId,ct);
                        messageToDelete = await bot.SendMessage(chatId, "Введите ваше ФИО");
                        user.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                        return;

                    case "confirmFullName":
                        user = db.Users.FirstOrDefault(u=>u.TgId == query.From.Id);
                        if (user == null) throw new InvalidOperationException($"Пользователь с TgId {query.From.Id} не найден в БД.");

                        await bot.EditMessageReplyMarkup(chatId, user.LastInlineMessageId, replyMarkup: null, cancellationToken: ct);
                        await bot.EditMessageText(chatId, user.LastInlineMessageId, "Корректность данных подтверждена ✅", cancellationToken: ct);

                        switch(user)
                        {
                            case StudentDB stud:
                                await bot.SendMessage(chatId, "Введите свой курс и группу. Например: 3 ИСИП 124");
                                stud.State = "WaitForGroup";
                                db.SaveChanges();
                                break;

                            case TeacherDB teach:
                                await bot.SendMessage(chatId, "Введите свою специальность. Например Разработка программных модулей");
                                teach.State = "WaitForSpecialty";
                                db.SaveChanges();
                                break;
                        }
                        return;

                    case "rejectGroup":
                        var student = db.Students.FirstOrDefault(u => u.TgId == query.From.Id);
                        if (student == null) throw new InvalidOperationException($"Студент с TgId {query.From.Id} не найден в БД.");
                        student.Course = 0;
                        student.Group = 0;
                        student.Speciality = null;
                        await bot.DeleteMessage(chatId, student.LastInlineMessageId, ct);
                        messageToDelete = await bot.SendMessage(chatId, "Введите свой курс и группу. Например: 3 ИСИП 124",cancellationToken:ct);
                        student.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                        return;

                    case "confirmGroup":
                        student = db.Students.FirstOrDefault(s=>s.TgId == query.From.Id); if (student == null) throw new InvalidOperationException($"Студент с TgId {query.From.Id} не найден в БД.");
                        await bot.DeleteMessage(chatId, student.LastInlineMessageId, ct);
                        messageToDelete = await bot.SendMessage(chatId, "Отправьте фото Вашего студенческого билета", cancellationToken: ct);
                        student.LastInlineMessageId = messageToDelete.Id;
                        student.State = "WaitingForStudentCard";
                        db.SaveChanges();
                        return;

                    case "rejectPhoto":
                        student = db.Students.FirstOrDefault(s => s.TgId == query.From.Id); if (student == null) throw new InvalidOperationException($"Студент с TgId {query.From.Id} не найден в БД.");
                        await bot.DeleteMessage(chatId, student.LastInlineMessageId, ct);
                        messageToDelete = await bot.SendMessage(chatId, "Отправьте фото Вашего студенческого билета", cancellationToken: ct);
                        student.StudentCardFileId = null;
                        student.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                        return;

                    case "confirmPhoto":
                        student = db.Students.FirstOrDefault(s => s.TgId == query.From.Id); if (student == null) throw new InvalidOperationException($"Студент с TgId {query.From.Id} не найден в БД.");
                        await bot.DeleteMessage(chatId, student.LastInlineMessageId, ct);
                        student.State = "AwaitingForVerification";
                        student.LastInlineMessageId = 0;
                        db.SaveChanges();
                        await bot.SendMessage(chatId, "Все готово!🎉 Ожидайте проверку данных. Результат проверки будет отправлен в этот чат.", cancellationToken: ct);
                        
                        return;

                    case "confirmSpecialty":
                        var teacher = db.Teachers.FirstOrDefault(s => s.TgId == query.From.Id); if (teacher == null) throw new InvalidOperationException($"Преподаватель с TgId {query.From.Id} не найден в БД.");
                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, ct);
                        teacher.State = "AwaitingForVerification";
                        db.SaveChanges();
                        await bot.SendMessage(chatId, "Все готово!🎉 Ожидайте проверку данных. Результат проверки будет отправлен в этот чат.", cancellationToken: ct);
                        return;

                    case "rejectSpecialty":
                        teacher = db.Teachers.FirstOrDefault(s => s.TgId == query.From.Id); if (teacher == null) throw new InvalidOperationException($"Преподаватель с TgId {query.From.Id} не найден в БД.");
                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, ct);
                        teacher.Specialty = null;
                        await bot.SendMessage(chatId, "Введите свою специальность. Например ~Разработка программных модулей~");
                        teacher.State = "WaitForSpecialty";
                        db.SaveChanges();   
                        return;
                }
            }
            break;

        case "admin":
            
            switch(command)
            {
                case "cancel":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var admin = db.Admins.FirstOrDefault(s => s.TgId == query.From.Id); if (admin == null) throw new InvalidOperationException($"Админ с TgId {query.From.Id} не найден в БД.");
                        if (admin.LastInlineMessageId == 0) return;
                        await bot.DeleteMessage(chatId, admin.LastInlineMessageId, ct);
                        admin.LastInlineMessageId = 0;
                        db.SaveChanges();
                    }

                    return;

                case "startUserVerification":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var admin = db.Admins.FirstOrDefault(s => s.TgId == query.From.Id); if (admin == null) throw new InvalidOperationException($"Админ с TgId {query.From.Id} не найден в БД.");

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🧑‍🎓 Студенты", "admin:startStudentVerification"),
                                InlineKeyboardButton.WithCallbackData("👨‍🏫 Преподаватели", "admin:startTeacherVerification")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена", "admin:cancel")
                            }
                        });
                
                        var messageToDelete = await bot.EditMessageText(
                            chatId: chatId,
                            messageId: messageId,
                            text: "Кого вы хотите верифицировать?",
                            replyMarkup: inlineKeyboard,
                            cancellationToken: ct
                        );

                        admin.LastInlineMessageId = messageToDelete.Id;
                        db.SaveChanges();
                    }
                    return;

                case "startStudentVerification":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        if (waitingUsers.Count == 0)
                        {
                            await bot.EditMessageText(chatId, messageId, "Нет заявок, ожидающих верификации.", cancellationToken: ct);
                            return;
                        }

                        int index = 0;
                        int prevIndex = index > 0 ? index - 1 : 0;
                        int nextIndex = index < waitingUsers.Count - 1 ? index + 1 : waitingUsers.Count - 1;

                        var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✅ Принять", $"verify:confirm:{waitingUsers[index].TgId}"),
                                InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verify:reject:{waitingUsers[index].TgId}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("⬅️", $"verify:page:{prevIndex}"),
                                InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingUsers.Count}", "empty"),
                                InlineKeyboardButton.WithCallbackData("➡️", $"verify:page:{nextIndex}")
                            }
                        });

                        await bot.SendPhoto(
                            chatId: chatId,
                            photo: InputFile.FromFileId(waitingUsers[index].StudentCardFileId!),
                            caption: $"Верификация пользователя @{waitingUsers[index].TgUsername}\nФИО: {waitingUsers[index].Surname} {waitingUsers[index].Name} {waitingUsers[index].Patronymic}\nГруппа: {waitingUsers[index].Course} {waitingUsers[index].Speciality} {waitingUsers[index].Group}",
                            replyMarkup: singleButtonMarkup,
                            cancellationToken: ct
                        );
                    }
                    return;

                case "startTeacherVerification":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var waitingTeachers = db.Teachers.Where(u => u.State == "AwaitingForVerification").ToList();

                        if (waitingTeachers.Count == 0)
                        {
                            await bot.EditMessageText(chatId, messageId, "Нет заявок на верификацию преподавателей.", cancellationToken: ct);
                            return;
                        }

                        int index = 0;
                        int prevIndex = index > 0 ? index - 1 : 0;
                        int nextIndex = index < waitingTeachers.Count - 1 ? index + 1 : waitingTeachers.Count - 1;

                        var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✅ Принять", $"verifyTeacher:confirm:{waitingTeachers[index].TgId}"),
                                InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verifyTeacher:reject:{waitingTeachers[index].TgId}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("⬅️", $"verifyTeacher:page:{prevIndex}"),
                                InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingTeachers.Count}", "empty"),
                                InlineKeyboardButton.WithCallbackData("➡️", $"verifyTeacher:page:{nextIndex}")
                            }
                        });

                        string caption = $"Верификация преподавателя @{waitingTeachers[index].TgUsername}\n" +
                                         $"ФИО: {waitingTeachers[index].Surname} {waitingTeachers[index].Name} {waitingTeachers[index].Patronymic}\n" +
                                         $"Кафедра/специальность: {waitingTeachers[index].Specialty ?? "не указана"}";

                        await bot.EditMessageText(
                            chatId: chatId,
                            messageId: messageId,
                            text: caption,
                            replyMarkup: singleButtonMarkup,
                            cancellationToken: ct
                        );
                    }
                    return;

                case "allUsers":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var allUsers = db.Users.ToList();
                        string messageWithUsers = "";
                        foreach (var user in allUsers)
                        {
                            string role = user switch
                            {
                                TeacherDB => "👨‍🏫 преподаватель",
                                StudentDB => "🧑‍🎓 студент",
                                AdminDB => "👤 администратор",
                                _ => "неизвестно"
                            };
                            messageWithUsers += $"{user.TgUsername} - {role} ({(user.Isverified ? "верифицирован" : "не верифицирован")})\n";
                        }
                        await bot.EditMessageText(chatId, messageId, messageWithUsers, cancellationToken: ct);
                    }
                return;
            }
        break;

        case "verify":
            switch(command)
            {
                case "page":

                    int page = Convert.ToInt32(parts[2]);

                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        if(waitingUsers.Count != 0)
                        {
                            int index = page;
                            if (index >= waitingUsers.Count) index = waitingUsers.Count - 1;
                            if (index < 0) index = 0;

                            int prevIndex = index > 0 ? index - 1 : 0;
                            int nextIndex = index < waitingUsers.Count - 1 ? index + 1 : waitingUsers.Count - 1;

                            var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verify:confirm:{waitingUsers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verify:reject:{waitingUsers[index].TgId}")

                                },
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("⬅️", $"verify:page:{prevIndex}"),
                                    InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingUsers.Count}", "empty"),
                                    InlineKeyboardButton.WithCallbackData("➡️", $"verify:page:{nextIndex}"),

                                }
                                });

                                await bot.EditMessageMedia(
                                    chatId: chatId,
                                    messageId: messageId,
                                    media: new InputMediaPhoto(InputFile.FromFileId(waitingUsers[index].StudentCardFileId!))
                                    {
                                        Caption = $"Верификация пользователя @{waitingUsers[index].TgUsername}\nФИО: {waitingUsers[index].Surname} {waitingUsers[index].Name} {waitingUsers[index].Patronymic}\nГруппа: {waitingUsers[index].Course} {waitingUsers[index].Speciality} {waitingUsers[index].Group}"
                                    },
                                    replyMarkup: singleButtonMarkup,
                                    cancellationToken: ct
                            );
                        }
                    }

                    return;

                case "confirm":
                    long id = long.Parse(parts[2]);
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Students.FirstOrDefault(u => u.TgId == id);
                        if (user != null)
                        {
                            user.Isverified = true;
                            user.State = "verified";
                            db.SaveChanges();

                            try { await bot.SendMessage(user.TgId, "Поздравляем! Ваша учетная запись верифицирована!",cancellationToken: ct); }
                            catch (Exception) { }
                        }

                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        var singleBackButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                        InlineKeyboardButton.WithCallbackData("Выход", $"admin:cancel"),
                         });

                        if (waitingUsers.Count == 0)
                        {
                            await bot.DeleteMessage(chatId, messageId, cancellationToken: ct);

                            var messageToDelete = await bot.SendMessage(
                                chatId: chatId,
                                text: "Все заявки рассмотрены!",
                                replyMarkup: singleBackButtonMarkup,
                                cancellationToken: ct
                            );

                            var admin = db.Admins.FirstOrDefault(a => a.TgId == query.From.Id);
                            admin.LastInlineMessageId = messageToDelete.Id;
                            db.SaveChanges();
                        }
                        else
                        {
                            int index = 0;
                            int prevIndex = index > 0 ? index - 1 : 0;
                            int nextIndex = index < waitingUsers.Count - 1 ? index + 1 : waitingUsers.Count - 1;

                            var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verify:confirm:{waitingUsers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verify:reject:{waitingUsers[index].TgId}")
                                },
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("⬅️", $"verify:page:{prevIndex}"),
                                    InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingUsers.Count}", "empty"),
                                    InlineKeyboardButton.WithCallbackData("➡️", $"verify:page:{nextIndex}"),
                                }
                            });

                            await bot.EditMessageMedia(
                                chatId: chatId,
                                messageId: messageId,
                                media: new InputMediaPhoto(InputFile.FromFileId(waitingUsers[index].StudentCardFileId!))
                                {
                                    Caption = $"Верификация пользователя @{waitingUsers[index].TgUsername}\nФИО: {waitingUsers[index].Surname} {waitingUsers[index].Name} {waitingUsers[index].Patronymic}\nГруппа: {waitingUsers[index].Course} {waitingUsers[index].Speciality} {waitingUsers[index].Group}"
                                },
                                replyMarkup: singleButtonMarkup,
                                cancellationToken: ct
                            );
                        }
                    }

                    return;

                case "reject":
                    id = long.Parse(parts[2]);
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Students.FirstOrDefault(u => u.TgId == id);
                        if (user != null)
                        {
                            user.State = "Rejected";
                            db.SaveChanges();

                            try { await bot.SendMessage(user.TgId, "Упс... Ваша заявка на верификацию отклонена! Проверьте свои данные через /profile → ✏️ Редактировать информацию и отправьте заявку заново.", cancellationToken: ct); }
                            catch (Exception) { }
                        }

                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        var singleBackButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                        InlineKeyboardButton.WithCallbackData("Выход", $"admin:cancel"),
                         });

                        if (waitingUsers.Count == 0)
                        {
                            await bot.DeleteMessage(chatId, messageId, cancellationToken: ct);

                            var messageToDelete = await bot.SendMessage(
                                chatId: chatId,
                                text: "Все заявки рассмотрены!",
                                replyMarkup: singleBackButtonMarkup,
                                cancellationToken: ct
                            );
                            var admin = db.Admins.FirstOrDefault(a => a.TgId == query.From.Id);
                            admin.LastInlineMessageId = messageToDelete.Id;
                            db.SaveChanges();
                        }
                        else
                        {
                            int index = 0;
                            int prevIndex = index > 0 ? index - 1 : 0;
                            int nextIndex = index < waitingUsers.Count - 1 ? index + 1 : waitingUsers.Count - 1;

                            var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verify:confirm:{waitingUsers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verify:reject:{waitingUsers[index].TgId}")
                                },
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("⬅️", $"verify:page:{prevIndex}"),
                                    InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingUsers.Count}", "empty"),
                                    InlineKeyboardButton.WithCallbackData("➡️", $"verify:page:{nextIndex}"),
                                }
                            });

                            await bot.EditMessageMedia(
                                chatId: chatId,
                                messageId: messageId,
                                media: new InputMediaPhoto(InputFile.FromFileId(waitingUsers[index].StudentCardFileId!))
                                {
                                    Caption = $"Верификация пользователя @{waitingUsers[index].TgUsername}\nФИО: {waitingUsers[index].Surname} {waitingUsers[index].Name} {waitingUsers[index].Patronymic}\nГруппа: {waitingUsers[index].Course} {waitingUsers[index].Speciality} {waitingUsers[index].Group}"
                                },
                                replyMarkup: singleButtonMarkup,
                                cancellationToken: ct
                            );
                        }
                    }
                    return;
            }
        break;

        case "verifyTeacher":
            switch (command)
            {
                case "page":
                    int teacherPage = Convert.ToInt32(parts[2]);

                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var waitingTeachers = db.Teachers.Where(u => u.State == "AwaitingForVerification").ToList();

                        if (waitingTeachers.Count != 0)
                        {
                            int index = teacherPage;
                            if (index >= waitingTeachers.Count) index = waitingTeachers.Count - 1;
                            if (index < 0) index = 0;

                            int prevIndex = index > 0 ? index - 1 : 0;
                            int nextIndex = index < waitingTeachers.Count - 1 ? index + 1 : waitingTeachers.Count - 1;

                            var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verifyTeacher:confirm:{waitingTeachers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verifyTeacher:reject:{waitingTeachers[index].TgId}")
                                },
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("⬅️", $"verifyTeacher:page:{prevIndex}"),
                                    InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingTeachers.Count}", "empty"),
                                    InlineKeyboardButton.WithCallbackData("➡️", $"verifyTeacher:page:{nextIndex}")
                                }
                            });

                            string caption = $"Верификация преподавателя @{waitingTeachers[index].TgUsername}\n" +
                                             $"ФИО: {waitingTeachers[index].Surname} {waitingTeachers[index].Name} {waitingTeachers[index].Patronymic}\n" +
                                             $"Кафедра/специальность: {waitingTeachers[index].Specialty ?? "не указана"}";

                            try
                            {
                                await bot.EditMessageText(chatId, messageId, caption, replyMarkup: singleButtonMarkup, cancellationToken: ct);
                            }
                            catch (Exception) { }
                        }
                    }
                    return;

                case "confirm":
                case "reject":
                    long teacherId = long.Parse(parts[2]);
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(u => u.TgId == teacherId);

                        if (teacher != null)
                        {
                            if (command == "confirm")
                            {
                                teacher.Isverified = true;
                                teacher.State = "verified";
                            }
                            else
                            {
                                teacher.State = "Rejected";
                            }
                            db.SaveChanges();

                            try { await bot.SendMessage(teacher.TgId, command == "confirm" ? "Поздравляем! Ваша учетная запись верифицирована!" : "Упс... Ваша заявка на верификацию отклонена! Проверьте свои данные через /profile → ✏️ Редактировать информацию и отправьте заявку заново.", cancellationToken: ct); }
                            catch (Exception) { }
                        }

                        var waitingTeachers = db.Teachers.Where(u => u.State == "AwaitingForVerification").ToList();

                        if (waitingTeachers.Count == 0)
                        {
                            var singleBackButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                InlineKeyboardButton.WithCallbackData("Выход", $"admin:cancel"),
                            });

                            await bot.EditMessageText(chatId, messageId, "Все заявки рассмотрены!", replyMarkup: singleBackButtonMarkup, cancellationToken: ct);

                            var admin = db.Admins.FirstOrDefault(a => a.TgId == query.From.Id);
                            admin.LastInlineMessageId = messageId;
                            db.SaveChanges();
                        }
                        else
                        {
                            int index = 0;
                            int prevIndex = 0;
                            int nextIndex = index < waitingTeachers.Count - 1 ? index + 1 : waitingTeachers.Count - 1;

                            var singleButtonMarkup = new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verifyTeacher:confirm:{waitingTeachers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verifyTeacher:reject:{waitingTeachers[index].TgId}")
                                },
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("⬅️", $"verifyTeacher:page:{prevIndex}"),
                                    InlineKeyboardButton.WithCallbackData($"{index + 1}/{waitingTeachers.Count}", "empty"),
                                    InlineKeyboardButton.WithCallbackData("➡️", $"verifyTeacher:page:{nextIndex}")
                                }
                            });

                            string caption = $"Верификация преподавателя @{waitingTeachers[index].TgUsername}\n" +
                                             $"ФИО: {waitingTeachers[index].Surname} {waitingTeachers[index].Name} {waitingTeachers[index].Patronymic}\n" +
                                             $"Кафедра/специальность: {waitingTeachers[index].Specialty ?? "не указана"}";

                            await bot.EditMessageText(chatId, messageId, caption, replyMarkup: singleButtonMarkup, cancellationToken: ct);
                        }
                    }
                    return;
            }
        break;

        case "teacher":
            switch (command)
            {
                case "uploadNote":
                    using(ApplicationContext db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);

                        if(teacher == null)
                        {
                            await bot.SendMessage(chatId, "Возникла ошибка при поиске преподавателя в БД");
                            return;
                        }

                        teacher.State = "WaitingForUploadNoteName";

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:cancelUploadNote")
                        }
                        });

                        var messageToDelete = await bot.EditMessageText(chatId, messageId, "Пришлите название заметки", cancellationToken: ct, replyMarkup: inlineKeyboard);
                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                return;

                case "cancelUploadNote":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);

                        if (teacher == null)
                        {
                            await bot.SendMessage(chatId, "Возникла ошибка при поиске преподавателя в БД");
                            return;
                        }

                        teacher.State = null;

                        await bot.DeleteMessage(chatId, messageId);

                        await bot.SendMessage(chatId, "Загрузка заметки отменена.");
                        teacher.LastInlineMessageId = 0;
                        db.SaveChanges();
                    }
                        return;

                case "rejectNoteName":
                    using (var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);

                        if (teacher == null) return;

                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId);

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
{
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:cancelUploadNote")
                        }
                        });

                        var messageToDelete = await bot.SendMessage(chatId, "Пришлите название заметки", cancellationToken: ct);

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                        return;

                case "confirmNoteName":
                    using(var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId==query.From.Id);

                        if(teacher == null) return;

                        await bot.EditMessageReplyMarkup(
                            chatId: chatId,
                            messageId: teacher.LastInlineMessageId,
                            replyMarkup: null,
                            cancellationToken: ct
                            );

                        var message = await bot.SendMessage(chatId, "Пришлите заметку сообщением или файлом (docx, txt)", cancellationToken: ct);

                        teacher.LastInlineMessageId = message.Id;

                        teacher.State = "WaitingForUploadNote";

                        db.SaveChanges();
                    }
                    return;

                case "confirmNoteRename":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteId = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n=>n.Id==noteId);
                        if(teacher == null || note == null) return;

                        teacher.State = null;
                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct );

                        note.NoteName = teacher.NoteName!;
                        teacher.NoteName = null;

                        teacher.LastInlineMessageId = (await bot.SendMessage(chatId,"Заметка успешно переименована!",cancellationToken:ct )).MessageId;

                        db.SaveChanges();
                        
                    }
                    return;

                case "rejectNoteRename":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteId = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteId);

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                  InlineKeyboardButton.WithCallbackData("✏️ Изменить название",$"teacher:editNoteNameNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✏️ Изменить содержимое",$"teacher:editNoteContentNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:menu")
                            }
                        }
                        );

                         await bot.EditMessageText
                            (
                            chatId: chatId,
                            messageId: teacher.LastInlineMessageId,
                            text: $"Название: <b>{WebUtility.HtmlEncode(note.NoteName)}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{WebUtility.HtmlEncode(note.Text.Length > 3000 ? note.Text.Substring(0, 3000) + "..." : note.Text)}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {WebUtility.HtmlEncode(note.FileName)}")}",
                            replyMarkup: inlineKeyboard,
                            parseMode: ParseMode.Html
                            );

                        db.SaveChanges();
                    }
                    return;

                case "editNote":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var teacherNotes = db.teacherNotes.Where(t=>t.TeacherTgId == query.From.Id).ToList();

                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        if (teacher == null) return;

                        try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId); }
                        catch (Exception) { }

                        if (teacherNotes.Count == 0)
                        {
                            await bot.SendMessage(chatId, "У Вас нет заметок 😞", cancellationToken: ct);
                            teacher.LastInlineMessageId = 0;
                            db.SaveChanges();
                            return;
                        }

                        var buttons = new List<InlineKeyboardButton[]>();

                        foreach (var note in teacherNotes)
                        {
                            var row = new[]
                            {
                                 InlineKeyboardButton.WithCallbackData
                                 (
                                     text: $"📋 {note.NoteName}",
                                     callbackData: $"teacher:editNoteNum:{note.Id}"
                                 )
                             };

                            buttons.Add(row);
                        }

                        buttons.Add(new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "teacher:menu")
                        });

                        var inlineKeyboard = new InlineKeyboardMarkup(buttons);

                        var messageToDelete = await bot.SendMessage(chatId, "Выберите заметку", replyMarkup: inlineKeyboard, cancellationToken: ct);

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }

                    return;

                case "editNoteNum":
                    using (var db = new ApplicationContext(options))
                    {
                        int num = int.Parse(parts[2]);

                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);

                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == num);

                        if (teacher == null || note == null) return;

                        teacher.State = null;

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                  InlineKeyboardButton.WithCallbackData("✏️ Изменить название",$"teacher:editNoteNameNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✏️ Изменить содержимое",$"teacher:editNoteContentNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🗑️ Удалить заметку",$"teacher:deleteNoteNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:editNote")
                            }
                        }
                        );

                        var messageToDelete = await bot.EditMessageText
                            (
                            chatId: chatId,
                            messageId: teacher.LastInlineMessageId,
                            text: $"Название: <b>{WebUtility.HtmlEncode(note.NoteName)}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{WebUtility.HtmlEncode(note.Text.Length > 3000 ? note.Text.Substring(0, 3000) + "..." : note.Text)}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {WebUtility.HtmlEncode(note.FileName)}")}", replyMarkup: inlineKeyboard,
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                            );

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                    return;

                case "editNoteNameNum":
                    using(var db = new ApplicationContext(options))
                    {
                        int noteNum = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n=>n.Id == noteNum);
                        if (teacher == null || note == null) return;

                        await bot.EditMessageText(chatId, teacher.LastInlineMessageId, "Пришлите новое название заметки.");

                        teacher.State = $"WaitingSendNewNoteName:{noteNum}";

                        db.SaveChanges();
                    }
                    return;

                case "editNoteContentNum":
                    using(var db = new ApplicationContext(options))
                    {
                        int noteNum = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n=>n.Id == noteNum);

                        if (teacher == null || note == null)
                        {
                            await bot.SendMessage(chatId, "ошибка");
                            return;
                        }

                         try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                         catch (Exception) { }
                         
                         var inlineKeyboard = new InlineKeyboardMarkup(new[]
                         {
                             InlineKeyboardButton.WithCallbackData("❌ Отмена", $"teacher:editNoteNum:{noteNum}")
                         });
                         
                         var messageToDelete = await bot.SendMessage(chatId, "Пришлите новое содержимое заметки текстом или файлом (docx, txt)", cancellationToken: ct, replyMarkup: inlineKeyboard); ;
                         
                         teacher.LastInlineMessageId = messageToDelete.Id;
                         teacher.State = $"WaitingSendNewNoteContent:{noteNum}";
                         
                         db.SaveChanges();
                    }    
                    return;

                case "rejectNoteContent":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteNum = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteNum);

                        if (teacher == null || note == null) return;

                        teacher.State = null;

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                       {
                            new[]
                            {
                                  InlineKeyboardButton.WithCallbackData("✏️ Изменить название",$"teacher:editNoteNameNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✏️ Изменить содержимое",$"teacher:editNoteContentNum:{note.Id}")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:editNote")
                            }
                        }
                       );

                        var messageToDelete = await bot.EditMessageText
                            (
                            chatId: chatId,
                            messageId: teacher.LastInlineMessageId,
                            text: $"Название: <b>{WebUtility.HtmlEncode(note.NoteName)}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{WebUtility.HtmlEncode(note.Text.Length > 3000 ? note.Text.Substring(0, 3000) + "..." : note.Text)}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {WebUtility.HtmlEncode(note.FileName)}")}", replyMarkup: inlineKeyboard,
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                            );

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                    return;

                case "confirmNoteTextContent":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteNum = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteNum);

                        if (teacher == null || note == null) return;

                        note.Text = teacher.NoteName;
                        note.CreatedTime = DateTime.Now;
                        teacher.NoteName = null;

                        db.studyAnnouncements.Where(a => a.TeacherDocumentId == noteNum).ExecuteDelete();

                        try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }

                        teacher.LastInlineMessageId = 0;
                        
                        db.SaveChanges();

                        var editMessage = await bot.SendMessage(chatId, "Изменения применяются...", cancellationToken: ct);

                        AI aiService = new AI(config);
                        try
                        {
                            await aiService.GenerationNote(options, note.Id, bot);
                            await bot.EditMessageText(chatId, editMessage.Id, "Содержимое заметки успешно изменено!", cancellationToken: ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] (GenerationNote): {ex.Message}");
                            await bot.EditMessageText(chatId, editMessage.Id, "⚠️ Содержимое сохранено, но обработать его не удалось. Попробуйте загрузить его еще раз позже.", cancellationToken: ct);
                        }
                    }
                    return;

                case "confirmNoteDocumentContent":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteId = int.Parse(parts[2]);
                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteId);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        if (note == null || teacher == null || teacher.NoteName == null) return;

                        string[] infoAboutNote = teacher.NoteName.Split('\n', 3);
                        note.FileId = infoAboutNote[0];
                        note.FileName = infoAboutNote[1];
                        note.Text = infoAboutNote[2] == "" ? null : infoAboutNote[2];
                        note.CreatedTime = DateTime.Now;
                        teacher.NoteName = null;

                        try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }
                        teacher.LastInlineMessageId = 0;

                        db.studyAnnouncements.Where(a => a.TeacherDocumentId == noteId).ExecuteDelete();

                        db.SaveChanges();

                        var messageToDelete = await bot.SendMessage(chatId, "Обработка загруженного файла... Время ожидания зависит от его объема, пожалуйста, подождите.", cancellationToken: ct);

                        AI aiService = new AI(config);
                        try
                        {
                            await aiService.GenerationNote(options, note.Id, bot);
                            await bot.DeleteMessage(chatId, messageToDelete.Id, cancellationToken: ct);
                            await bot.SendMessage(chatId, "Содержание заметки успешно обновлено!", cancellationToken: ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] (GenerationNote): {ex.Message}");
                            await bot.EditMessageText(chatId, messageToDelete.Id, "⚠️ Файл сохранен, но обработать его не удалось. Попробуйте загрузить его еще раз позже.", cancellationToken: ct);
                        }
                    }
                    return;

                case "deleteNoteNum":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteId = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        if (teacher == null) return;

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]{
                        new[] {InlineKeyboardButton.WithCallbackData("✅ Подтвердить", $"teacher:confirmDeleteNoteNum:{noteId}") },
                        new[] {InlineKeyboardButton.WithCallbackData("❌ Отмена", $"teacher:editNoteNum:{noteId}") }
                        });

                        await bot.EditMessageText(chatId, teacher.LastInlineMessageId, "Подтвердить удаление? Отменить это действие невозможно.");
                        await bot.EditMessageReplyMarkup(chatId, teacher.LastInlineMessageId, inlineKeyboard, cancellationToken: ct);

                    }
                    return;

                case "confirmDeleteNoteNum":
                    using (var db = new ApplicationContext(options))
                    {
                        int noteId = int.Parse(parts[2]);
                        var note = db.teacherNotes.FirstOrDefault(n=>n.Id == noteId);
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        if(note == null || teacher == null) return;

                        db.teacherNotes.Remove(note);

                        await bot.DeleteMessage(chatId,teacher.LastInlineMessageId, cancellationToken: ct);

                        await bot.SendMessage(chatId,"Заметка успешно удалена.", cancellationToken: ct);
                        teacher.LastInlineMessageId = 0;
                        db.SaveChanges();
                    }
                    return;

                case "cancel":
                    using(var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        if (teacher == null) return;
                        try { await bot.DeleteMessage(chatId, messageId, ct); }
                        catch (Exception) { }
                        return;
                    }

                case "menu":
                    using(var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);

                        if (teacher == null) return;

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                       new[]
                           {
                    InlineKeyboardButton.WithCallbackData("📋 Загрузить новую заметку","teacher:uploadNote"),
                           },
                          new[]
                          {
                    InlineKeyboardButton.WithCallbackData("✏️ Управление заметками","teacher:editNote"),
                          },
                          new[]
                         {
                    InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:cancel")
                         }
                        });

                        try { await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct); }
                        catch (Exception) { }

                        var messageToDelete = await bot.SendMessage(chatId, "Для навигации по меню воспользуйтесь кнопками", replyMarkup: inlineKeyboard, cancellationToken: ct);

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                    return;
            }

        break;

        case "student":
            switch(command)
            {
                case "cancel":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var student = db.Students.FirstOrDefault(s=>s.TgId == query.From.Id);
                        if (student == null)
                            return;
                        student.State=null;
                        student.LastInlineMessageId=0;
                        db.SaveChanges();
                    }
                    await bot.DeleteMessage(chatId, messageId, ct);
                    return;
            }
            return;

        case "help":
            switch (command)
            {
                case "menu":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Users.FirstOrDefault(u => u.TgId == query.From.Id);

                        var buttons = new List<InlineKeyboardButton[]>
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("📝 Как зарегистрироваться?", "help:topic:1") },
                            new[] { InlineKeyboardButton.WithCallbackData("⏳ Сколько ждать проверки заявки?", "help:topic:2") },
                            new[] { InlineKeyboardButton.WithCallbackData("⛔ Что делать, если заявку отклонили?", "help:topic:3") }
                        };

                        if (user != null)
                        {
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Как изменить свои данные?", "help:topic:4") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🗑️ Как удалить профиль?", "help:topic:5") });
                        }

                        if (user is StudentDB)
                        {
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❓ Как задать вопрос боту?", "help:topic:6") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🤔 Почему бот не нашел ответ?", "help:topic:7") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🪪 Зачем нужно фото студенческого?", "help:topic:8") });
                        }

                        if (user is TeacherDB)
                        {
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 Как загрузить заметку?", "help:topic:9") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📎 Какие файлы можно загрузить?", "help:topic:10") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("✏️ Как изменить или удалить заметку?", "help:topic:11") });
                            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🎯 Как заметка попадет нужной группе?", "help:topic:12") });
                        }

                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть", "help:close") });

                        await bot.EditMessageText(chatId, messageId, "Выберите интересующую тему", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
                    }
                    return;

                case "topic":
                    {
                        string answer = parts[2] switch
                        {
                            "1" => "📝 Как зарегистрироваться?\n\n" +
                                   "1. Отправьте команду /start и выберите свою роль: студент или преподаватель.\n" +
                                   "2. Отправьте ФИО одним сообщением, например: Иванов Иван Иванович.\n" +
                                   "3. Студенту нужно указать курс и группу (например: 3 ИСИП 124) и отправить фото студенческого билета. Преподавателю нужно указать свою специальность.\n" +
                                   "4. После этого заявка уйдет на проверку администратору.",

                            "2" => "⏳ Сколько ждать проверки заявки?\n\n" +
                                   "Заявки проверяет администратор вручную, поэтому это может занять некоторое время. " +
                                   "Результат проверки придет в этот чат. Пока заявка не одобрена, функции бота недоступны.",

                            "3" => "⛔ Что делать, если заявку отклонили?\n\n" +
                                   "Скорее всего, в данных была ошибка или фото студенческого билета плохо читается.\n" +
                                   "Откройте /profile → ✏️ Редактировать информацию, заново введите данные и отправьте заявку еще раз.",

                            "4" => "👤 Как изменить свои данные?\n\n" +
                                   "Откройте /profile и нажмите ✏️ Редактировать информацию. Бот попросит заново ввести данные, как при регистрации.\n" +
                                   "⚠️ После изменения профиль снова отправится на проверку, и до ее прохождения функции бота будут недоступны.",

                            "5" => "🗑️ Как удалить профиль?\n\n" +
                                   "Откройте /profile, нажмите 🗑️ Удалить профиль и подтвердите удаление. Отменить это действие невозможно.\n" +
                                   "У преподавателя вместе с профилем удаляются все его заметки.\n" +
                                   "Зарегистрироваться заново можно командой /start.",

                            "6" => "❓ Как задать вопрос боту?\n\n" +
                                   "Отправьте команду /student, а затем напишите свой вопрос одним сообщением. Например: «Когда пересдача по математике?»\n" +
                                   "Чем конкретнее вопрос, тем точнее будет ответ.",

                            "7" => "🤔 Почему бот не нашел ответ?\n\n" +
                                   "Бот отвечает только на основе заметок, которые загрузили преподаватели для вашей группы. " +
                                   "Если преподаватель еще не загрузил нужную информацию, ответа не будет.\n" +
                                   "Также проверьте в /profile, что курс и группа указаны верно.",

                            "8" => "🪪 Зачем нужно фото студенческого билета?\n\n" +
                                   "По нему администратор проверяет, что вы действительно студент этой группы. " +
                                   "Это нужно, чтобы доступ к информации для группы был только у ее студентов.",

                            "9" => "📋 Как загрузить заметку?\n\n" +
                                   "1. Отправьте команду /teacher и нажмите 📋 Загрузить новую заметку.\n" +
                                   "2. Пришлите название заметки и подтвердите его.\n" +
                                   "3. Пришлите текст заметки сообщением или файлом.\n" +
                                   "После обработки студенты смогут задавать вопросы по этой заметке.",

                            "10" => "📎 Какие файлы можно загрузить?\n\n" +
                                    "Поддерживаются файлы .docx и .txt. К файлу можно добавить подпись, она тоже будет учтена.\n" +
                                    "Таблицы в .docx тоже обрабатываются, например ведомость с результатами студентов.",

                            "11" => "✏️ Как изменить или удалить заметку?\n\n" +
                                    "Отправьте /teacher → ✏️ Управление заметками и выберите нужную заметку. " +
                                    "Там можно изменить ее название, содержимое или удалить ее.",

                            "12" => "🎯 Как заметка попадет нужной группе?\n\n" +
                                    "Бот сам определяет группу по тексту заметки, поэтому указывайте ее явно, например: «3 ИСИП 124».\n" +
                                    "Если группа не указана, заметка будет доступна всем студентам.\n" +
                                    "Если в заметке список студентов с результатами, бот сохранит отдельную запись по каждому студенту, и студент сможет спросить о своем результате.",

                            _ => "Тема не найдена."
                        };

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад к темам", "help:menu") }
                        });

                        await bot.EditMessageText(chatId, messageId, answer, replyMarkup: inlineKeyboard, cancellationToken: ct);
                    }
                    return;

                case "close":
                    await bot.DeleteMessage(chatId, messageId, ct);
                    return;
            }
            return;

        case "profile":
            switch (command)
            {
                case "delete":
                    {
                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("✅ Да, удалить", "profile:confirmDelete") },
                            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "profile:cancel") }
                        });

                        await bot.EditMessageText(chatId, messageId, "Вы уверены, что хотите удалить профиль? Отменить это действие невозможно.", replyMarkup: inlineKeyboard, cancellationToken: ct);
                    }
                    return;

                case "confirmDelete":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Users.FirstOrDefault(u => u.TgId == query.From.Id);

                        if (user == null)
                        {
                            await bot.DeleteMessage(chatId, messageId, ct);
                            return;
                        }

                        if (user is TeacherDB)
                        {
                            var notes = db.teacherNotes.Where(n => n.TeacherTgId == user.TgId).ToList();
                            db.teacherNotes.RemoveRange(notes);
                        }

                        db.Users.Remove(user);
                        db.SaveChanges();
                    }
                    await bot.DeleteMessage(chatId, messageId, ct);
                    await bot.SendMessage(chatId, "Ваш профиль удален. Для повторной регистрации воспользуйтесь командой /start", cancellationToken: ct);
                    return;

                case "cancel":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Users.FirstOrDefault(u => u.TgId == query.From.Id);

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("🗑️ Удалить профиль", "profile:delete") },
                            new[] { InlineKeyboardButton.WithCallbackData("✏️ Редактировать информацию", "profile:edit") },
                            new[] { InlineKeyboardButton.WithCallbackData("❌ Закрыть меню", "profile:close") }
                        });

                        switch (user)
                        {
                            case StudentDB student:
                                await bot.EditMessageText(chatId, messageId, $"Роль: 🧑‍🎓 студент\nФИО: {student.Surname} {student.Name} {student.Patronymic}\nКурс: {student.Course}\nГруппа: {student.Group}\nВерифицирован: {(student.Isverified ? "✅" : "❌")}", replyMarkup: inlineKeyboard, cancellationToken: ct);
                                return;

                            case TeacherDB teacher:
                                await bot.EditMessageText(chatId, messageId, $"Роль: 🧑‍🏫 преподаватель\nФИО: {teacher.Surname} {teacher.Name} {teacher.Patronymic}\nСпециальность: {teacher.Specialty}\nВерифицирован: {(teacher.Isverified ? "✅" : "❌")}", replyMarkup: inlineKeyboard, cancellationToken: ct);
                                return;

                            case AdminDB admin:
                                await bot.EditMessageText(chatId, messageId, $"Роль: администратор", replyMarkup: inlineKeyboard, cancellationToken: ct);
                                return;

                            default:
                                await bot.DeleteMessage(chatId, messageId, ct);
                                return;
                        }
                    }

                case "edit":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Users.FirstOrDefault(u => u.TgId == query.From.Id);
                        if (user == null) return;

                        if (user is AdminDB)
                        {
                            await bot.EditMessageText(chatId, messageId, "Профиль администратора нельзя редактировать.", cancellationToken: ct);
                            return;
                        }

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("✅ Продолжить", "profile:confirmEdit") },
                            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "profile:cancel") }
                        });

                        await bot.EditMessageText(chatId, messageId, "После редактирования профиль будет отправлен на повторную верификацию. До ее прохождения функции бота будут недоступны. Продолжить?", replyMarkup: inlineKeyboard, cancellationToken: ct);
                    }
                    return;

                case "confirmEdit":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Users.FirstOrDefault(u => u.TgId == query.From.Id);
                        if (user == null) return;

                        user.Isverified = false;
                        user.State = "WaitingForFullName";
                        user.LastInlineMessageId = 0;
                        db.SaveChanges();

                        await bot.DeleteMessage(chatId, messageId, ct);
                        await bot.SendMessage(chatId, "Отправьте ваше ФИО одним сообщением.", cancellationToken: ct);
                    }
                    return;

                case "close":
                    await bot.DeleteMessage(chatId, messageId, ct);
                    return;
            }
            return;
    }
}

Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
{
    var errorMessage = exception.InnerException?.Message ?? exception.Message;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] ({source}): {errorMessage}");
    Console.ResetColor();
    return Task.CompletedTask;
}
