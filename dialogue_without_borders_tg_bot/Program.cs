using dialogue_without_borders_tg_bot;
using DocumentFormat.OpenXml.Drawing.Diagrams;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

// Чтение конфигурации
var builder = new ConfigurationBuilder();
builder.SetBasePath(Directory.GetCurrentDirectory());
builder.AddJsonFile("config.json");
var config = builder.Build();

// Подключение
var connectionString = config.GetConnectionString("DefaultConnection");
string botToken = config["BotConfiguration:BotToken"]
    ?? throw new InvalidOperationException("BotToken не найден в config.json");

var botClient = new TelegramBotClient(botToken);

var optionsBuilder = new DbContextOptionsBuilder<ApplicationContext>();
var options = optionsBuilder.UseSqlite(connectionString).Options;

// Инициализация базы данных при старте
using (var initDb = new ApplicationContext(options))
{
    //initDb.Database.EnsureDeleted();
    //initDb.Database.EnsureCreated();





    initDb.SaveChanges();
}

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    errorHandler: HandleErrorAsync,
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

Console.WriteLine("Бот запущен. Нажми Enter для безопасного завершения работы");
Console.ReadLine();

using (var db = new ApplicationContext(options))
{
    var users = db.Users.ToList();
    foreach (var user in users)
    {
        user.LastInlineMessageId = 0;
        user.State = null;
        Console.WriteLine($"Пользователь {user.Id} обработан");
    }
    db.SaveChanges();
}

cts.Cancel();

async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
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
async Task OnMessageReceived(ITelegramBotClient bot, Message msg, CancellationToken ct)
{
    long chatId = msg.Chat.Id;
    long fromUserId = msg.From?.Id ?? 0;

    if (msg.Text != null)
    {
        if (msg.Text == "/start")
        {
            using (ApplicationContext db = new ApplicationContext(options))
            {
                bool exists = db.Users.Any(u => u.TgId == fromUserId);

                if (exists)
                {
                    await bot.SendMessage(chatId, "Вы уже зарегистрировались!", cancellationToken: ct);
                    return;
                }
            }

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("👨‍🏫 Преподаватель", "reg:btn_teacher"),
                    InlineKeyboardButton.WithCallbackData("🧑‍🎓 Студент", "reg:btn_student")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("❌ Отмена", "reg:btn_cancel")
                }
            });

            await bot.SendMessage(
                chatId: chatId,
                text: "Выберите свой статус",
                replyMarkup: inlineKeyboard,
                cancellationToken: ct
            );
            return;
        }

        else if (msg.Text == "/verification")
        {
            using (ApplicationContext db = new ApplicationContext(options))
            {
                var user = db.Users.FirstOrDefault(u => u.TgId == fromUserId);

                if (user == null)
                {
                    await bot.SendMessage(chatId, "Требуется регистрация!", cancellationToken: ct);
                    return;
                }

                if (user.Isverified)
                {
                    await bot.SendMessage(chatId, "Ваш аккаунт уже верифицирован.", cancellationToken: ct);
                    return;
                }

                if (user is StudentDB)
                {
                    if (user.State != "WaitingForStudentCard")
                    {
                        user.State = "WaitingForStudentCard";
                        db.SaveChanges();
                        await bot.SendMessage(chatId, "Отправьте фото Вашего студенческого билета.", cancellationToken: ct);
                        return;
                    }
                    else
                    {
                        await bot.SendMessage(chatId, "Процесс верификации уже начат. Ожидание фотографии студенческого билета.", cancellationToken: ct);
                        return;
                    }
                }

                if (user is TeacherDB)
                {
                    await bot.SendMessage(chatId, "Для верификации обратитесь в администрацию.", cancellationToken: ct);
                    return;
                }
            }
        }

        else if (msg.Text == "/admin")
        {
            using (ApplicationContext db = new ApplicationContext(options))
            {
                var admin = db.Users.FirstOrDefault(a=>a.TgId == fromUserId);

                bool isAdmin = admin is AdminDB;

                if (admin == null || !isAdmin) return;

                if (isAdmin)
                {
                    if (admin.LastInlineMessageId != 0)
                    {
                        await bot.DeleteMessage(chatId, admin.LastInlineMessageId, cancellationToken: ct);
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

                else
                {
                    await bot.SendMessage(chatId, "Вы не являетесь администратором.", cancellationToken: ct);
                    return;
                }
            }

        }

        else if (msg.Text == "/teacher")
        {
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
                    InlineKeyboardButton.WithCallbackData("✏️ Изменить загруженные заметки","teacher:editNote"),
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🗑️ Удалить загруженные заметки","teacher:deleteNote"),
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:canсel")
                }
                });

                if(teacher.LastInlineMessageId != 0)
                {
                    await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct);
                    teacher.State = null;
                }

                var messageTodelete = await bot.SendMessage(chatId, "Для навигации по меню воспользуйтесь кнопками", replyMarkup: inlineKeyboard, cancellationToken: ct);

                teacher.LastInlineMessageId = messageTodelete.Id;

                db.SaveChanges();
            }
        }

        else if(msg.Text == "/student")
        {
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
                    await bot.DeleteMessage(chatId, student.LastInlineMessageId, cancellationToken: ct);
                    student.State = null;
                }

                var messageToDelete = await bot.SendMessage(chatId, "Отправьте свой вопрос. Он должен быть связан только с вашей учебой. \nПостарайтесь задать конретный вопрос.", replyMarkup: inlineKeyboard, cancellationToken: ct);

                student.State = "WaitingAskedQuestion";
                student.LastInlineMessageId = messageToDelete.Id;

                db.SaveChanges();
            }
        }

        else
        {
            using(ApplicationContext db = new ApplicationContext(options))
            {
                var user = db.Users.FirstOrDefault(u=>u.TgId == fromUserId);

                if (user == null)
                    return;

                if(user.State == "WaitingForUploadNote")
                {
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

                    AI aiService = new AI(config);
                    await aiService.GenerationNote(options, note.Id, bot);

                    await bot.SendMessage(chatId, "Заметка успешно добавлена!", cancellationToken: ct);
                    await bot.DeleteMessage(chatId, user.LastInlineMessageId, cancellationToken: ct);
                    return;
                }

                if(user.State == "WaitingForUploadNoteName")
                {
                    await bot.DeleteMessage(chatId, user.LastInlineMessageId);

                    var name=msg.Text;

                    // клава для подвтреждения корректности названия заметки
                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "teacher:confirmNoteName"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "teacher:rejectNoteName")
                        }
                    });
                    
                    var message = await bot.SendMessage(chatId, $"Заметка будет названа '{name}'", cancellationToken: ct, replyMarkup: inlineKeyboard);

                    (user as TeacherDB).NoteName = name;

                    user.LastInlineMessageId = message.Id;
                    db.SaveChanges();
                }

                if (user.State != null && user.State.StartsWith("WaitingSendNewNoteName:"))
                {
                    int noteNum = int.Parse(user.State.Split(':')[1]);

                    var teacher = user as TeacherDB;
                    var note = db.teacherNotes.FirstOrDefault(n=>n.Id == noteNum);

                    var inlineKeyboard = new InlineKeyboardMarkup(new[]
{
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Подтвердить", $"teacher:confirmNoteRename:{noteNum}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", $"teacher:rejectNoteRename:{noteNum}")
                        }
                    });

                    await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct);
                    var messageToDelete = await bot.SendMessage(chatId, $"Подтвердить переименование?\n<s>{note.NoteName}</s> ➡️ {msg.Text}", cancellationToken: ct, replyMarkup: inlineKeyboard, parseMode: ParseMode.Html);
                    teacher.LastInlineMessageId = messageToDelete.Id;
                    teacher.NoteName = msg.Text;

                    db.SaveChanges();
                    
                }

                if(user.State != null && user.State.StartsWith("WaitingSendNewNoteContent:"))
                {
                    var teacher = user as TeacherDB;
                    int noteNum = int.Parse(user.State.Split(':')[1]);
                    var note = db.teacherNotes.FirstOrDefault(n=>n.Id == noteNum);
                    string newContent = msg.Text;
                    InlineKeyboardMarkup inlineKeyboard = (new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Подтвердить",$"teacher:confirmNoteTextContent:{noteNum}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("❌ Отмена",$"teacher:rejectNoteContent:{noteNum}"),
                        }
                    }
                    );
                    var messageToDelete = await bot.SendMessage(chatId, $"Подтвердите изменение содержания файла.\n\nНовое содержание:<blockquote expandable>{newContent}</blockquote>",parseMode: ParseMode.Html, replyMarkup:inlineKeyboard, cancellationToken: ct);

                    teacher.LastInlineMessageId = messageToDelete.Id;

                    teacher.NoteName= msg.Text;

                    db.SaveChanges();
                }

                if(user.State == "WaitingAskedQuestion")
                {
                    StudentDB? student = user as StudentDB;
                    string group = student.Group;
                    int course = student.Course;
                    string name = student.Name;
                    string surname = student.Surname;
                    string question = msg.Text;

                    await bot.DeleteMessage(chatId, user.LastInlineMessageId);
                    var messageResponse = await bot.SendMessage(chatId, "Ищем ответ на Ваш вопрос... 🔎");

                    AI aiService = new AI(config);
                    string answer = await aiService.GenerationAnswer(options,group,course,name,surname,question);

                    user.LastInlineMessageId = 0;
                    user.State =null;
                    db.SaveChanges();

                    await bot.EditMessageText(chatId, messageResponse.Id, answer, cancellationToken: ct);
                } 
            }
        }
    }

    // Обработка отправки фото для верификации
    if (msg.Photo != null)
    {
        using (ApplicationContext db = new ApplicationContext(options))
        {
            var user = db.Students.FirstOrDefault(u => u.TgId == fromUserId);

            if (user == null)
                return;

            if (user.State == "WaitingForStudentCard")
            {
                // msg.Photo — это массив размеров одной фотографии. Последний элемент [^1] — максимальное качество
                user.StudentCardFileId = msg.Photo[^1].FileId;
                user.State = "AwaitingForVerification";
                db.SaveChanges();

                await bot.SendMessage(chatId, "Фото студенческого билета принято на проверку!", cancellationToken: ct);
                return;
            }

            
        }
    }

    if (msg.Document != null)
    {
        await bot.SendMessage(chatId, "загрузка файла", cancellationToken: ct);

        using (ApplicationContext db = new ApplicationContext(options))
        {
                var teacher = db.Teachers.FirstOrDefault(t => t.TgId == fromUserId);

                if (teacher == null)
                    return;

                // если файл не txt или docx
                if( !(msg.Document.FileName.EndsWith(".txt") || msg.Document.FileName.EndsWith(".docx")) )
                {
                     await bot.SendMessage(chatId, "Файл такого расширения не поддерживается! Повторите попытку!", cancellationToken: ct);

                     return;
                }

                if(teacher.State == "WaitingForUploadNote")
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

                    await bot.SendMessage(chatId, "Обработка загруженного файла... Время ожидания зависит от его содержания, пожалуйста, подождите.", cancellationToken: ct);


                AI aiService = new AI(config);
                    await aiService.GenerationNote(options, note.Id,bot);

                    await bot.SendMessage(chatId, "Заметка успешно добавлена!", cancellationToken: ct);

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
                    case "btn_cancel":
                        await bot.DeleteMessage(chatId, messageId, ct);
                        await bot.SendMessage(chatId, "Регистрация отменена", cancellationToken: ct);
                        return;

                    case "btn_teacher":
                        TeacherDB newTeacher = new TeacherDB();
                        newTeacher.TgId = query.From.Id;
                        newTeacher.TgUsername = query.From.Username ?? "AnonymousTeacher";
                        db.Users.Add(newTeacher);
                        db.SaveChanges();
                        await bot.DeleteMessage(chatId, messageId, ct);
                        await bot.SendMessage(chatId, "Начат процесс регистрации. Для верификации профиля обратитесь в учебную часть", cancellationToken: ct);
                        return;

                    case "btn_student":
                        StudentDB newStudent = new StudentDB();
                        newStudent.TgId = query.From.Id;
                        newStudent.TgUsername = query.From.Username ?? "AnonymousStudent";
                        newStudent.State = "WaitingForStudentCard";
                        db.Users.Add(newStudent);
                        db.SaveChanges();
                        await bot.DeleteMessage(chatId, messageId, ct);
                        await bot.SendMessage(chatId, "Начат процесс регистрации. Для верификации профиля отправьте фотографию Вашего студенческого билета", cancellationToken: ct);
                        return;
                }
            }
            break;

        case "admin":
            
            switch(command)
            {
                case "cancel":
                    await bot.DeleteMessage(chatId,messageId, ct);
                    return;

                case "startUserVerification":
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        if (waitingUsers.Count == 0)
                        {
                            await bot.EditMessageText(chatId,messageId, "Нет заявок, ожидающих верификации.", cancellationToken: ct);
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
                            caption: $"Верификация пользователя @{waitingUsers[index].TgUsername}",
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
                //отладка сделана
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
                                        Caption = $"Верифицация пользователя {waitingUsers[index].TgUsername}"
                                    },
                                    replyMarkup: singleButtonMarkup,
                                    cancellationToken: ct
                            );
                        }
                    }

                    return;

                //отладка сделана
                case "confirm":
                    long id = long.Parse(parts[2]);
                    using (ApplicationContext db = new ApplicationContext(options))
                    {
                        var user = db.Students.First(u => u.TgId == id);
                        user.Isverified = true;
                        user.State = "verified";
                        db.SaveChanges();

                        await bot.SendMessage(user.TgId, "Поздравляем! Ваша учетная запись верифицирована!",cancellationToken: ct);

                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        var singleBackButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                        InlineKeyboardButton.WithCallbackData("Выход", $"admin:cancel"),
                         });

                        if (waitingUsers.Count == 0)
                        {
                            await bot.DeleteMessage(chatId, messageId, cancellationToken: ct);

                            // 2. Отправляем чистое текстовое сообщение
                            await bot.SendMessage(
                                chatId: chatId,
                                text: "Все заявки рассмотрены!",
                                replyMarkup: singleBackButtonMarkup,
                                cancellationToken: ct
                            );
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
                                    // Исправлено: указан .UserId вместо целого объекта
                                    InlineKeyboardButton.WithCallbackData("✅ Принять", $"verify:confirm:{waitingUsers[index].TgId}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"verify:reject:{waitingUsers[index].TgId}")
                                },
                                new[]
                                {
                                    // Добавлено двоеточие для консистентного парсинга через Split(':')
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
                                    Caption = $"Верифицация пользователя {waitingUsers[index].TgUsername}"
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
                        var user = db.Students.First(u => u.TgId == id);
                        user.State = "Rejected";
                        db.SaveChanges();

                        await bot.SendMessage(user.TgId, "Упс... Ваша заявка на верификацию отклонена! Повторите попытку.", cancellationToken: ct);

                        var waitingUsers = db.Students.Where(u => u.State == "AwaitingForVerification").ToList();

                        var singleBackButtonMarkup = new InlineKeyboardMarkup(new[]
                        {
                        InlineKeyboardButton.WithCallbackData("Выход", $"admin:cancel"),
                         });

                        if (waitingUsers.Count == 0)
                        {
                            await bot.DeleteMessage(chatId, messageId, cancellationToken: ct);

                            // 2. Отправляем чистое текстовое сообщение
                            await bot.SendMessage(
                                chatId: chatId,
                                text: "Все заявки рассмотрены!",
                                replyMarkup: singleBackButtonMarkup,
                                cancellationToken: ct
                            );
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
                                    Caption = $"Верифицация пользователя {waitingUsers[index].TgUsername}"
                                },
                                replyMarkup: singleButtonMarkup,
                                cancellationToken: ct
                            );
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
                            InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:canсelUploadNote")
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

                        db.SaveChanges();

                        await bot.DeleteMessage(chatId, messageId);

                        await bot.SendMessage(chatId, "Загрузка заметки отменена.");
                    }
                        return;

                case "rejectNoteName":
                    using (var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);

                        if (teacher == null) return;

                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId);

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
                        var teaher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n=>n.Id==noteId);
                        if(teaher == null || note == null) return;

                        teaher.State = null;
                        await bot.DeleteMessage(chatId, teaher.LastInlineMessageId, cancellationToken: ct );

                        note.NoteName = teaher.NoteName!;
                        teaher.NoteName = null;

                        teaher.LastInlineMessageId = (await bot.SendMessage(chatId,"Заметка успешно переименована!",cancellationToken:ct )).MessageId;




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
                            text: $"Название: <b>{note.NoteName}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{note.Text}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {note.FileName}")}",
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

                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId);

                        if (teacherNotes.Count == 0)
                        {
                            await bot.SendMessage(chatId, "У Вас нет заметок 😞", cancellationToken: ct);
                            return;
                        }

                        var buttons = new List<InlineKeyboardButton[]>();

                        foreach (var note in teacherNotes)
                        {
                            // Создаем строку с одной кнопкой
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
                                InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:editNote")
                            }
                        }
                        );

                        var messageToDelete = await bot.EditMessageText
                            (
                            chatId: chatId,
                            messageId: teacher.LastInlineMessageId,
                            text: $"Название: <b>{note.NoteName}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{note.Text}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {note.FileName}")}", replyMarkup: inlineKeyboard,
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
                        var note = db.teacherNotes.FirstOrDefault(n=>n.TeacherTgId == query.From.Id);

                        if (teacher == null || note == null) return;

                        // текстовая заметка
                        if(string.IsNullOrEmpty(note.FileName))
                        {
                            await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct);

                            var inlineKeyboard = new InlineKeyboardMarkup(new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена", $"teacher:editNoteNum:{noteNum}")
                            });

                            var messageToDelete = await bot.SendMessage(chatId, "Пришлите новое содержание файла.", cancellationToken: ct, replyMarkup: inlineKeyboard); ;

                            teacher.LastInlineMessageId = messageToDelete.Id;
                            teacher.State = $"WaitingSendNewNoteContent:{noteNum}";

                            db.SaveChanges();
                        }
                        

                        //заметка с файлом

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
                            text: $"Название: <b>{note.NoteName}</b>\n\nДата создания: <b>{note.CreatedTime}</b>\n\n{(note.Text == null ? "" : $"Содержание:<blockquote expandable>{note.Text}</blockquote>\n\n")}{(note.FileName == null ? "" : $"Название файла: {note.FileName}")}", replyMarkup: inlineKeyboard,
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                            );

                        teacher.LastInlineMessageId = messageToDelete.Id;

                        db.SaveChanges();
                    }
                    return;

                case "confirmNoteTextContent":
                    using(var db = new ApplicationContext(options))
                    {
                        int noteNum = int.Parse(parts[2]);
                        var teacher = db.Teachers.FirstOrDefault(t => t.TgId == query.From.Id);
                        var note = db.teacherNotes.FirstOrDefault(n => n.Id == noteNum);

                        note.Text = teacher.NoteName;
                        note.CreatedTime = DateTime.Now;
                        teacher.NoteName = null;

                        var editMessage = await bot.SendMessage(chatId, "Изменения применяются...", cancellationToken: ct);

                        AI aiService = new AI(config);
                        await aiService.GenerationNote(options, note.Id, bot);

                        await bot.EditMessageText(chatId, editMessage.Id, "Содержимое заметки успешно изменено!", cancellationToken: ct);
                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct);
                        teacher.LastInlineMessageId = 0;

                        db.SaveChanges();
                    }
                    // остановился тут
                    return;

                case "deleteNote":

                    return;

                case "cancelDeleteNote":

                    return;

                case "canсel":
                    using(var db = new ApplicationContext(options))
                    {
                        var teacher = db.Teachers.FirstOrDefault(t=>t.TgId == query.From.Id);
                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, ct);
                        //db.SaveChanges();
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
                                InlineKeyboardButton.WithCallbackData("✏️ Изменить загруженные заметки","teacher:editNote"),
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🗑️ Удалить загруженные заметки","teacher:deleteNote"),
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("❌ Отмена","teacher:canсel")
                            }
                            });

                        await bot.DeleteMessage(chatId, teacher.LastInlineMessageId, cancellationToken: ct);

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
                        db.SaveChanges();
                    }
                    await bot.DeleteMessage(chatId, messageId, ct);
                    return;


            }
            return;
    }
}

Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
{
    var errorMessage = exception.InnerException?.Message ?? exception.Message;
    Console.WriteLine($"Ошибка ({source}): {errorMessage}");
    return Task.CompletedTask;
}